using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

/// <summary>In-memory RabbitMQ connection factory for BackFiller offline tests.</summary>
internal sealed class FakeBackFillerRabbitMqConnectionFactory : IRabbitMqConnectionFactory
{
    private int _connectCount;
    private int _attemptCount;

    public int ConnectCount => Volatile.Read(ref _connectCount);

    public int AttemptCount => Volatile.Read(ref _attemptCount);

    public FakeBackFillerRabbitMqConnection? LastConnection { get; private set; }

    public List<FakeBackFillerRabbitMqConnection> Connections { get; } = [];

    public Exception? ConnectException { get; set; }

    public int RemainingConnectFailures { get; set; }

    public TaskCompletionSource? ConnectStarted { get; set; }

    public TaskCompletionSource? BlockConnect { get; set; }

    public TaskCompletionSource<int>? Connected { get; set; }

    public bool ReturnUnusableConnection { get; set; }

    public FakePublishConfirmBehavior DefaultPublishConfirmBehavior { get; set; } =
        FakePublishConfirmBehavior.Wait;

    public TaskCompletionSource? BlockCreatePublishChannel { get; set; }

    public TaskCompletionSource? CreatePublishChannelStarted { get; set; }

    public async Task<IRabbitMqConnection> ConnectAsync(
        RabbitMqOptions options,
        string connectionName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        Interlocked.Increment(ref _attemptCount);
        ConnectStarted?.TrySetResult();
        if (BlockConnect is not null)
        {
            await BlockConnect.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (RemainingConnectFailures > 0)
        {
            RemainingConnectFailures--;
            throw ConnectException ?? new InvalidOperationException("broker down");
        }

        if (ConnectException is not null)
        {
            throw ConnectException;
        }

        var count = Interlocked.Increment(ref _connectCount);
        var hosts = options.Hosts ?? [];
        var connection = new FakeBackFillerRabbitMqConnection(
            hosts.Length > 0 ? hosts[0]! : "127.0.0.1",
            options.Port ?? 5672,
            string.IsNullOrWhiteSpace(options.VirtualHost) ? "/" : options.VirtualHost.Trim(),
            connectionName)
        {
            IsOpen = !ReturnUnusableConnection,
            DefaultPublishConfirmBehavior = DefaultPublishConfirmBehavior,
            BlockCreatePublishChannel = BlockCreatePublishChannel,
            CreatePublishChannelStarted = CreatePublishChannelStarted,
        };

        LastConnection = connection;
        Connections.Add(connection);
        Connected?.TrySetResult(count);
        return connection;
    }
}

internal sealed class FakeBackFillerRabbitMqConnection : IRabbitMqConnection
{
    public FakeBackFillerRabbitMqConnection(string host, int port, string virtualHost, string clientProvidedName)
    {
        Host = host;
        Port = port;
        VirtualHost = virtualHost;
        ClientProvidedName = clientProvidedName;
    }

    public bool IsOpen { get; set; } = true;

    public string Host { get; }

    public int Port { get; }

    public string VirtualHost { get; }

    public string ClientProvidedName { get; }

    public int DisposeCount { get; private set; }

    public TaskCompletionSource? DisposeStarted { get; set; }

    public TaskCompletionSource? BlockDispose { get; set; }

    public event EventHandler<RabbitMqConnectionLostEventArgs>? ConnectionLost;

    public List<FakeBackFillerRabbitMqChannel> Channels { get; } = [];

    public List<FakeBackFillerRabbitMqPublishChannel> PublishChannels { get; } = [];

    public Exception? CreateChannelException { get; set; }

    public Exception? CreatePublishChannelException { get; set; }

    /// <summary>
    /// When set, the next manual-ack channel receives this as
    /// <see cref="FakeBackFillerRabbitMqChannel.QueueDeclareException"/> (once).
    /// </summary>
    public Exception? NextChannelQueueDeclareException { get; set; }

    public TaskCompletionSource? CreatePublishChannelStarted { get; set; }

    public TaskCompletionSource? BlockCreatePublishChannel { get; set; }

    public Action<FakeBackFillerRabbitMqPublishChannel>? OnPublishChannelCreated { get; set; }

    public FakePublishConfirmBehavior DefaultPublishConfirmBehavior { get; set; } =
        FakePublishConfirmBehavior.Wait;

    public Task<IRabbitMqTopologyChannel> CreateTopologyChannelAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("BackFiller Article Work tests use manual-ack channels for topology.");

    public Task<IRabbitMqRpcChannel> CreateRpcChannelAsync(long generation, CancellationToken cancellationToken) =>
        throw new NotSupportedException("BackFiller Article Work tests do not open RPC channels.");

    public Task<IRabbitMqAsyncConfirmPublishChannel> CreateAsyncConfirmPublishChannelAsync(
        long generation,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("BackFiller Article Work tests do not open async-confirm channels.");

    public Task<IRabbitMqManualAckChannel> CreateManualAckChannelAsync(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CreateChannelException is not null)
        {
            throw CreateChannelException;
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ connection is not open for channel creation.");
        }

        var channel = new FakeBackFillerRabbitMqChannel(generation);
        if (NextChannelQueueDeclareException is not null)
        {
            channel.QueueDeclareException = NextChannelQueueDeclareException;
            NextChannelQueueDeclareException = null;
        }

        Channels.Add(channel);
        return Task.FromResult<IRabbitMqManualAckChannel>(channel);
    }

    public async Task<IRabbitMqPublishChannel> CreatePublishChannelAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreatePublishChannelStarted?.TrySetResult();
        if (BlockCreatePublishChannel is not null)
        {
            await BlockCreatePublishChannel.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (CreatePublishChannelException is not null)
        {
            throw CreatePublishChannelException;
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ connection is not open for publish channel creation.");
        }

        var channel = new FakeBackFillerRabbitMqPublishChannel(generation)
        {
            ConfirmBehavior = DefaultPublishConfirmBehavior,
        };
        OnPublishChannelCreated?.Invoke(channel);
        PublishChannels.Add(channel);
        return channel;
    }

    public void SimulateLost(
        ushort replyCode = 320,
        string replyText = "connection lost",
        string initiator = "Peer")
    {
        IsOpen = false;
        ConnectionLost?.Invoke(this, new RabbitMqConnectionLostEventArgs(replyCode, replyText, initiator));
    }

    public async ValueTask DisposeAsync()
    {
        DisposeStarted?.TrySetResult();
        if (BlockDispose is not null)
        {
            await BlockDispose.Task.ConfigureAwait(false);
        }

        DisposeCount++;
        IsOpen = false;
    }
}

internal readonly record struct FakeRabbitMqSettlement(ulong DeliveryTag, bool Acknowledge, bool Requeue);

internal sealed class FakeBackFillerRabbitMqChannel(long generation) : IRabbitMqManualAckChannel
{
    private Func<RabbitMqManualAckDelivery, Task>? _onDelivery;

    public long Generation { get; } = generation;

    public bool IsOpen { get; set; } = true;

    public int DisposeCount { get; private set; }

    public int ConsumeCount { get; private set; }

    public int CancelCount { get; private set; }

    public ushort? LastPrefetch { get; private set; }

    public string? LastQueue { get; private set; }

    public string? ConsumerTag { get; private set; }

    public List<string> ConsumedQueues { get; } = [];

    public List<FakeRabbitMqSettlement> Settlements { get; } = [];

    public List<(string Name, string Type, bool Durable, bool AutoDelete)> ExchangeDeclarations { get; } = [];

    public List<(string Name, bool Durable, bool Exclusive, bool AutoDelete, IReadOnlyDictionary<string, object?>? Arguments)> QueueDeclarations { get; } = [];

    public List<(string Queue, string Exchange, string RoutingKey)> BindingDeclarations { get; } = [];

    public Exception? ExchangeDeclareException { get; set; }

    public Exception? QueueDeclareException { get; set; }

    public Exception? QueueBindException { get; set; }

    public Exception? ConsumeException { get; set; }

    public Exception? AckException { get; set; }

    public Exception? NackException { get; set; }

    public TaskCompletionSource? AckStarted { get; set; }

    public TaskCompletionSource? AckGate { get; set; }

    public Task ExchangeDeclareAsync(
        string exchange,
        string type,
        bool durable,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        cancellationToken.ThrowIfCancellationRequested();
        if (ExchangeDeclareException is not null)
        {
            throw ExchangeDeclareException;
        }

        ExchangeDeclarations.Add((exchange, type, durable, autoDelete));
        return Task.CompletedTask;
    }

    public Task QueueDeclareAsync(
        string queue,
        bool durable,
        bool exclusive,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        cancellationToken.ThrowIfCancellationRequested();
        if (QueueDeclareException is not null)
        {
            throw QueueDeclareException;
        }

        QueueDeclarations.Add((queue, durable, exclusive, autoDelete, arguments));
        return Task.CompletedTask;
    }

    public Task QueueBindAsync(
        string queue,
        string exchange,
        string routingKey,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentNullException.ThrowIfNull(routingKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (QueueBindException is not null)
        {
            throw QueueBindException;
        }

        BindingDeclarations.Add((queue, exchange, routingKey));
        return Task.CompletedTask;
    }

    public Task<string> BasicConsumeAsync(
        string queue,
        ushort prefetchCount,
        Func<RabbitMqManualAckDelivery, Task> onDelivery,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentNullException.ThrowIfNull(onDelivery);
        cancellationToken.ThrowIfCancellationRequested();
        if (ConsumeException is not null)
        {
            throw ConsumeException;
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ channel is not open for consume.");
        }

        _onDelivery = onDelivery;
        LastPrefetch = prefetchCount;
        LastQueue = queue;
        ConsumedQueues.Add(queue);
        ConsumeCount++;
        ConsumerTag = $"ctag-{Generation}-{ConsumeCount}";
        return Task.FromResult(ConsumerTag);
    }

    public Task BasicCancelAsync(string consumerTag, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerTag);
        cancellationToken.ThrowIfCancellationRequested();
        CancelCount++;
        return Task.CompletedTask;
    }

    public async Task BasicAckAsync(ulong deliveryTag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ channel is not open for ACK.");
        }

        AckStarted?.TrySetResult();
        if (AckGate is not null)
        {
            await AckGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (AckException is not null)
        {
            throw AckException;
        }

        Settlements.Add(new FakeRabbitMqSettlement(deliveryTag, Acknowledge: true, Requeue: false));
    }

    public Task BasicNackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ channel is not open for NACK.");
        }

        if (NackException is not null)
        {
            throw NackException;
        }

        Settlements.Add(new FakeRabbitMqSettlement(deliveryTag, Acknowledge: false, requeue));
        return Task.CompletedTask;
    }

    public Task DeliverAsync(RabbitMqManualAckDelivery delivery)
    {
        if (_onDelivery is null)
        {
            throw new InvalidOperationException("RabbitMQ channel has no consumer.");
        }

        return _onDelivery(delivery);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        IsOpen = false;
        return ValueTask.CompletedTask;
    }
}

internal enum FakePublishConfirmBehavior
{
    Wait = 0,
    Confirm = 1,
    Nack = 2,
    ThrowOnPublish = 3,
    Timeout = 4,
    CloseChannel = 5,
    Unroutable = 6,
}

internal sealed class FakeBackFillerRabbitMqPublishChannel(long generation) : IRabbitMqPublishChannel
{
    public long Generation { get; } = generation;

    public bool IsOpen { get; set; } = true;

    public int DisposeCount { get; private set; }

    public FakePublishConfirmBehavior ConfirmBehavior { get; set; } = FakePublishConfirmBehavior.Wait;

    public TaskCompletionSource? Enqueued { get; set; }

    public TaskCompletionSource? ConfirmGate { get; set; }

    public Action? AfterEnqueue { get; set; }

    public List<RabbitMqConfirmedPublication> Publications { get; } = [];

    public Task PublishConfirmedAsync(
        string exchange,
        string routingKey,
        string messageId,
        string appId,
        string expiration,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken) =>
        PublishConfirmedAsync(
            new RabbitMqConfirmedPublication(
                exchange,
                routingKey,
                messageId,
                appId,
                CorrelationId: null,
                ContentType: null,
                RequestIdHeader: null,
                expiration,
                Persistent: true,
                Mandatory: true,
                body),
            cancellationToken);

    public async Task PublishConfirmedAsync(RabbitMqConfirmedPublication publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ publish channel is not open.");
        }

        if (ConfirmBehavior == FakePublishConfirmBehavior.ThrowOnPublish)
        {
            throw new InvalidOperationException("publish failed");
        }

        Publications.Add(publication);
        Enqueued?.TrySetResult();
        AfterEnqueue?.Invoke();

        switch (ConfirmBehavior)
        {
            case FakePublishConfirmBehavior.Confirm:
                return;
            case FakePublishConfirmBehavior.Nack:
                throw new InvalidOperationException("publisher nack");
            case FakePublishConfirmBehavior.Unroutable:
                throw new InvalidOperationException("RabbitMQ returned the publication as unroutable.");
            case FakePublishConfirmBehavior.Timeout:
                throw new OperationCanceledException("publisher confirm timeout", cancellationToken);
            case FakePublishConfirmBehavior.CloseChannel:
                IsOpen = false;
                throw new InvalidOperationException("RabbitMQ publish channel closed during confirmation.");
            default:
                if (ConfirmGate is not null)
                {
                    await ConfirmGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        IsOpen = false;
        return ValueTask.CompletedTask;
    }
}
