using System.Buffers;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Articles.OverviewDb;
using VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Diagnostics;
using VectorNNTP.NNTPD.Newsgroups;

namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Background application service that drains CanonicalV1 queued articles through
/// a dynamically sized <see cref="IngestionWorkerPool"/>.
/// </summary>
/// <remarks>
/// <para>
/// The queue guarantee is a CanonicalV1 <c>ArticleRecord</c>. Workers do not
/// destuff, parse, or construct records. After dequeue each worker publishes a
/// compact OverviewDB protobuf message to <c>overviewdb.queue</c> and waits for a
/// RabbitMQ publisher confirmation on a leased confirm-enabled channel. On confirm
/// it classifies <c>+</c>/<c>j</c> and emits those events through
/// <see cref="INewsLogWriter"/> (production: Serilog), writes the canonical
/// <c>ArticleRecord.Path</c> to the dedicated Path-survey stream
/// (<see cref="IPathSurveyWriter"/>), then continues to the existing
/// <see cref="IIncomingArticlePersister"/> (/dev/null no-op). A failed confirm or
/// publish is treated as incomplete: the article is requeued through
/// <see cref="IArticleIngestionQueue.EnqueueAsync"/>. Rejected articles never
/// reach this service; <c>-</c> is emitted at the protocol decision. Moderated
/// POST (<c>m</c>) is emitted at the moderation-success decision and never enters
/// the queue. Path-survey observations are not written for articles that never
/// become a CanonicalV1 queued record.
/// </para>
/// <para>
/// Multiple workers may be in-flight concurrently. Outstanding OverviewDB
/// publishes are bounded by <see cref="ArticleIngestionOptions.MaxPublishConcurrency"/>
/// via leased RabbitMQ publish channels. Worker count scales between
/// <see cref="ArticleIngestionOptions.MinWorkers"/> and
/// <see cref="ArticleIngestionOptions.MaxWorkers"/> from sustained queue pressure.
/// A news-log or Path-survey failure is reported through
/// <see cref="SpoolLogMessages.NewsLogFailed"/> /
/// <see cref="SpoolLogMessages.PathSurveyFailed"/> and does not re-admit,
/// re-queue, or emit a second NNTP response. The article still proceeds to the
/// persister after a successful OverviewDB handoff. The worker never calls
/// OverviewDB over RPC, HTTP, gRPC, or a database connection.
/// </para>
/// </remarks>
public sealed class IncomingSpoolWriterService : IApplicationService
{
    private readonly IArticleIngestionQueue _queue;
    private readonly IIncomingArticlePersister _persister;
    private readonly INewsLogWriter _newsLog;
    private readonly IPathSurveyWriter _pathSurvey;
    private readonly INewsgroupCatalogue? _catalogue;
    private readonly IOverviewDbHandoffPublisher _overviewHandoff;
    private readonly IOptions<NntpdOptions> _options;
    private readonly ILogger<IncomingSpoolWriterService> _logger;
    private readonly IFeedDiagnostics _feedDiagnostics;
    private readonly IngestionPipelineMetrics? _pipeline;
    private readonly TimeProvider _time;
    private readonly Func<IngestionPressureSnapshot>? _samplePressure;
    private readonly CancellationTokenSource _runCts = new();
    private Task? _execution;
    private int _started;
    private IngestionWorkerPool? _pool;

    /// <summary>Initializes a new instance of the <see cref="IncomingSpoolWriterService"/> class.</summary>
    public IncomingSpoolWriterService(
        IArticleIngestionQueue queue,
        IIncomingArticlePersister persister,
        IOptions<NntpdOptions> options,
        ILogger<IncomingSpoolWriterService> logger,
        IFeedDiagnostics? feedDiagnostics = null,
        INewsLogWriter? newsLog = null,
        INewsgroupCatalogue? catalogue = null,
        TimeProvider? timeProvider = null,
        IOverviewDbHandoffPublisher? overviewHandoff = null,
        IngestionPipelineMetrics? pipelineMetrics = null,
        IPathSurveyWriter? pathSurvey = null,
        Func<IngestionPressureSnapshot>? samplePressure = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(persister);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _queue = queue;
        _persister = persister;
        _options = options;
        _logger = logger;
        _feedDiagnostics = feedDiagnostics ?? NullFeedDiagnostics.Instance;
        _newsLog = newsLog ?? NullNewsLogWriter.Instance;
        _pathSurvey = pathSurvey ?? NullPathSurveyWriter.Instance;
        _catalogue = catalogue;
        _time = timeProvider ?? TimeProvider.System;
        _overviewHandoff = overviewHandoff ?? NullOverviewDbHandoffPublisher.Instance;
        _pipeline = pipelineMetrics;
        _samplePressure = samplePressure;
    }

    /// <inheritdoc />
    public string Name => "IncomingSpoolWriter";

    /// <inheritdoc />
    public Task? Execution => _execution;

    /// <summary>Gets the active worker pool (tests).</summary>
    internal IngestionWorkerPool? Pool => _pool;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        var ingestion = _options.Value.ArticleIngestion ?? new ArticleIngestionOptions();
        SpoolLogMessages.WriterStarted(
            _logger,
            _queue.MemoryLimitBytes,
            _queue.MaxArticleBytes,
            ingestion.IncomingDirectory
            ?? ArticleIngestionOptions.DefaultIncomingDirectory);

        _pool = new IngestionWorkerPool(
            _queue,
            ProcessArticleAsync,
            ingestion,
            _logger,
            _pipeline,
            _samplePressure,
            _time);
        _execution = Task.Run(() => _pool.RunAsync(_runCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Complete();
        await _runCts.CancelAsync().ConfigureAwait(false);

        var execution = _execution;
        if (execution is not null)
        {
            try
            {
                await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SpoolLogMessages.StopCanceledWithBufferedArticles(_logger, _queue.Count);
            }
            catch (Exception ex)
            {
                SpoolLogMessages.WriterStoppedWithError(_logger, ex);
            }
        }

        try
        {
            _newsLog.Flush();
        }
        catch (Exception ex)
        {
            SpoolLogMessages.NewsLogFlushFailed(_logger, ex);
        }

        try
        {
            _pathSurvey.Flush();
        }
        catch (Exception ex)
        {
            SpoolLogMessages.PathSurveyFlushFailed(_logger, ex);
        }

        SpoolLogMessages.WriterStopped(_logger);
    }

    private async Task ProcessArticleAsync(
        InboundArticle article,
        long itemStart,
        CancellationToken cancellationToken)
    {
        var persisted = false;
        var overviewAccepted = false;
        _feedDiagnostics.BeginSpoolWork();
        try
        {
            await PublishOverviewAsync(article, itemStart, cancellationToken).ConfigureAwait(false);
            overviewAccepted = true;
            var newsStart = System.Diagnostics.Stopwatch.GetTimestamp();
            WriteNewsLog(article);
            _pipeline?.RecordNews(newsStart);
            WritePathSurvey(article);
            var persistStart = System.Diagnostics.Stopwatch.GetTimestamp();
            await _persister.PersistAsync(article, CancellationToken.None).ConfigureAwait(false);
            _pipeline?.RecordPersist(persistStart);
            persisted = true;
        }
        catch (Exception ex)
        {
            if (!overviewAccepted)
            {
                OverviewDbHandoffLogMessages.PublishFailed(
                    _logger,
                    ex,
                    article.MessageId,
                    article.Payload.Length);
                await RequeueAsync(article).ConfigureAwait(false);
            }
            else
            {
                SpoolLogMessages.PersistFailed(
                    _logger,
                    ex,
                    article.MessageId,
                    article.Payload.Length);
            }
        }
        finally
        {
            _feedDiagnostics.EndSpoolWork(article.Payload.Length, persisted);
        }
    }

    /// <summary>
    /// Writes the post-queue INN <c>news</c> event. Failures are logged and
    /// swallowed so the already-accepted article still reaches the persister.
    /// </summary>
    private void WriteNewsLog(InboundArticle article)
    {
        try
        {
            var transit = _options.Value.Transit ?? new TransitOptions();
            if (!IngressNewsDisposition.TryCreateEvent(
                    article,
                    transit,
                    _catalogue,
                    _time.GetLocalNow(),
                    out var evt))
            {
                return;
            }

            _newsLog.Write(in evt);
        }
        catch (Exception ex)
        {
            SpoolLogMessages.NewsLogFailed(_logger, ex, article.MessageId);
        }
    }

    /// <summary>
    /// Writes the canonical Path-survey observation. Failures are logged and
    /// swallowed so the already-accepted article still reaches the persister.
    /// </summary>
    private void WritePathSurvey(InboundArticle article)
    {
        try
        {
            _pathSurvey.Write(article.Record.Path);
        }
        catch (Exception ex)
        {
            SpoolLogMessages.PathSurveyFailed(_logger, ex, article.MessageId);
        }
    }

    private async Task PublishOverviewAsync(
        InboundArticle article,
        long itemStart,
        CancellationToken cancellationToken)
    {
        var max = OverviewArticleV1Codec.GetMaxEncodedSize(article.Record);
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(max, 1));
        try
        {
            var encodeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var written = OverviewArticleV1Codec.Encode(article.Record, rented);
            _pipeline?.RecordEncode(encodeStart);
            _pipeline?.RecordToPublishStart(itemStart);
            await _overviewHandoff
                .PublishConfirmedAsync(rented.AsMemory(0, written), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task RequeueAsync(InboundArticle article)
    {
        var result = await _queue.EnqueueAsync(article, CancellationToken.None).ConfigureAwait(false);
        if (result == ArticleEnqueueResult.Accepted)
        {
            OverviewDbHandoffLogMessages.Requeued(_logger, article.MessageId);
            return;
        }

        OverviewDbHandoffLogMessages.RequeueUnavailable(_logger, article.MessageId);
    }
}
