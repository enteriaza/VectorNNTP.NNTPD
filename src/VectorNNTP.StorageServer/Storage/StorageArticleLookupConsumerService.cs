using VectorNNTP.Common.Messaging.Cache;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.Common.Core;
using VectorNNTP.StorageServer.Configuration;

namespace VectorNNTP.StorageServer.Storage;

/// <summary>
/// Consumes fleet article-presence lookups from <c>cache.requests</c> on an ephemeral
/// per-StorageServer queue and publishes positive responses only.
/// </summary>
public sealed class StorageArticleLookupConsumerService : IApplicationService
{
    private readonly IRabbitMqService _rabbitMq;
    private readonly StorageServerRuntimeOptions _runtime;
    private readonly IStorageArticlePresence _presence;
    private readonly ILogger<StorageArticleLookupConsumerService> _logger;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly object _sessionGate = new();
    private CancellationTokenSource? _shutdownCts;
    private ConsumeSession? _session;
    private IRabbitMqPublishChannel? _publishChannel;
    private int _started;

    /// <summary>Initializes a new lookup consumer.</summary>
    public StorageArticleLookupConsumerService(
        IRabbitMqService rabbitMq,
        StorageServerRuntimeOptions runtime,
        IStorageArticlePresence presence,
        ILogger<StorageArticleLookupConsumerService> logger)
    {
        ArgumentNullException.ThrowIfNull(rabbitMq);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(logger);
        _rabbitMq = rabbitMq;
        _runtime = runtime;
        _presence = presence;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "StorageArticleLookupConsumer";

    /// <inheritdoc />
    public Task? Execution => null;

    /// <summary>Gets the current request queue name when attached (tests).</summary>
    internal string? CurrentQueueName
    {
        get
        {
            lock (_sessionGate)
            {
                return _session?.QueueName;
            }
        }
    }

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
            _rabbitMq.ConnectionReplaced += OnConnectionReplaced;
            await AttachCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task AttachCurrentSessionAsync(CancellationToken cancellationToken)
    {
        if (!_rabbitMq.TryGetCurrent(out var handle) || !handle.IsCurrent)
        {
            StorageArticleLookupConsumerLogMessages.ConnectionNotReady(_logger);
            throw new InvalidOperationException("RabbitMQ connection is not ready for storage article lookup consume.");
        }

        if (string.IsNullOrWhiteSpace(_runtime.Fqdn))
        {
            throw new InvalidOperationException("Storage article lookup consume requires the generated StorageServer FQDN.");
        }

        var queueName = CacheFleetTopology.BuildStorageServerRequestQueueName(_runtime.Fqdn);
        var generation = handle.Generation;
        IRabbitMqManualAckChannel? channel = null;
        try
        {
            channel = await handle.Connection
                .CreateManualAckChannelAsync(generation, cancellationToken)
                .ConfigureAwait(false);

            await channel.ExchangeDeclareAsync(
                    CacheFleetTopology.RequestsExchangeName,
                    CacheFleetTopology.FanoutExchangeType,
                    durable: true,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken)
                .ConfigureAwait(false);

            await channel.QueueDeclareAsync(
                    queueName,
                    durable: false,
                    exclusive: true,
                    autoDelete: true,
                    arguments: null,
                    cancellationToken)
                .ConfigureAwait(false);

            await channel.QueueBindAsync(
                    queueName,
                    CacheFleetTopology.RequestsExchangeName,
                    routingKey: string.Empty,
                    arguments: null,
                    cancellationToken)
                .ConfigureAwait(false);

            var publishChannel = await handle.Connection
                .CreatePublishChannelAsync(generation, cancellationToken)
                .ConfigureAwait(false);
            var previousPublish = Interlocked.Exchange(ref _publishChannel, publishChannel);
            if (previousPublish is not null)
            {
                await previousPublish.DisposeAsync().ConfigureAwait(false);
            }

            var consumerTag = await channel
                .BasicConsumeAsync(queueName, prefetchCount: 64, OnDeliveryAsync, cancellationToken)
                .ConfigureAwait(false);

            var session = new ConsumeSession(generation, queueName, consumerTag, channel);
            ConsumeSession? previous;
            lock (_sessionGate)
            {
                previous = _session;
                _session = session;
            }

            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
                StorageArticleLookupConsumerLogMessages.SessionReplaced(_logger, queueName, generation);
            }
            else
            {
                StorageArticleLookupConsumerLogMessages.Ready(_logger, queueName, generation);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StorageArticleLookupConsumerLogMessages.SessionAttachFailed(_logger, ex, generation);
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task OnDeliveryAsync(RabbitMqManualAckDelivery delivery)
    {
        ConsumeSession? session;
        lock (_sessionGate)
        {
            session = _session;
        }

        if (session is null || delivery.Generation != session.Generation)
        {
            return;
        }

        try
        {
            if (!StorageArticleLookupWireProtocol.TryParseRequestV1(
                    delivery.Body.Span,
                    out var request,
                    out var reason)
                || request is null)
            {
                StorageArticleLookupConsumerLogMessages.PayloadRejected(_logger, reason);
                await session.Channel.BasicAckAsync(delivery.DeliveryTag, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            var hasArticle = await _presence
                .HasArticleAsync(request.ArticleId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!hasArticle)
            {
                await session.Channel.BasicAckAsync(delivery.DeliveryTag, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(delivery.ReplyTo)
                || string.IsNullOrWhiteSpace(delivery.CorrelationId))
            {
                StorageArticleLookupConsumerLogMessages.PayloadRejected(
                    _logger,
                    "Positive response requires AMQP ReplyTo and CorrelationId.");
                await session.Channel.BasicAckAsync(delivery.DeliveryTag, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            var response = new StorageArticleLookupResponse(
                StorageArticleLookupWireProtocol.CurrentVersion,
                request.RequestId,
                _runtime.ServerId,
                _runtime.Fqdn,
                request.ArticleId,
                _runtime.BindPortTls);
            var body = StorageArticleLookupWireProtocol.SerializeResponseV1(response);

            await PublishResponseAsync(
                    delivery.ReplyTo,
                    delivery.CorrelationId,
                    request.RequestId.ToString("D"),
                    body,
                    CancellationToken.None)
                .ConfigureAwait(false);

            await session.Channel.BasicAckAsync(delivery.DeliveryTag, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StorageArticleLookupConsumerLogMessages.DeliveryFailed(_logger, ex);
            try
            {
                await session.Channel
                    .BasicNackAsync(delivery.DeliveryTag, requeue: false, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception nackEx)
            {
                StorageArticleLookupConsumerLogMessages.DeliveryFailed(_logger, nackEx);
            }
        }
    }

    private async Task PublishResponseAsync(
        string replyTo,
        string correlationId,
        string requestId,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = _publishChannel
                ?? throw new InvalidOperationException("Storage lookup response publish channel is not ready.");
            await channel.PublishConfirmedAsync(
                    new RabbitMqConfirmedPublication(
                        Exchange: string.Empty,
                        RoutingKey: replyTo,
                        MessageId: Guid.NewGuid().ToString("D"),
                        AppId: _runtime.Fqdn,
                        CorrelationId: correlationId,
                        ContentType: StorageArticleLookupWireProtocol.JsonContentType,
                        RequestIdHeader: requestId,
                        ExpirationMilliseconds: CacheFleetTopology.LookupExpirationMilliseconds,
                        Persistent: false,
                        Mandatory: true,
                        Body: body),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private void OnConnectionReplaced(object? sender, RabbitMqConnectionReplacedEventArgs eventArgs)
    {
        if (_shutdownCts is null || _shutdownCts.IsCancellationRequested || !eventArgs.IsReplacement)
        {
            return;
        }

        _ = AttachReplacementAsync();
    }

    private async Task AttachReplacementAsync()
    {
        try
        {
            await AttachCurrentSessionAsync(_shutdownCts?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StorageArticleLookupConsumerLogMessages.SessionAttachFailed(_logger, ex, _rabbitMq.ConnectionGeneration);
        }
    }

    private async Task StopCoreAsync()
    {
        _rabbitMq.ConnectionReplaced -= OnConnectionReplaced;
        if (_shutdownCts is not null)
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
        }

        ConsumeSession? session;
        lock (_sessionGate)
        {
            session = _session;
            _session = null;
        }

        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        var publishChannel = Interlocked.Exchange(ref _publishChannel, null);
        if (publishChannel is not null)
        {
            await publishChannel.DisposeAsync().ConfigureAwait(false);
        }

        _shutdownCts?.Dispose();
        _shutdownCts = null;
        Interlocked.Exchange(ref _started, 0);
        StorageArticleLookupConsumerLogMessages.Stopped(_logger);
    }

    private sealed class ConsumeSession : IAsyncDisposable
    {
        internal ConsumeSession(
            long generation,
            string queueName,
            string consumerTag,
            IRabbitMqManualAckChannel channel)
        {
            Generation = generation;
            QueueName = queueName;
            ConsumerTag = consumerTag;
            Channel = channel;
        }

        internal long Generation { get; }

        internal string QueueName { get; }

        internal string ConsumerTag { get; }

        internal IRabbitMqManualAckChannel Channel { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Channel.IsOpen)
                {
                    await Channel.BasicCancelAsync(ConsumerTag, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
            }

            await Channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
