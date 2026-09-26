using System.Text;
using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Session;

namespace VectorNNTP.NNTPD.RabbitMq.ArticleWork;

/// <summary>
/// Orchestrates storage-first article-work RPC with a 500ms provider fan-out grace,
/// first-Success-wins completion, and aggregate lookup deadlines.
/// </summary>
/// <remarks>
/// The 500ms value is only the storage-to-provider grace. Responses are processed as
/// soon as they arrive. The only aggregate-terminal source outcome is
/// <see cref="ArticleWorkOutcome.Success"/>. In-process correlations are removed as
/// soon as the lookup completes. Outstanding provider publications are observed, not
/// awaited. RabbitMQ message TTL is a separate broker concern.
/// </remarks>
internal sealed class ArticleWorkRpcClient : IArticleWorkRpcClient
{
    private static readonly ArticleWorkRpcDestination StorageDestination = new(
        StorageArticleRetrievalTopology.Definition.ExchangeName,
        StorageArticleRetrievalTopology.Definition.RoutingKey,
        StorageArticleRetrievalTopology.Backbone);

    private static readonly ArticleWorkRpcDestination[] ProviderDestinations =
        BackfillArticleRetrievalTopology.Definitions
            .Select(static definition => new ArticleWorkRpcDestination(
                definition.ExchangeName,
                definition.RoutingKey,
                definition.Provider))
            .ToArray();

    private readonly IArticleWorkRpcPublisher _publisher;
    private readonly ArticleWorkRpcResponseRouter _router;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Func<long> _currentGeneration;

    /// <summary>Initializes a new orchestrator.</summary>
    internal ArticleWorkRpcClient(
        IArticleWorkRpcPublisher publisher,
        ArticleWorkRpcResponseRouter router,
        TimeProvider timeProvider,
        ILogger logger,
        Func<long>? currentGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _publisher = publisher;
        _router = router;
        _timeProvider = timeProvider;
        _logger = logger;
        _currentGeneration = currentGeneration ?? (() => 0);
    }

    /// <inheritdoc />
    public async Task<ArticleWorkRpcResult> LookupByMessageIdAsync(
        ReadOnlyMemory<byte> messageId,
        CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetUtcNow();
        var decoded = DecodeMessageId(messageId);
        var requestId = Guid.NewGuid();
        var operation = new ArticleWorkLookupOperation(requestId, decoded);

        using var safetyCts = new CancellationTokenSource(ArticleWorkRpcTiming.AbsoluteLifetime, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, safetyCts.Token);
        var lookupToken = linked.Token;

        try
        {
            var storagePublish = PublishAsync(
                operation,
                StorageDestination,
                lookupToken,
                skipIfCompleted: false);
            var graceWait = WaitUntilAsync(
                operation,
                started + ArticleWorkRpcTiming.StorageGrace,
                lookupToken);

            await storagePublish.ConfigureAwait(false);
            lookupToken.ThrowIfCancellationRequested();
            if (TryReturnCompleted(operation, started, out var afterStorage))
            {
                return afterStorage;
            }

            await graceWait.ConfigureAwait(false);
            lookupToken.ThrowIfCancellationRequested();
            if (TryReturnCompleted(operation, started, out var afterGrace))
            {
                return afterGrace;
            }

            StartProviderFanOut(operation, lookupToken);
            await WaitUntilAsync(
                    operation,
                    started + ArticleWorkRpcTiming.LookupDeadline,
                    lookupToken)
                .ConfigureAwait(false);

            if (TryReturnCompleted(operation, started, out var completed))
            {
                return completed;
            }

            var notFound = ArticleWorkRpcResult.NotFound(
                requestId,
                decoded,
                "Article lookup deadline elapsed");
            CompleteLocally(operation, notFound, started);
            return notFound;
        }
        catch (OperationCanceledException) when (safetyCts.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            var expired = ArticleWorkRpcResult.NotFound(
                requestId,
                decoded,
                "Article-work RPC absolute lifetime elapsed");
            CompleteLocally(operation, expired, started);
            return expired;
        }
        finally
        {
            _router.UnregisterAll(operation);
        }
    }

    private void StartProviderFanOut(
        ArticleWorkLookupOperation operation,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < ProviderDestinations.Length; i++)
        {
            ObservePublication(PublishAsync(
                operation,
                ProviderDestinations[i],
                cancellationToken,
                skipIfCompleted: true));
        }
    }

    private static void ObservePublication(Task publication)
    {
        _ = publication.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task PublishAsync(
        ArticleWorkLookupOperation operation,
        ArticleWorkRpcDestination destination,
        CancellationToken cancellationToken,
        bool skipIfCompleted)
    {
        if (skipIfCompleted && operation.IsCompleted)
        {
            return;
        }

        var correlationId = Guid.NewGuid().ToString("D");
        var request = new ArticleWorkRequest(
            ArticleWorkWireProtocol.CurrentVersion,
            operation.RequestId,
            operation.MessageId,
            destination.Backbone);
        var body = ArticleWorkWireProtocol.SerializeRequestV1(request);
        var generation = _currentGeneration();
        if (!_router.TryRegister(correlationId, operation, destination.Exchange, generation))
        {
            return;
        }

        try
        {
            await _publisher
                .PublishAsync(
                    destination.Exchange,
                    destination.RoutingKey,
                    operation.RequestId,
                    correlationId,
                    body,
                    cancellationToken)
                .ConfigureAwait(false);
            if (operation.IsCompleted)
            {
                _router.Unregister(correlationId);
                return;
            }

            ArticleWorkRpcLogMessages.Published(
                _logger,
                operation.RequestId,
                correlationId,
                operation.MessageId,
                destination.Exchange,
                generation);
        }
        catch (OperationCanceledException)
        {
            _router.Unregister(correlationId);
            if (operation.IsCompleted)
            {
                return;
            }

            throw;
        }
        catch (Exception ex)
        {
            _router.Unregister(correlationId);
            if (operation.IsCompleted)
            {
                return;
            }

            ArticleWorkRpcLogMessages.PublishFailed(
                _logger,
                ex,
                operation.RequestId,
                correlationId,
                operation.MessageId,
                destination.Exchange,
                generation);
        }
    }

    private async Task WaitUntilAsync(
        ArticleWorkLookupOperation operation,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var remaining = deadline - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero || operation.IsCompleted)
        {
            return;
        }

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(remaining, _timeProvider, delayCts.Token);
        var completed = await Task.WhenAny(operation.Completion, delay).ConfigureAwait(false);
        if (completed == operation.Completion)
        {
            await delayCts.CancelAsync().ConfigureAwait(false);
        }
        else
        {
            await delay.ConfigureAwait(false);
        }
    }

    private bool TryReturnCompleted(
        ArticleWorkLookupOperation operation,
        DateTimeOffset started,
        out ArticleWorkRpcResult result)
    {
        if (operation.TryGetResult(out result))
        {
            _router.UnregisterAll(operation);
            LogCompleted(result, started);
            return true;
        }

        result = default!;
        return false;
    }

    private void CompleteLocally(
        ArticleWorkLookupOperation operation,
        ArticleWorkRpcResult result,
        DateTimeOffset started)
    {
        operation.TryComplete(result);
        _router.UnregisterAll(operation);
        LogCompleted(result, started);
    }

    private void LogCompleted(ArticleWorkRpcResult result, DateTimeOffset started)
    {
        var elapsedMs = (long)Math.Max(0, (_timeProvider.GetUtcNow() - started).TotalMilliseconds);
        ArticleWorkRpcLogMessages.Completed(
            _logger,
            result.RequestId,
            result.MessageId,
            result.Outcome.ToString(),
            result.SourceExchange,
            elapsedMs);
    }

    /// <summary>
    /// Converts command-line Message-ID bytes to the JSON string at the RPC application boundary.
    /// NNTP Message-IDs are ASCII; this is not a protocol-data-plane default representation.
    /// </summary>
    internal static string DecodeMessageId(ReadOnlyMemory<byte> messageId)
    {
        if (messageId.IsEmpty || !NntpMessageId.IsWellFormed(messageId.Span))
        {
            throw new ArgumentException("Article-work RPC requires a well-formed Message-ID.", nameof(messageId));
        }

        return Encoding.ASCII.GetString(messageId.Span);
    }

    private readonly record struct ArticleWorkRpcDestination(string Exchange, string RoutingKey, string Backbone);
}
