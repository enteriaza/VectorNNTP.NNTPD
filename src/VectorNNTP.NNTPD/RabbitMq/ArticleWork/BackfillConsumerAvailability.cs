using System.Collections.Immutable;

namespace VectorNNTP.NNTPD.RabbitMq.ArticleWork;

/// <summary>
/// Cached BackFiller ArticleWork consumer counts from RabbitMQ passive queue declares.
/// </summary>
/// <remarks>
/// Refreshes on a fixed interval rather than per selection when the snapshot is still
/// fresh. When a refresh is due, selection waits for that probe so the first lookup after
/// start (or after the interval) does not observe an empty snapshot spuriously.
/// </remarks>
internal sealed class BackfillConsumerAvailabilityService : IBackfillConsumerAvailability, IAsyncDisposable
{
    /// <summary>Minimum interval between broker refresh attempts.</summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly IRabbitMqService _rabbitMq;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private ImmutableArray<BackfillEligibleBackbone> _snapshot = [];
    private DateTimeOffset _nextRefresh = DateTimeOffset.MinValue;
    private int _disposed;

    /// <summary>Initializes a new availability cache.</summary>
    internal BackfillConsumerAvailabilityService(
        IRabbitMqService rabbitMq,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(rabbitMq);
        ArgumentNullException.ThrowIfNull(logger);
        _rabbitMq = rabbitMq;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public IReadOnlyList<BackfillEligibleBackbone> GetEligibleBackbones()
    {
        RefreshIfDueAsync(force: false, CancellationToken.None).GetAwaiter().GetResult();
        return _snapshot;
    }

    /// <summary>Forces a refresh regardless of interval (startup / tests).</summary>
    internal Task RefreshNowAsync(CancellationToken cancellationToken = default) =>
        RefreshIfDueAsync(force: true, cancellationToken);

    private async Task RefreshIfDueAsync(bool force, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (!force)
        {
            lock (_gate)
            {
                if (now < _nextRefresh)
                {
                    return;
                }
            }
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            now = _timeProvider.GetUtcNow();
            if (!force)
            {
                lock (_gate)
                {
                    if (now < _nextRefresh)
                    {
                        return;
                    }
                }
            }

            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _nextRefresh = _timeProvider.GetUtcNow() + RefreshInterval;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (!_rabbitMq.TryGetCurrent(out var handle))
        {
            return;
        }

        try
        {
            await using var channel = await handle.Connection
                .CreateTopologyChannelAsync(cancellationToken)
                .ConfigureAwait(false);
            var builder = ImmutableArray.CreateBuilder<BackfillEligibleBackbone>();
            foreach (var definition in BackfillArticleRetrievalTopology.Definitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var ok = await channel.QueueDeclarePassiveAsync(definition.QueueName, cancellationToken)
                        .ConfigureAwait(false);
                    var consumers = checked((int)ok.ConsumerCount);
                    if (consumers > 0)
                    {
                        builder.Add(new BackfillEligibleBackbone(
                            definition.Provider,
                            definition.ExchangeName,
                            definition.RoutingKey,
                            definition.QueueName,
                            consumers));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ArticleWorkRpcLogMessages.ConsumerProbeFailed(
                        _logger,
                        ex,
                        definition.QueueName);
                }
            }

            var next = builder.ToImmutable();
            lock (_gate)
            {
                _snapshot = next;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ArticleWorkRpcLogMessages.ConsumerProbeFailed(_logger, ex, "(all)");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _refreshGate.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>Test double / static snapshot availability.</summary>
internal sealed class StaticBackfillConsumerAvailability : IBackfillConsumerAvailability
{
    private volatile IReadOnlyList<BackfillEligibleBackbone> _eligible;

    /// <summary>Creates availability from an initial snapshot.</summary>
    internal StaticBackfillConsumerAvailability(IReadOnlyList<BackfillEligibleBackbone> eligible)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        _eligible = eligible;
    }

    /// <summary>Replaces the snapshot (tests).</summary>
    internal void SetEligible(IReadOnlyList<BackfillEligibleBackbone> eligible)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        _eligible = eligible;
    }

    /// <inheritdoc />
    public IReadOnlyList<BackfillEligibleBackbone> GetEligibleBackbones() => _eligible;
}
