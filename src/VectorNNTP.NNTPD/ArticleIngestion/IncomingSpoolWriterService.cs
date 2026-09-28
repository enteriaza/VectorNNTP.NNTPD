using System.Buffers;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Diagnostics;
using VectorNNTP.NNTPD.Newsgroups;

namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Background application service that drains CanonicalV1 queued articles.
/// </summary>
/// <remarks>
/// <para>
/// The queue guarantee is a CanonicalV1 <c>ArticleRecord</c>. This worker does
/// not destuff, parse, or construct records. After dequeue it publishes a compact
/// OverviewDB protobuf message to <c>overviewdb.queue</c> and waits for a RabbitMQ
/// publisher confirmation. On confirm it classifies <c>+</c>/<c>j</c> and emits
/// those events through <see cref="INewsLogWriter"/> (production: Serilog), then
/// continues to the existing <see cref="IIncomingArticlePersister"/> (/dev/null
/// no-op). A failed confirm or publish is treated as incomplete: the article is
/// requeued through <see cref="IArticleIngestionQueue.EnqueueAsync"/>. Rejected
/// articles never reach this worker; <c>-</c> is emitted at the protocol decision.
/// Moderated POST (<c>m</c>) is emitted at the moderation-success decision and
/// never enters the queue.
/// </para>
/// <para>
/// A news-log failure is reported through
/// <see cref="SpoolLogMessages.NewsLogFailed"/> and does not re-admit, re-queue,
/// or emit a second NNTP response. The article still proceeds to the persister
/// after a successful OverviewDB handoff. The worker never calls OverviewDB
/// over RPC, HTTP, gRPC, or a database connection.
/// </para>
/// </remarks>
public sealed class IncomingSpoolWriterService : IApplicationService
{
    private readonly IArticleIngestionQueue _queue;
    private readonly IIncomingArticlePersister _persister;
    private readonly INewsLogWriter _newsLog;
    private readonly INewsgroupCatalogue? _catalogue;
    private readonly IOverviewDbHandoffPublisher _overviewHandoff;
    private readonly IOptions<NntpdOptions> _options;
    private readonly ILogger<IncomingSpoolWriterService> _logger;
    private readonly IFeedDiagnostics _feedDiagnostics;
    private readonly IngestionPipelineMetrics? _pipeline;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _runCts = new();
    private Task? _execution;
    private int _started;

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
        IngestionPipelineMetrics? pipelineMetrics = null)
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
        _catalogue = catalogue;
        _time = timeProvider ?? TimeProvider.System;
        _overviewHandoff = overviewHandoff ?? NullOverviewDbHandoffPublisher.Instance;
        _pipeline = pipelineMetrics;
    }

    /// <inheritdoc />
    public string Name => "IncomingSpoolWriter";

    /// <inheritdoc />
    public Task? Execution => _execution;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        _execution = Task.Run(() => RunAsync(_runCts.Token), CancellationToken.None);
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
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        SpoolLogMessages.WriterStarted(
            _logger,
            _queue.MemoryLimitBytes,
            _queue.MaxArticleBytes,
            _options.Value.ArticleIngestion?.IncomingDirectory
            ?? ArticleIngestionOptions.DefaultIncomingDirectory);

        while (true)
        {
            InboundArticle? article;
            var idleStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                article = await _queue.DequeueAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _pipeline?.AddIdleTicks(System.Diagnostics.Stopwatch.GetTimestamp() - idleStart);
                _pipeline?.RecordDequeueWait(idleStart);
                break;
            }

            _pipeline?.AddIdleTicks(System.Diagnostics.Stopwatch.GetTimestamp() - idleStart);
            _pipeline?.RecordDequeueWait(idleStart);

            if (article is null)
            {
                break;
            }

            var itemStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _pipeline?.ObserveQueueDepth(_queue.Count + 1);
            var persisted = false;
            var overviewAccepted = false;
            _feedDiagnostics.BeginSpoolWork();
            try
            {
                await PublishOverviewAsync(article, itemStart, CancellationToken.None).ConfigureAwait(false);
                overviewAccepted = true;
                var newsStart = System.Diagnostics.Stopwatch.GetTimestamp();
                WriteNewsLog(article);
                _pipeline?.RecordNews(newsStart);
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
                _pipeline?.AddBusyTicks(System.Diagnostics.Stopwatch.GetTimestamp() - itemStart);
                _pipeline?.RecordWorkerItem(itemStart);
                _feedDiagnostics.EndSpoolWork(article.Payload.Length, persisted);
            }

            if (cancellationToken.IsCancellationRequested && _queue.Count == 0)
            {
                break;
            }
        }

        SpoolLogMessages.WriterStopped(_logger);
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
