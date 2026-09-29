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
/// a dynamically sized <see cref="IngestionWorkerPool"/> and hands OverviewDB
/// payloads to an independent publisher stage.
/// </summary>
/// <remarks>
/// <para>
/// Article workers encode OverviewArticleV1, enqueue the owned bytes onto a bounded
/// in-process <see cref="IOverviewDbWorkQueue"/>, then write news / Path-survey /
/// persist. They do <b>not</b> await RabbitMQ publication or publisher confirmation.
/// Successful enqueue is not durable RabbitMQ handoff; broker confirmation remains
/// owned by <see cref="OverviewDbPublisherPool"/> via asynchronous outstanding
/// confirms on <see cref="IOverviewDbHandoffPublisher"/>.
/// </para>
/// <para>
/// OverviewDB publisher workers scale independently from article workers based on
/// OverviewDB work-queue pressure. Publisher confirms, mandatory publishing,
/// persistent delivery, and the 2-second AMQP expiration are unchanged.
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
    private readonly CancellationTokenSource _articleRunCts = new();
    private readonly CancellationTokenSource _overviewRunCts = new();
    private Task? _execution;
    private int _started;
    private IngestionWorkerPool? _pool;
    private OverviewDbWorkQueue? _overviewWorkQueue;
    private OverviewDbPublisherPool? _overviewPublisherPool;
    private Task? _overviewPublisherExecution;

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

    /// <summary>Gets the active article worker pool (tests).</summary>
    internal IngestionWorkerPool? Pool => _pool;

    /// <summary>Gets the OverviewDB work queue (tests).</summary>
    internal IOverviewDbWorkQueue? OverviewWorkQueue => _overviewWorkQueue;

    /// <summary>Gets the OverviewDB publisher pool (tests).</summary>
    internal OverviewDbPublisherPool? OverviewPublisherPool => _overviewPublisherPool;

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

        _overviewWorkQueue = new OverviewDbWorkQueue(ingestion.OverviewDbWorkQueueMemoryLimit);
        _overviewPublisherPool = new OverviewDbPublisherPool(
            _overviewWorkQueue,
            _overviewHandoff,
            ingestion,
            _logger,
            _pipeline,
            _time);
        _overviewPublisherExecution = Task.Run(
            () => _overviewPublisherPool.RunAsync(_overviewRunCts.Token),
            CancellationToken.None);

        _pool = new IngestionWorkerPool(
            _queue,
            ProcessArticleAsync,
            ingestion,
            _logger,
            _pipeline,
            _samplePressure,
            _time);
        _execution = Task.Run(() => _pool.RunAsync(_articleRunCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 1) Stop admitting article ingress and drain article workers (they may still
        //    enqueue OverviewDB work while draining). Publisher pool keeps running.
        _queue.Complete();
        await _articleRunCts.CancelAsync().ConfigureAwait(false);

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

        // 2) Complete OverviewDB work queue and drain publisher workers for a bounded period.
        //    Outstanding OverviewDB confirms may be abandoned after the configured timeout.
        _overviewWorkQueue?.Complete();
        await _overviewRunCts.CancelAsync().ConfigureAwait(false);
        var overviewExecution = _overviewPublisherExecution;
        if (overviewExecution is not null)
        {
            var shutdownSeconds = Math.Max(
                1,
                (_options.Value.ArticleIngestion ?? new ArticleIngestionOptions())
                    .OverviewDbPublisherShutdownSeconds);
            using var overviewDrainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overviewDrainCts.CancelAfter(TimeSpan.FromSeconds(shutdownSeconds));
            try
            {
                await overviewExecution.WaitAsync(overviewDrainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SpoolLogMessages.WriterStoppedWithError(_logger, ex);
            }
        }

        _overviewHandoff.AbandonOutstanding();

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
            await EnqueueOverviewWorkAsync(article, itemStart, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Encodes OverviewArticleV1 and enqueues it onto the in-process OverviewDB work
    /// queue. Does not call RabbitMQ.
    /// </summary>
    private async Task EnqueueOverviewWorkAsync(
        InboundArticle article,
        long itemStart,
        CancellationToken cancellationToken)
    {
        var workQueue = _overviewWorkQueue
            ?? throw new InvalidOperationException("OverviewDB work queue is not started.");

        var max = OverviewArticleV1Codec.GetMaxEncodedSize(article.Record);
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(max, 1));
        try
        {
            var encodeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var written = OverviewArticleV1Codec.Encode(article.Record, rented);
            _pipeline?.RecordEncode(encodeStart);
            _pipeline?.RecordToPublishStart(itemStart);

            var owned = new byte[written];
            rented.AsSpan(0, written).CopyTo(owned);
            var item = new OverviewDbWorkItem(owned, article.MessageId);
            var result = await workQueue.EnqueueAsync(item, cancellationToken).ConfigureAwait(false);
            if (result != ArticleEnqueueResult.Accepted)
            {
                throw new InvalidOperationException(
                    $"OverviewDB work queue rejected handoff enqueue ({result}).");
            }
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
