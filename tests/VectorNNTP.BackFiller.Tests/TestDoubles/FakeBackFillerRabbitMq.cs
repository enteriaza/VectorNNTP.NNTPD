using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

internal sealed class FakeBackFillerRabbitMqConnectionFactory : IBackFillerRabbitMqConnectionFactory
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

    public async Task<IBackFillerRabbitMqConnection> ConnectAsync(
        BackFillerRabbitMqRuntimeOptions options,
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
        var hosts = options.Hosts;
        var connection = new FakeBackFillerRabbitMqConnection(
            hosts.Count > 0 ? hosts[0] : "127.0.0.1",
            options.Port,
            options.VirtualHost,
            connectionName)
        {
            IsOpen = !ReturnUnusableConnection,
        };

        LastConnection = connection;
        Connections.Add(connection);
        Connected?.TrySetResult(count);
        return connection;
    }
}

internal sealed class FakeBackFillerRabbitMqConnection : IBackFillerRabbitMqConnection
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

    public event EventHandler<BackFillerRabbitMqConnectionLostEventArgs>? ConnectionLost;

    public List<FakeBackFillerRabbitMqChannel> Channels { get; } = [];

    public List<FakeBackFillerRabbitMqPublishChannel> PublishChannels { get; } = [];

    public Exception? CreateChannelException { get; set; }

    public Exception? CreatePublishChannelException { get; set; }

    public TaskCompletionSource? CreatePublishChannelStarted { get; set; }

    public TaskCompletionSource? BlockCreatePublishChannel { get; set; }

    public FakePublishConfirmBehavior DefaultPublishConfirmBehavior { get; set; } =
        FakePublishConfirmBehavior.Wait;

    public Task<IBackFillerRabbitMqChannel> CreateChannelAsync(long generation, CancellationToken cancellationToken)
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
        Channels.Add(channel);
        return Task.FromResult<IBackFillerRabbitMqChannel>(channel);
    }

    public async Task<IBackFillerRabbitMqPublishChannel> CreatePublishChannelAsync(
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
        PublishChannels.Add(channel);
        return channel;
    }

    public void SimulateLost(
        ushort replyCode = 320,
        string replyText = "connection lost",
        string initiator = "Peer")
    {
        IsOpen = false;
        ConnectionLost?.Invoke(this, new BackFillerRabbitMqConnectionLostEventArgs(replyCode, replyText, initiator));
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

internal sealed class FakeBackFillerRabbitMqChannel(long generation) : IBackFillerRabbitMqChannel
{
    private Func<BackFillerRabbitMqConsumedDelivery, Task>? _onDelivery;

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

    public Exception? ConsumeException { get; set; }

    public Exception? AckException { get; set; }

    public Exception? NackException { get; set; }

    public Task<string> BasicConsumeAsync(
        string queue,
        ushort prefetchCount,
        Func<BackFillerRabbitMqConsumedDelivery, Task> onDelivery,
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

    public Task BasicAckAsync(ulong deliveryTag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ channel is not open for ACK.");
        }

        if (AckException is not null)
        {
            throw AckException;
        }

        Settlements.Add(new FakeRabbitMqSettlement(deliveryTag, Acknowledge: true, Requeue: false));
        return Task.CompletedTask;
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

    public Task DeliverAsync(BackFillerRabbitMqConsumedDelivery delivery)
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
}

internal sealed class FakeBackFillerRabbitMqPublishChannel(long generation) : IBackFillerRabbitMqPublishChannel
{
    public long Generation { get; } = generation;

    public bool IsOpen { get; set; } = true;

    public int DisposeCount { get; private set; }

    public FakePublishConfirmBehavior ConfirmBehavior { get; set; } = FakePublishConfirmBehavior.Wait;

    public TaskCompletionSource? Enqueued { get; set; }

    public TaskCompletionSource? ConfirmGate { get; set; }

    public Action? AfterEnqueue { get; set; }

    public List<BackFillerRabbitMqPublication> Publications { get; } = [];

    public async Task PublishConfirmedAsync(BackFillerRabbitMqPublication publication, CancellationToken cancellationToken)
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
