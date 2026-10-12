using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Core;

namespace VectorNNTP.StorageServer.Ingress;

/// <summary>
/// The single consumer of <see cref="IAcceptedArticleQueue"/>.
/// </summary>
/// <remarks>
/// Articles are processed one at a time on this service's execution task. A per-article failure
/// is logged and the next article is read. Queue completion and shutdown cancellation both end
/// the loop. The loop does not poll and does not start another consumer.
/// </remarks>
internal sealed class AcceptedArticleWorker : IApplicationService
{
    private readonly IAcceptedArticleQueue _queue;
    private readonly IAcceptedArticleProcessor _processor;
    private readonly ILogger<AcceptedArticleWorker> _logger;
    private readonly CancellationTokenSource _runCts = new();
    private Task? _execution;
    private int _started;

    /// <summary>Initializes the worker.</summary>
    /// <param name="queue">The shared accepted-article queue.</param>
    /// <param name="processor">Path manipulation and discard.</param>
    /// <param name="logger">Lifecycle and per-article failure logger.</param>
    public AcceptedArticleWorker(
        IAcceptedArticleQueue queue,
        IAcceptedArticleProcessor processor,
        ILogger<AcceptedArticleWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(logger);
        _queue = queue;
        _processor = processor;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "AcceptedArticleWorker";

    /// <inheritdoc />
    public Task? Execution => _execution;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        _processor.EnsureReady();
        AcceptedArticleWorkerLogMessages.Started(_logger);
        _execution = RunAsync(_runCts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
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
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                AcceptedArticleDelivery delivery;
                try
                {
                    delivery = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!delivery.HasArticle)
                {
                    break;
                }

                try
                {
                    _processor.TryProcess(delivery.Article);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AcceptedArticleWorkerLogMessages.ProcessingFailed(
                        _logger,
                        ex,
                        delivery.Article.ArtId.ToLowerHexString());
                }
            }
        }
        finally
        {
            AcceptedArticleWorkerLogMessages.Stopped(_logger);
        }
    }
}
