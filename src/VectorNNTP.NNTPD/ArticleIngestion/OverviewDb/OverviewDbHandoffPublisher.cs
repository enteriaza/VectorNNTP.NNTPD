using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Diagnostics;
using VectorNNTP.NNTPD.RabbitMq;

namespace VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;

/// <summary>
/// Publishes OverviewDB protobuf payloads to <c>overviewdb.queue</c> with publisher confirms.
/// </summary>
/// <remarks>
/// <para>
/// RabbitMQ is the handoff boundary. This type does not open an OverviewDB client,
/// RPC channel, HTTP/gRPC call, or database connection, and does not wait for an
/// OverviewDB acknowledgement.
/// </para>
/// <para>
/// Concurrent outstanding confirms use a bounded pool of confirm-enabled publish
/// channels. Official RabbitMQ.Client guidance still treats concurrent publishes
/// on one shared <c>IChannel</c> as unsafe for framing; each in-flight publish
/// therefore leases a dedicated channel and returns it only after the confirm
/// await completes. Pool size is <see cref="ArticleIngestionOptions.MaxPublishConcurrency"/>.
/// </para>
/// </remarks>
internal sealed class OverviewDbHandoffPublisher : IOverviewDbHandoffPublisher, IAsyncDisposable
{
    private readonly IRabbitMqService _rabbitMq;
    private readonly IOptions<NntpdOptions> _nntpdOptions;
    private readonly IOptions<RabbitMqOptions> _rabbitMqOptions;
    private readonly IngestionPipelineMetrics? _pipeline;
    private readonly ConcurrentBag<IRabbitMqPublishChannel> _idleChannels = new();
    private readonly object _poolGate = new();
    private SemaphoreSlim? _publishSlots;
    private int _configuredSlots;
    private int _createdChannels;
    private long _poolGeneration = -1;
    private int _disposed;

    /// <summary>Initializes a new OverviewDB handoff publisher.</summary>
    public OverviewDbHandoffPublisher(
        IRabbitMqService rabbitMq,
        IOptions<NntpdOptions> nntpdOptions,
        IOptions<RabbitMqOptions> rabbitMqOptions,
        IngestionPipelineMetrics? pipelineMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(rabbitMq);
        ArgumentNullException.ThrowIfNull(nntpdOptions);
        ArgumentNullException.ThrowIfNull(rabbitMqOptions);
        _rabbitMq = rabbitMq;
        _nntpdOptions = nntpdOptions;
        _rabbitMqOptions = rabbitMqOptions;
        _pipeline = pipelineMetrics;
    }

    /// <summary>Gets the number of currently leased / in-flight publishes (tests/telemetry).</summary>
    internal int InFlightPublishes
    {
        get
        {
            var slots = Volatile.Read(ref _publishSlots);
            var configured = Volatile.Read(ref _configuredSlots);
            if (slots is null || configured <= 0)
            {
                return 0;
            }

            return configured - slots.CurrentCount;
        }
    }

    /// <summary>Gets how many publish channels have been created for the current generation (tests).</summary>
    internal int CreatedChannelCount => Volatile.Read(ref _createdChannels);

    /// <inheritdoc />
    public async Task PublishConfirmedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        var fqdn = _nntpdOptions.Value.Fqdn;
        if (string.IsNullOrWhiteSpace(fqdn))
        {
            throw new InvalidOperationException("OverviewDB handoff requires the generated application FQDN.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var confirmTimeoutSeconds = _rabbitMqOptions.Value.PublishConfirmTimeoutSeconds ?? 10;
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(confirmTimeoutSeconds));

        var handoffStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var leaseStart = handoffStart;
        var slots = EnsurePublishSlots();
        try
        {
            await slots.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _pipeline?.RecordConfirmTimeout();
            _pipeline?.RecordHandoff(handoffStart);
            throw;
        }

        _pipeline?.RecordGateWait(leaseStart);
        _pipeline?.ObserveInFlightPublishes(InFlightPublishes);
        IRabbitMqPublishChannel? channel = null;
        var returnToPool = false;
        try
        {
            channel = await LeaseChannelAsync(timeoutCts.Token).ConfigureAwait(false);
            var publishStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await channel.PublishConfirmedAsync(
                        OverviewDbTopology.DefaultExchange,
                        OverviewDbTopology.RoutingKey,
                        Guid.NewGuid().ToString(),
                        fqdn,
                        OverviewDbTopology.ExpirationMilliseconds,
                        payload,
                        timeoutCts.Token)
                    .ConfigureAwait(false);
                _pipeline?.RecordPublishAndConfirm(publishStart);
                returnToPool = true;
            }
            catch (OperationCanceledException) when (
                timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _pipeline?.RecordConfirmTimeout();
                throw;
            }
            catch
            {
                _pipeline?.RecordConfirmFailure();
                throw;
            }
        }
        finally
        {
            if (channel is not null)
            {
                if (returnToPool)
                {
                    ReturnChannel(channel);
                }
                else
                {
                    await DisposeChannelAsync(channel).ConfigureAwait(false);
                    Interlocked.Decrement(ref _createdChannels);
                }
            }

            _pipeline?.RecordHandoff(handoffStart);
            slots.Release();
            _pipeline?.ObserveInFlightPublishes(InFlightPublishes);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        var slots = Interlocked.Exchange(ref _publishSlots, null);
        if (slots is not null)
        {
            await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                await DrainIdleChannelsAsync().ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
                slots.Dispose();
            }
        }
        else
        {
            await DrainIdleChannelsAsync().ConfigureAwait(false);
        }
    }

    private SemaphoreSlim EnsurePublishSlots()
    {
        var existing = Volatile.Read(ref _publishSlots);
        if (existing is not null)
        {
            return existing;
        }

        var concurrency = _nntpdOptions.Value.ArticleIngestion?.MaxPublishConcurrency
            ?? ArticleIngestionOptions.DefaultMaxPublishConcurrency;
        if (concurrency < 1)
        {
            concurrency = 1;
        }

        var created = new SemaphoreSlim(concurrency, concurrency);
        var prior = Interlocked.CompareExchange(ref _publishSlots, created, null);
        if (prior is not null)
        {
            created.Dispose();
            return prior;
        }

        Volatile.Write(ref _configuredSlots, concurrency);
        return created;
    }

    private async Task<IRabbitMqPublishChannel> LeaseChannelAsync(CancellationToken cancellationToken)
    {
        if (!_rabbitMq.TryGetCurrent(out var handle) || !handle.IsCurrent)
        {
            throw new InvalidOperationException("RabbitMQ connection is not ready for OverviewDB handoff.");
        }

        InvalidatePoolIfGenerationChanged(handle.Generation);

        while (_idleChannels.TryTake(out var idle))
        {
            if (idle.Generation == handle.Generation && idle.IsOpen)
            {
                return idle;
            }

            await DisposeChannelAsync(idle).ConfigureAwait(false);
            Interlocked.Decrement(ref _createdChannels);
        }

        var channel = await handle.Connection
            .CreatePublishChannelAsync(handle.Generation, cancellationToken)
            .ConfigureAwait(false);
        Interlocked.Increment(ref _createdChannels);
        return channel;
    }

    private void ReturnChannel(IRabbitMqPublishChannel channel)
    {
        if (!_rabbitMq.TryGetCurrent(out var handle)
            || !handle.IsCurrent
            || channel.Generation != handle.Generation
            || !channel.IsOpen
            || Volatile.Read(ref _disposed) == 1)
        {
            _ = DisposeChannelAsync(channel);
            Interlocked.Decrement(ref _createdChannels);
            return;
        }

        _idleChannels.Add(channel);
    }

    private void InvalidatePoolIfGenerationChanged(long generation)
    {
        if (Volatile.Read(ref _poolGeneration) == generation)
        {
            return;
        }

        lock (_poolGate)
        {
            if (_poolGeneration == generation)
            {
                return;
            }

            _poolGeneration = generation;
            while (_idleChannels.TryTake(out var stale))
            {
                _ = DisposeChannelAsync(stale);
                Interlocked.Decrement(ref _createdChannels);
            }
        }
    }

    private async Task DrainIdleChannelsAsync()
    {
        while (_idleChannels.TryTake(out var channel))
        {
            await DisposeChannelAsync(channel).ConfigureAwait(false);
            Interlocked.Decrement(ref _createdChannels);
        }
    }

    private static async Task DisposeChannelAsync(IRabbitMqPublishChannel channel)
    {
        try
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
