using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Background application service that drains the ingestion queue into the incoming spool.
/// </summary>
/// <remarks>
/// Start order: after listeners may accept connections is acceptable; the queue is a singleton
/// that buffers until this service is running. Stop completes the queue writer and drains
/// already-accepted articles before exiting.
/// </remarks>
public sealed class IncomingSpoolWriterService : IApplicationService
{
    private readonly IArticleIngestionQueue _queue;
    private readonly IIncomingArticlePersister _persister;
    private readonly IOptions<NntpdOptions> _options;
    private readonly ILogger<IncomingSpoolWriterService> _logger;
    private readonly CancellationTokenSource _runCts = new();
    private Task? _execution;
    private int _started;

    /// <summary>Initializes a new instance of the <see cref="IncomingSpoolWriterService"/> class.</summary>
    public IncomingSpoolWriterService(
        IArticleIngestionQueue queue,
        IIncomingArticlePersister persister,
        IOptions<NntpdOptions> options,
        ILogger<IncomingSpoolWriterService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(persister);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _queue = queue;
        _persister = persister;
        _options = options;
        _logger = logger;
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
            _logger.LogWarning(
                "Incoming spool writer stop canceled with {Queued} article(s) still buffered",
                _queue.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Incoming spool writer stopped with an error");
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Incoming spool writer started (capacity {Capacity}, max article {MaxBytes} bytes, dir {Dir})",
            _queue.Capacity,
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

            try
            {
                await _persister.PersistAsync(article, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Already accepted (239). Log and continue — do not poison the drain loop.
                _logger.LogError(
                    ex,
                    "Failed to persist incoming article {MessageId} ({Bytes} bytes)",
                    article.MessageId,
                    article.Payload.Length);
            }

            if (cancellationToken.IsCancellationRequested && _queue.Count == 0)
            {
                break;
            }
        }

        _logger.LogInformation("Incoming spool writer stopped");
    }
}
