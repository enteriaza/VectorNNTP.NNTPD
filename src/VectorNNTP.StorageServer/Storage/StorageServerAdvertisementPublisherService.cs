using VectorNNTP.Common.Messaging.Cache;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.StorageServer.Configuration;

namespace VectorNNTP.StorageServer.Storage;

/// <summary>
/// Publishes StorageServer capacity advertisements to <c>cache.broadcast</c> once per second.
/// </summary>
/// <remarks>
/// Declares the durable fanout exchange so StorageServer can start before NNTPD.
/// Uses the shared <see cref="RabbitMqService"/> connection; does not own TCP lifecycle.
/// Heartbeat publication must not block clean shutdown.
/// </remarks>
public sealed class StorageServerAdvertisementPublisherService : IApplicationService
{
    private readonly IRabbitMqService _rabbitMq;
    private readonly StorageServerRuntimeOptions _runtime;
    private readonly IStorageCapacityReader _capacityReader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StorageServerAdvertisementPublisherService> _logger;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private CancellationTokenSource? _shutdownCts;
    private Task? _loop;
    private IRabbitMqPublishChannel? _channel;
    private int _started;

    /// <summary>Initializes a new advertisement publisher.</summary>
    public StorageServerAdvertisementPublisherService(
        IRabbitMqService rabbitMq,
        StorageServerRuntimeOptions runtime,
        IStorageCapacityReader capacityReader,
        ILogger<StorageServerAdvertisementPublisherService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(rabbitMq);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(capacityReader);
        ArgumentNullException.ThrowIfNull(logger);
        _rabbitMq = rabbitMq;
        _runtime = runtime;
        _capacityReader = capacityReader;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => "StorageServerAdvertisementPublisher";

    /// <inheritdoc />
    public Task? Execution => _loop;

    /// <summary>Gets how many advertisements were published successfully (tests).</summary>
    internal long PublishedCount => Volatile.Read(ref _publishedCount);

    private long _publishedCount;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            _shutdownCts = new CancellationTokenSource();
            await EnsureExchangeAsync(cancellationToken).ConfigureAwait(false);
            await EnsurePublishChannelAsync(cancellationToken).ConfigureAwait(false);
            _loop = RunPublishLoopAsync(_shutdownCts.Token);
            StorageServerAdvertisementLogMessages.Started(_logger, CacheFleetTopology.BroadcastExchangeName);
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => StopCoreAsync();

    /// <summary>Publishes one advertisement immediately (tests).</summary>
    internal Task PublishOnceAsync(CancellationToken cancellationToken) =>
        PublishAdvertisementAsync(cancellationToken);

    private async Task RunPublishLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await PublishAdvertisementAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    StorageServerAdvertisementLogMessages.PublishFailed(_logger, ex);
                }

                try
                {
                    await Task.Delay(CacheFleetTopology.AdvertisementInterval, _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PublishAdvertisementAsync(CancellationToken cancellationToken)
    {
        var capacity = _capacityReader.Read();
        var advertisement = new StorageServerAdvertisement(
            StorageServerAdvertisementWireProtocol.CurrentVersion,
            _runtime.ServerId,
            _runtime.Fqdn,
            capacity.TotalBytes,
            capacity.UsedBytes,
            capacity.AvailableBytes,
            _timeProvider.GetUtcNow(),
            _runtime.BindPortTls);

        var body = StorageServerAdvertisementWireProtocol.SerializeV1(advertisement);
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = await EnsurePublishChannelAsync(cancellationToken).ConfigureAwait(false);
            await channel.PublishConfirmedAsync(
                    new RabbitMqConfirmedPublication(
                        CacheFleetTopology.BroadcastExchangeName,
                        RoutingKey: string.Empty,
                        MessageId: Guid.NewGuid().ToString("D"),
                        AppId: _runtime.Fqdn,
                        CorrelationId: null,
                        ContentType: StorageServerAdvertisementWireProtocol.JsonContentType,
                        RequestIdHeader: null,
                        ExpirationMilliseconds: CacheFleetTopology.AdvertisementExpirationMilliseconds,
                        Persistent: false,
                        Mandatory: false,
                        Body: body),
                    cancellationToken)
                .ConfigureAwait(false);
            Interlocked.Increment(ref _publishedCount);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private async Task EnsureExchangeAsync(CancellationToken cancellationToken)
    {
        if (!_rabbitMq.TryGetCurrent(out var handle) || !handle.IsCurrent)
        {
            throw new InvalidOperationException("RabbitMQ connection is not ready for cache broadcast declaration.");
        }

        await using var channel = await handle.Connection
            .CreateTopologyChannelAsync(cancellationToken)
            .ConfigureAwait(false);
        await channel.ExchangeDeclareAsync(
                CacheFleetTopology.BroadcastExchangeName,
                CacheFleetTopology.BroadcastExchangeType,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IRabbitMqPublishChannel> EnsurePublishChannelAsync(CancellationToken cancellationToken)
    {
        var existing = _channel;
        if (existing is { IsOpen: true })
        {
            return existing;
        }

        if (!_rabbitMq.TryGetCurrent(out var handle) || !handle.IsCurrent)
        {
            throw new InvalidOperationException("RabbitMQ connection is not ready for StorageServer advertisement publish.");
        }

        var channel = await handle.Connection
            .CreatePublishChannelAsync(handle.Generation, cancellationToken)
            .ConfigureAwait(false);
        var previous = Interlocked.Exchange(ref _channel, channel);
        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        return channel;
    }

    private async Task StopCoreAsync()
    {
        if (_shutdownCts is not null)
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
        }

        var loop = Interlocked.Exchange(ref _loop, null);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        var channel = Interlocked.Exchange(ref _channel, null);
        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        _shutdownCts?.Dispose();
        _shutdownCts = null;
        Interlocked.Exchange(ref _started, 0);
        StorageServerAdvertisementLogMessages.Stopped(_logger);
    }
}
