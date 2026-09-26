using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.RabbitMq;

namespace VectorNNTP.NNTPD.Tests.TestDoubles;

/// <summary>In-memory RabbitMQ connector for offline tests.</summary>
internal sealed class FakeRabbitMqBrokerConnector : IRabbitMqBrokerConnector
{
    private int _connectCount;

    /// <summary>Gets the number of connect attempts.</summary>
    public int ConnectCount => Volatile.Read(ref _connectCount);

    /// <summary>Gets the most recently created connection.</summary>
    public FakeRabbitMqBrokerConnection? LastConnection { get; private set; }

    /// <summary>Gets every connection created by this connector.</summary>
    public List<FakeRabbitMqBrokerConnection> Connections { get; } = [];

    /// <summary>Optional exception thrown by the next connect attempt.</summary>
    public Exception? ConnectException { get; set; }

    /// <summary>When set, connect waits on this source before completing.</summary>
    public TaskCompletionSource? BlockConnect { get; set; }

    /// <summary>Signaled after each successful connect with the new <see cref="ConnectCount"/>.</summary>
    public TaskCompletionSource<int>? Connected { get; set; }

    /// <summary>When <see langword="true"/>, created connections report <c>IsOpen == false</c>.</summary>
    public bool ReturnUnusableConnection { get; set; }

    /// <inheritdoc />
    public async Task<IRabbitMqBrokerConnection> ConnectAsync(
        RabbitMqRuntimeOptions runtimeOptions,
        string clientProvidedConnectionName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientProvidedConnectionName);

        if (BlockConnect is not null)
        {
            await BlockConnect.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (ConnectException is not null)
        {
            throw ConnectException;
        }

        var count = Interlocked.Increment(ref _connectCount);
        var connection = new FakeRabbitMqBrokerConnection(
            runtimeOptions.Hosts.Count > 0 ? runtimeOptions.Hosts[0] : "127.0.0.1",
            runtimeOptions.Port,
            runtimeOptions.VirtualHost,
            clientProvidedConnectionName)
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
internal sealed class FakeRabbitMqBrokerConnection : IRabbitMqBrokerConnection
{
    /// <summary>Initializes a new fake connection.</summary>
    public FakeRabbitMqBrokerConnection(string host, int port, string virtualHost, string clientProvidedName)
    {
        EndpointHostName = host;
        EndpointPort = port;
        VirtualHost = virtualHost;
        ClientProvidedName = clientProvidedName;
    }

    /// <inheritdoc />
    public bool IsOpen { get; set; } = true;

    /// <inheritdoc />
    public string EndpointHostName { get; }

    /// <inheritdoc />
    public int EndpointPort { get; }

    /// <inheritdoc />
    public string VirtualHost { get; }

    /// <inheritdoc />
    public string ClientProvidedName { get; }

    /// <summary>Gets how many times the connection was disposed.</summary>
    public int DisposeCount { get; private set; }

    /// <inheritdoc />
    public event EventHandler<ShutdownEventArgs>? ConnectionShutdown;

    /// <inheritdoc />
    public event EventHandler<CallbackExceptionEventArgs>? CallbackException;

    /// <inheritdoc />
    public event EventHandler<ConnectionBlockedEventArgs>? ConnectionBlocked;

    /// <inheritdoc />
    public event EventHandler<AsyncEventArgs>? ConnectionUnblocked;

    /// <inheritdoc />
    public event EventHandler<ConnectionRecoveryErrorEventArgs>? ConnectionRecoveryError;

    /// <inheritdoc />
    public event EventHandler<AsyncEventArgs>? RecoverySucceeded;

    /// <summary>Raises <see cref="ConnectionShutdown"/> as a peer-initiated disconnect.</summary>
    public void SimulateShutdown(ushort replyCode = 320, string replyText = "CONNECTION FORCED")
    {
        IsOpen = false;
        ConnectionShutdown?.Invoke(this, new ShutdownEventArgs(ShutdownInitiator.Peer, replyCode, replyText));
    }

    /// <summary>Raises the remaining connection events so the fake implements the full lifecycle seam.</summary>
    public void SimulateLifecycleSignals()
    {
        CallbackException?.Invoke(this, null!);
        ConnectionBlocked?.Invoke(this, null!);
        ConnectionUnblocked?.Invoke(this, AsyncEventArgs.Empty);
        ConnectionRecoveryError?.Invoke(this, null!);
        RecoverySucceeded?.Invoke(this, AsyncEventArgs.Empty);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
