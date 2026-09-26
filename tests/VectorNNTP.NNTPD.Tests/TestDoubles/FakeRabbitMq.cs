using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.RabbitMq;

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
        };

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

    /// <inheritdoc />
    public event EventHandler<RabbitMqConnectionLostEventArgs>? ConnectionLost;

    /// <summary>Raises <see cref="ConnectionLost"/> as a peer-initiated disconnect.</summary>
    public void SimulateLost(ushort replyCode = 320, string replyText = "CONNECTION FORCED")
    {
        IsOpen = false;
        ConnectionLost?.Invoke(this, new RabbitMqConnectionLostEventArgs(replyCode, replyText, "Peer"));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
