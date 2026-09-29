using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.RabbitMq;
using VectorNNTP.NNTPD.RabbitMq.ArticleWork;

namespace VectorNNTP.NNTPD.Tests.TestDoubles;

/// <summary>In-memory RabbitMQ connection factory for offline tests.</summary>
internal sealed class FakeRabbitMqConnectionFactory : IRabbitMqConnectionFactory
{
    private int _connectCount;
    private int _attemptCount;
    private readonly SemaphoreSlim _attemptPulse = new(0, int.MaxValue);

    /// <summary>Gets the number of successful connects.</summary>
    public int ConnectCount => Volatile.Read(ref _connectCount);

    /// <summary>Gets the number of connect attempts, including failures.</summary>
    public int AttemptCount => Volatile.Read(ref _attemptCount);

    /// <summary>Gets the most recently created connection.</summary>
    public FakeRabbitMqConnection? LastConnection { get; private set; }

    /// <summary>Gets every connection created by this factory.</summary>
    public List<FakeRabbitMqConnection> Connections { get; } = [];

    /// <summary>When set, every connect attempt throws this exception.</summary>
    public Exception? ConnectException { get; set; }

    /// <summary>
    /// Remaining connect attempts that throw <see cref="ConnectException"/> or a default broker failure.
    /// After the budget is exhausted, connects succeed unless <see cref="ConnectException"/> is set.
    /// </summary>
    public int RemainingConnectFailures { get; set; }

    /// <summary>Signaled when a connect attempt begins, before <see cref="BlockConnect"/>.</summary>
    public TaskCompletionSource? ConnectStarted { get; set; }

    /// <summary>When set, connect waits on this source before completing.</summary>
    public TaskCompletionSource? BlockConnect { get; set; }

    /// <summary>Signaled after each successful connect with the new <see cref="ConnectCount"/>.</summary>
    public TaskCompletionSource<int>? Connected { get; set; }

    /// <summary>When <see langword="true"/>, created connections report <c>IsOpen == false</c>.</summary>
    public bool ReturnUnusableConnection { get; set; }

    /// <summary>Copied onto each created connection as <see cref="FakeRabbitMqConnection.QueueDeclareException"/>.</summary>
    public Exception? QueueDeclareException { get; set; }

    /// <summary>
    /// Copied onto each created connection as <see cref="FakeRabbitMqConnection.PassiveConsumerCounts"/>.
    /// </summary>
    public Dictionary<string, uint> PassiveConsumerCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>Waits until at least <paramref name="count"/> connect attempts have started.</summary>
    public async Task WaitForAttemptsAsync(int count, CancellationToken cancellationToken)
    {
        while (AttemptCount < count)
        {
            await _attemptPulse.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IRabbitMqConnection> ConnectAsync(
        RabbitMqOptions options,
        string connectionName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        Interlocked.Increment(ref _attemptCount);
        _attemptPulse.Release();
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
        var connection = new FakeRabbitMqConnection(
            hosts.Length > 0 ? hosts[0] : "127.0.0.1",
            options.Port ?? 5672,
            string.IsNullOrWhiteSpace(options.VirtualHost) ? "/" : options.VirtualHost.Trim(),
            connectionName)
        {
            IsOpen = !ReturnUnusableConnection,
            QueueDeclareException = QueueDeclareException,
        };
        foreach (var pair in PassiveConsumerCounts)
        {
            connection.PassiveConsumerCounts[pair.Key] = pair.Value;
        }

        LastConnection = connection;
        Connections.Add(connection);
        Connected?.TrySetResult(count);
        return connection;
    }
}

/// <summary>In-memory RabbitMQ connection for lifecycle tests.</summary>
internal sealed class FakeRabbitMqConnection : IRabbitMqConnection
{
    /// <summary>Initializes a new fake connection.</summary>
    public FakeRabbitMqConnection(string host, int port, string virtualHost, string clientProvidedName)
    {
        Host = host;
        Port = port;
        VirtualHost = virtualHost;
        ClientProvidedName = clientProvidedName;
    }

    /// <inheritdoc />
    public bool IsOpen { get; set; } = true;

    /// <inheritdoc />
    public string Host { get; }

    /// <inheritdoc />
    public int Port { get; }

    /// <inheritdoc />
    public string VirtualHost { get; }

    /// <inheritdoc />
    public string ClientProvidedName { get; }

    /// <summary>Gets how many times the connection was disposed.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>Signaled when <see cref="DisposeAsync"/> begins, before <see cref="BlockDispose"/>.</summary>
    public TaskCompletionSource? DisposeStarted { get; set; }

    /// <summary>When set, <see cref="DisposeAsync"/> waits on this source before completing.</summary>
    public TaskCompletionSource? BlockDispose { get; set; }

    /// <inheritdoc />
    public event EventHandler<RabbitMqConnectionLostEventArgs>? ConnectionLost;

    /// <summary>Gets every topology channel created on this connection.</summary>
    public List<FakeRabbitMqTopologyChannel> TopologyChannels { get; } = [];

    /// <summary>Gets every RPC channel created on this connection.</summary>
    public List<FakeRabbitMqRpcChannel> RpcChannels { get; } = [];

    /// <summary>Gets every confirm-enabled publish channel created on this connection.</summary>
    public List<FakeRabbitMqPublishChannel> PublishChannels { get; } = [];

    /// <summary>When set, <see cref="CreateRpcChannelAsync"/> throws this exception.</summary>
    public Exception? CreateRpcChannelException { get; set; }

    /// <summary>When set, <see cref="CreatePublishChannelAsync"/> throws this exception.</summary>
    public Exception? CreatePublishChannelException { get; set; }

    /// <summary>Optional configurator invoked for every newly created publish channel.</summary>
    public Action<FakeRabbitMqPublishChannel>? ConfigurePublishChannel { get; set; }

    /// <summary>When set, <see cref="CreateTopologyChannelAsync"/> throws this exception.</summary>
    public Exception? CreateTopologyChannelException { get; set; }

    /// <summary>When set, the next created topology channel throws this exception from queue declare.</summary>
    public Exception? QueueDeclareException { get; set; }

    /// <summary>When set, the next created topology channel throws this exception from exchange declare.</summary>
    public Exception? ExchangeDeclareException { get; set; }

    /// <summary>When set, the next created topology channel throws this exception from queue bind.</summary>
    public Exception? QueueBindException { get; set; }

    /// <summary>
    /// Passive-declare consumer counts copied onto each new topology channel (ArticleWork tests).
    /// </summary>
    public Dictionary<string, uint> PassiveConsumerCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, <see cref="IRabbitMqTopologyChannel.QueueDeclarePassiveAsync"/> throws on new topology channels.</summary>
    public Exception? QueueDeclarePassiveException { get; set; }

    /// <summary>
    /// Queue names for which passive declare throws <c>NOT_FOUND</c> on each new topology channel
    /// (simulates missing provider queues under BackFiller-owned topology).
    /// </summary>
    public HashSet<string> PassiveDeclareNotFoundQueues { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IRabbitMqTopologyChannel> CreateTopologyChannelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CreateTopologyChannelException is not null)
        {
            throw CreateTopologyChannelException;
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ connection is not open for topology declaration.");
        }

        var channel = new FakeRabbitMqTopologyChannel
        {
            ExchangeDeclareException = ExchangeDeclareException,
            QueueDeclareException = QueueDeclareException,
            QueueBindException = QueueBindException,
            QueueDeclarePassiveException = QueueDeclarePassiveException,
        };
        foreach (var pair in PassiveConsumerCounts)
        {
            channel.PassiveConsumerCounts[pair.Key] = pair.Value;
        }

        foreach (var queue in PassiveDeclareNotFoundQueues)
        {
            channel.PassiveDeclareNotFoundQueues.Add(queue);
        }

        TopologyChannels.Add(channel);
        return Task.FromResult<IRabbitMqTopologyChannel>(channel);
    }

    /// <inheritdoc />
    public Task<IRabbitMqRpcChannel> CreateRpcChannelAsync(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CreateRpcChannelException is not null)
        {
            throw CreateRpcChannelException;
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ connection is not open for article-work RPC.");
        }

        var channel = new FakeRabbitMqRpcChannel(generation);
        RpcChannels.Add(channel);
        return Task.FromResult<IRabbitMqRpcChannel>(channel);
    }

    /// <inheritdoc />
    public Task<IRabbitMqPublishChannel> CreatePublishChannelAsync(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CreatePublishChannelException is not null)
        {
            throw CreatePublishChannelException;
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ connection is not open for OverviewDB publication.");
        }

        var channel = new FakeRabbitMqPublishChannel(generation);
        ConfigurePublishChannel?.Invoke(channel);
        PublishChannels.Add(channel);
        return Task.FromResult<IRabbitMqPublishChannel>(channel);
    }

    /// <summary>Raises <see cref="ConnectionLost"/> as a peer-initiated disconnect.</summary>
    public void SimulateLost(ushort replyCode = 320, string replyText = "CONNECTION FORCED")
    {
        IsOpen = false;
        ConnectionLost?.Invoke(this, new RabbitMqConnectionLostEventArgs(replyCode, replyText, "Peer"));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        IsOpen = false;
        DisposeStarted?.TrySetResult();
        if (BlockDispose is not null)
        {
            await BlockDispose.Task.ConfigureAwait(false);
        }

        DisposeCount++;
    }

    /// <summary>Gets every exchange declared on any topology channel of this connection.</summary>
    public IReadOnlyList<FakeRabbitMqExchangeDeclaration> ExchangeDeclarations =>
        TopologyChannels.SelectMany(static channel => channel.Exchanges).ToList();

    /// <summary>Gets every queue declared on any topology channel of this connection.</summary>
    public IReadOnlyList<FakeRabbitMqQueueDeclaration> QueueDeclarations =>
        TopologyChannels.SelectMany(static channel => channel.Queues).ToList();

    /// <summary>Gets every binding declared on any topology channel of this connection.</summary>
    public IReadOnlyList<FakeRabbitMqBindingDeclaration> BindingDeclarations =>
        TopologyChannels.SelectMany(static channel => channel.Bindings).ToList();
}

/// <summary>In-memory declare-only RabbitMQ channel for topology tests.</summary>
internal sealed class FakeRabbitMqTopologyChannel : IRabbitMqTopologyChannel
{
    /// <summary>Gets recorded exchange declarations in call order.</summary>
    public List<FakeRabbitMqExchangeDeclaration> Exchanges { get; } = [];

    /// <summary>Gets recorded queue declarations in call order.</summary>
    public List<FakeRabbitMqQueueDeclaration> Queues { get; } = [];

    /// <summary>Gets recorded queue bindings in call order.</summary>
    public List<FakeRabbitMqBindingDeclaration> Bindings { get; } = [];

    /// <summary>Gets how many times the channel was disposed.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>When set, <see cref="ExchangeDeclareAsync"/> throws this exception.</summary>
    public Exception? ExchangeDeclareException { get; set; }

    /// <summary>When set, <see cref="QueueDeclareAsync"/> throws this exception.</summary>
    public Exception? QueueDeclareException { get; set; }

    /// <summary>When set, <see cref="QueueBindAsync"/> throws this exception.</summary>
    public Exception? QueueBindException { get; set; }

    /// <summary>Passive-declare consumer counts keyed by queue name (tests).</summary>
    public Dictionary<string, uint> PassiveConsumerCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, <see cref="QueueDeclarePassiveAsync"/> throws this exception.</summary>
    public Exception? QueueDeclarePassiveException { get; set; }

    /// <summary>Queues that throw a broker-style NOT_FOUND on passive declare.</summary>
    public HashSet<string> PassiveDeclareNotFoundQueues { get; } = new(StringComparer.Ordinal);

    /// <summary>Number of passive queue declares performed on this channel.</summary>
    public int PassiveDeclareCount { get; private set; }

    /// <summary>Passive-declare call targets in order.</summary>
    public List<string> PassiveDeclareQueues { get; } = [];

    /// <inheritdoc />
    public Task ExchangeDeclareAsync(
        string exchange,
        string type,
        bool durable,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ExchangeDeclareException is not null)
        {
            throw ExchangeDeclareException;
        }

        Exchanges.Add(new FakeRabbitMqExchangeDeclaration(
            exchange,
            type,
            durable,
            autoDelete,
            CopyArguments(arguments)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task QueueDeclareAsync(
        string queue,
        bool durable,
        bool exclusive,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (QueueDeclareException is not null)
        {
            throw QueueDeclareException;
        }

        Queues.Add(new FakeRabbitMqQueueDeclaration(
            queue,
            durable,
            exclusive,
            autoDelete,
            CopyArguments(arguments)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<RabbitMqQueueStats> QueueDeclarePassiveAsync(string queue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PassiveDeclareCount++;
        PassiveDeclareQueues.Add(queue);
        if (QueueDeclarePassiveException is not null)
        {
            throw QueueDeclarePassiveException;
        }

        if (PassiveDeclareNotFoundQueues.Contains(queue))
        {
            throw new InvalidOperationException($"NOT_FOUND - no queue '{queue}' in vhost '/'");
        }

        PassiveConsumerCounts.TryGetValue(queue, out var consumers);
        return Task.FromResult(new RabbitMqQueueStats(MessageCount: 0, ConsumerCount: consumers));
    }

    /// <inheritdoc />
    public Task QueueBindAsync(
        string queue,
        string exchange,
        string routingKey,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (QueueBindException is not null)
        {
            throw QueueBindException;
        }

        Bindings.Add(new FakeRabbitMqBindingDeclaration(
            queue,
            exchange,
            routingKey,
            CopyArguments(arguments)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    private static IReadOnlyDictionary<string, object?>? CopyArguments(
        IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        return new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
    }
}

/// <summary>In-memory RabbitMQ channel for article-work RPC tests.</summary>
internal sealed class FakeRabbitMqRpcChannel : IRabbitMqRpcChannel
{
    /// <summary>Initializes a new fake RPC channel.</summary>
    public FakeRabbitMqRpcChannel(long generation)
    {
        Generation = generation;
    }

    /// <inheritdoc />
    public long Generation { get; }

    /// <summary>Gets recorded queue declarations.</summary>
    public List<FakeRabbitMqQueueDeclaration> Queues { get; } = [];

    /// <summary>Gets recorded publications.</summary>
    public List<FakeRabbitMqRpcPublication> Publications { get; } = [];

    /// <summary>Gets how many times the channel was disposed.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>When set, <see cref="PublishAsync"/> throws this exception.</summary>
    public Exception? PublishException { get; set; }

    /// <summary>Gets the consume handler when a consumer has started.</summary>
    public Func<RabbitMqRpcDelivery, Task>? DeliveryHandler { get; private set; }

    /// <summary>Gets the most recent delivery passed to the consumer, when any.</summary>
    public RabbitMqRpcDelivery? LastDelivery { get; private set; }

    /// <summary>Gets the consumed queue name.</summary>
    public string? ConsumedQueue { get; private set; }

    /// <inheritdoc />
    public Task QueueDeclareAsync(
        string queue,
        bool durable,
        bool exclusive,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Queues.Add(new FakeRabbitMqQueueDeclaration(
            queue,
            durable,
            exclusive,
            autoDelete,
            arguments is null ? null : new Dictionary<string, object?>(arguments, StringComparer.Ordinal)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PublishAsync(
        string exchange,
        string routingKey,
        string correlationId,
        string requestId,
        string replyTo,
        string contentType,
        string expiration,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DisposeCount > 0)
        {
            throw new ObjectDisposedException(nameof(FakeRabbitMqRpcChannel));
        }

        if (PublishException is not null)
        {
            throw PublishException;
        }

        Publications.Add(new FakeRabbitMqRpcPublication(
            exchange,
            routingKey,
            correlationId,
            requestId,
            replyTo,
            contentType,
            expiration,
            body.ToArray()));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> ConsumeAsync(
        string queue,
        Func<RabbitMqRpcDelivery, Task> onDelivery,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConsumedQueue = queue;
        DeliveryHandler = onDelivery;
        return Task.FromResult("fake-consumer");
    }

    /// <summary>Delivers one message to the registered consumer.</summary>
    public Task DeliverAsync(
        string? correlationId,
        ReadOnlyMemory<byte> body,
        string? contentType = "application/json",
        string? requestId = null,
        string? expiration = ArticleWorkRpcAmqp.ExpirationMilliseconds)
    {
        if (DeliveryHandler is null)
        {
            throw new InvalidOperationException("No RPC consumer is registered.");
        }

        var delivery = new RabbitMqRpcDelivery(
            correlationId,
            requestId,
            contentType,
            expiration,
            body.ToArray(),
            Generation);
        LastDelivery = delivery;
        return DeliveryHandler(delivery);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>In-memory confirm-enabled publish channel for OverviewDB handoff tests.</summary>
internal sealed class FakeRabbitMqPublishChannel : IRabbitMqPublishChannel
{
    /// <summary>Initializes a new fake publish channel.</summary>
    public FakeRabbitMqPublishChannel(long generation)
    {
        Generation = generation;
    }

    /// <inheritdoc />
    public long Generation { get; }

    /// <inheritdoc />
    public bool IsOpen { get; set; } = true;

    /// <summary>Gets recorded publications.</summary>
    public List<FakeRabbitMqConfirmedPublication> Publications { get; } = [];

    /// <summary>Gets how many times the channel was disposed.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>When set, <see cref="PublishConfirmedAsync"/> throws this exception.</summary>
    public Exception? PublishException { get; set; }

    /// <summary>
    /// Remaining publications that throw <see cref="PublishException"/> or a default nack.
    /// After the budget is exhausted, publications succeed unless <see cref="PublishException"/> stays set
    /// and <see cref="RemainingPublishFailures"/> is zero with a sticky exception.
    /// </summary>
    public int RemainingPublishFailures { get; set; }

    /// <summary>
    /// When <see langword="true"/>, waits until <c>cancellationToken</c> is cancelled
    /// instead of completing a confirm.
    /// </summary>
    public bool HoldUntilCancelled { get; set; }

    /// <summary>
    /// When <see langword="true"/>, marks the channel closed at the start of the next
    /// publish so the not-open failure path is exercised after the channel was obtained.
    /// </summary>
    public bool CloseBeforePublish { get; set; }

    /// <summary>
    /// Optional await injected after open checks and before recording a successful confirm.
    /// Used by concurrency tests to observe in-flight publishes.
    /// </summary>
    public Func<Task>? BeforeConfirmAsync { get; set; }

    /// <inheritdoc />
    public async Task PublishConfirmedAsync(
        string exchange,
        string routingKey,
        string messageId,
        string appId,
        string expiration,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DisposeCount > 0)
        {
            throw new ObjectDisposedException(nameof(FakeRabbitMqPublishChannel));
        }

        if (CloseBeforePublish)
        {
            IsOpen = false;
        }

        if (HoldUntilCancelled)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        if (!IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ publish channel is not open.");
        }

        if (BeforeConfirmAsync is not null)
        {
            await BeforeConfirmAsync().ConfigureAwait(false);
        }

        if (RemainingPublishFailures > 0)
        {
            RemainingPublishFailures--;
            throw PublishException ?? new InvalidOperationException("RabbitMQ negatively acknowledged the OverviewDB handoff.");
        }

        if (PublishException is not null)
        {
            throw PublishException;
        }

        Publications.Add(new FakeRabbitMqConfirmedPublication(
            exchange,
            routingKey,
            messageId,
            appId,
            expiration,
            Persistent: true,
            Mandatory: OverviewDbTopology.Mandatory,
            body.ToArray()));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Recorded article-work RPC publication.</summary>
internal sealed record FakeRabbitMqRpcPublication(
    string Exchange,
    string RoutingKey,
    string CorrelationId,
    string RequestId,
    string ReplyTo,
    string ContentType,
    string Expiration,
    byte[] Body);

/// <summary>Recorded confirm-enabled OverviewDB publication.</summary>
internal sealed record FakeRabbitMqConfirmedPublication(
    string Exchange,
    string RoutingKey,
    string MessageId,
    string AppId,
    string Expiration,
    bool Persistent,
    bool Mandatory,
    byte[] Body);

/// <summary>Recorded exchange declaration.</summary>
internal sealed record FakeRabbitMqExchangeDeclaration(
    string Name,
    string Type,
    bool Durable,
    bool AutoDelete,
    IReadOnlyDictionary<string, object?>? Arguments);

/// <summary>Recorded queue declaration.</summary>
internal sealed record FakeRabbitMqQueueDeclaration(
    string Name,
    bool Durable,
    bool Exclusive,
    bool AutoDelete,
    IReadOnlyDictionary<string, object?>? Arguments);

/// <summary>Recorded queue binding.</summary>
internal sealed record FakeRabbitMqBindingDeclaration(
    string Queue,
    string Exchange,
    string RoutingKey,
    IReadOnlyDictionary<string, object?>? Arguments);
