using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Diagnostics;

namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Background application service that drains the ingestion queue into the incoming spool.
/// </summary>
/// <remarks>
/// Start order: after listeners may accept connections is acceptable; the queue is a singleton
/// that buffers until this service is running. Stop completes the queue writer and drains
/// already-accepted articles before exiting. IHAVE items are destuffed once
/// under <c>ArticleIngestion:MaxArticleBytes</c>. POST items are destuffed once
/// using the queued stuffed payload length (POST receive already enforced
/// <c>Nntpd:MaxArticleSize</c> on destuffed client bytes). TAKETHIS payloads
/// are unchanged.
/// Production starts exactly one drain loop. The queue supports concurrent
/// <see cref="IArticleIngestionQueue.DequeueAsync"/> callers; this service does not
/// create additional consumers.
/// </remarks>
public sealed class IncomingSpoolWriterService : IApplicationService
{
    private readonly IArticleIngestionQueue _queue;
    private readonly IIncomingArticlePersister _persister;
    private readonly IOptions<NntpdOptions> _options;
    private readonly ILogger<IncomingSpoolWriterService> _logger;
    private readonly IFeedDiagnostics _feedDiagnostics;
    private readonly CancellationTokenSource _runCts = new();
    private Task? _execution;
    private int _started;

    /// <summary>Initializes a new instance of the <see cref="IncomingSpoolWriterService"/> class.</summary>
    public IncomingSpoolWriterService(
        IArticleIngestionQueue queue,
        IIncomingArticlePersister persister,
        IOptions<NntpdOptions> options,
        ILogger<IncomingSpoolWriterService> logger,
        IFeedDiagnostics? feedDiagnostics = null)
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
        // Stop accepting new articles; drain whatever is already queued.
        _queue.Complete();
        await _runCts.CancelAsync().ConfigureAwait(false);

        var execution = _execution;
        if (execution is null)
        {
            return;
        }

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

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        SpoolLogMessages.WriterStarted(
            _logger,
            _queue.MemoryLimitBytes,
            _queue.MaxArticleBytes,
            _options.Value.ArticleIngestion?.IncomingDirectory
            ?? ArticleIngestionOptions.DefaultIncomingDirectory);

        // Drain until the queue is completed and empty. Cancellation during stop still attempts
        // cooperative exit after Complete(); WaitToReadAsync will return false once drained.
        while (true)
        {
            InboundArticle? article;
            try
            {
                article = await _queue.DequeueAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (article is null)
            {
                break;
            }

            var persisted = false;
            _feedDiagnostics.BeginSpoolWork();
            try
            {
                if (article.Producer is InboundArticleProducer.IHave or InboundArticleProducer.Post)
                {
                    article = IhaveArticleInterpreter.Interpret(article, DestuffLimit(article));
                }

                await _persister.PersistAsync(article, CancellationToken.None).ConfigureAwait(false);
                persisted = true;
            }
            catch (Exception ex)
            {
                // Already accepted (239). Log and continue — do not poison the drain loop.
                SpoolLogMessages.PersistFailed(
                    _logger,
                    ex,
                    article.MessageId,
                    article.Payload.Length);
            }
            finally
            {
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
    /// Destuff ceiling for one queued item. IHAVE uses
    /// <c>ArticleIngestion:MaxArticleBytes</c>. POST uses the queued stuffed
    /// payload length, which is an upper bound on destuffed size and already
    /// includes server-owned headers written after <c>Nntpd:MaxArticleSize</c>
    /// was enforced on the client destuffed article.
    /// </summary>
    internal int DestuffLimit(InboundArticle article)
    {
        ArgumentNullException.ThrowIfNull(article);
        if (article.Producer == InboundArticleProducer.Post)
        {
            return Math.Max(1, article.Payload.Length);
        }

        return _queue.MaxArticleBytes;
    }
}
