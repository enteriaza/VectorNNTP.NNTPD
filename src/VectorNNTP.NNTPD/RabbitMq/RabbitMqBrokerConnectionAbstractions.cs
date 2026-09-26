using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Creates RabbitMQ broker connections for infrastructure lifecycle management.
/// </summary>
internal interface IRabbitMqBrokerConnector
{
    /// <summary>
    /// Opens a broker connection using the validated runtime snapshot and client-provided connection name.
    /// </summary>
    /// <param name="runtimeOptions">Validated immutable RabbitMQ runtime options.</param>
    /// <param name="clientProvidedConnectionName">Connection name exposed to the broker for diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token for the connect attempt.</param>
    /// <returns>An owned broker connection abstraction.</returns>
    Task<IRabbitMqBrokerConnection> ConnectAsync(
        RabbitMqRuntimeOptions runtimeOptions,
        string clientProvidedConnectionName,
        CancellationToken cancellationToken);
}

/// <summary>
/// RabbitMQ broker connection abstraction used to isolate lifecycle ownership and testing seams.
/// </summary>
/// <remarks>
/// This phase exposes only connection-lifecycle members. Channel creation, topology, and messaging
/// are intentionally absent.
/// </remarks>
internal interface IRabbitMqBrokerConnection : IAsyncDisposable
{
    /// <summary>Gets a value indicating whether the underlying broker connection is currently open.</summary>
    bool IsOpen { get; }

    /// <summary>Gets the broker endpoint host name selected for the current connection.</summary>
    string EndpointHostName { get; }

    /// <summary>Gets the broker endpoint port selected for the current connection.</summary>
    int EndpointPort { get; }

    /// <summary>Gets the virtual host used to establish the current connection.</summary>
    string VirtualHost { get; }

    /// <summary>Gets the client-provided connection name visible in broker diagnostics.</summary>
    string ClientProvidedName { get; }

    /// <summary>Raised when the broker shuts the connection down.</summary>
    event EventHandler<ShutdownEventArgs>? ConnectionShutdown;

    /// <summary>Raised when RabbitMQ.Client surfaces an asynchronous callback exception.</summary>
    event EventHandler<CallbackExceptionEventArgs>? CallbackException;

    /// <summary>Raised when the broker blocks publishing or consumption on the connection.</summary>
    event EventHandler<ConnectionBlockedEventArgs>? ConnectionBlocked;

    /// <summary>Raised when the broker lifts a prior connection block.</summary>
    event EventHandler<AsyncEventArgs>? ConnectionUnblocked;

    /// <summary>Raised when RabbitMQ.Client automatic recovery reports a failure.</summary>
    event EventHandler<ConnectionRecoveryErrorEventArgs>? ConnectionRecoveryError;

    /// <summary>Raised when RabbitMQ.Client automatic recovery reports success.</summary>
    event EventHandler<AsyncEventArgs>? RecoverySucceeded;
}

/// <summary>
/// Production RabbitMQ connector that maps runtime options to RabbitMQ.Client and opens broker connections.
/// </summary>
internal sealed class RabbitMqBrokerConnector : IRabbitMqBrokerConnector
{
    /// <inheritdoc/>
    public async Task<IRabbitMqBrokerConnection> ConnectAsync(
        RabbitMqRuntimeOptions runtimeOptions,
        string clientProvidedConnectionName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientProvidedConnectionName);

        var factory = RabbitMqConnectionFactoryBuilder.BuildConnectionFactory(runtimeOptions, clientProvidedConnectionName);
        var hosts = RabbitMqConnectionFactoryBuilder.BuildHostList(runtimeOptions);

        var connection = await factory.CreateConnectionAsync(hosts, cancellationToken).ConfigureAwait(false);
        return await CreateOwnedConnectionAsync(connection, runtimeOptions.VirtualHost).ConfigureAwait(false);
    }

    /// <summary>
    /// Transfers ownership of an opened RabbitMQ connection into the adapter boundary.
    /// </summary>
    /// <param name="connection">Opened broker connection about to be owned by the adapter.</param>
    /// <param name="virtualHost">Configured virtual host used to establish the connection.</param>
    /// <returns>The owned broker connection abstraction.</returns>
    internal static async Task<IRabbitMqBrokerConnection> CreateOwnedConnectionAsync(IConnection connection, string virtualHost)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualHost);

        try
        {
            return new RabbitMqBrokerConnectionAdapter(connection, virtualHost);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Adapts RabbitMQ.Client <see cref="IConnection"/> to <see cref="IRabbitMqBrokerConnection"/>.
/// </summary>
internal sealed class RabbitMqBrokerConnectionAdapter : IRabbitMqBrokerConnection
{
    private readonly IConnection _connection;
    private readonly string _virtualHost;
    private readonly string _clientProvidedName;
    private readonly AsyncEventHandler<ShutdownEventArgs> _connectionShutdownAsyncHandler;
    private readonly AsyncEventHandler<CallbackExceptionEventArgs> _callbackExceptionAsyncHandler;
    private readonly AsyncEventHandler<ConnectionBlockedEventArgs> _connectionBlockedAsyncHandler;
    private readonly AsyncEventHandler<AsyncEventArgs> _connectionUnblockedAsyncHandler;
    private readonly AsyncEventHandler<ConnectionRecoveryErrorEventArgs> _connectionRecoveryErrorAsyncHandler;
    private readonly AsyncEventHandler<AsyncEventArgs> _recoverySucceededAsyncHandler;

    /// <summary>
    /// Initializes a new adapter for a live RabbitMQ connection.
    /// </summary>
    /// <param name="connection">Connected RabbitMQ connection owned by the adapter.</param>
    /// <param name="virtualHost">Configured virtual host used to establish the connection.</param>
    internal RabbitMqBrokerConnectionAdapter(IConnection connection, string virtualHost)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _virtualHost = !string.IsNullOrWhiteSpace(virtualHost)
            ? virtualHost
            : throw new ArgumentException("Virtual host is required.", nameof(virtualHost));
        _clientProvidedName = !string.IsNullOrWhiteSpace(_connection.ClientProvidedName)
            ? _connection.ClientProvidedName
            : throw new InvalidOperationException(
                "RabbitMQ connection invariant violated: IConnection.ClientProvidedName must be non-null and non-whitespace.");

        _connectionShutdownAsyncHandler = (sender, args) =>
        {
            ConnectionShutdown?.Invoke(sender, args);
            return Task.CompletedTask;
        };

        _callbackExceptionAsyncHandler = (sender, args) =>
        {
            CallbackException?.Invoke(sender, args);
            return Task.CompletedTask;
        };

        _connectionBlockedAsyncHandler = (sender, args) =>
        {
            ConnectionBlocked?.Invoke(sender, args);
            return Task.CompletedTask;
        };

        _connectionUnblockedAsyncHandler = (sender, args) =>
        {
            ConnectionUnblocked?.Invoke(sender, args);
            return Task.CompletedTask;
        };

        _connectionRecoveryErrorAsyncHandler = (sender, args) =>
        {
            ConnectionRecoveryError?.Invoke(sender, args);
            return Task.CompletedTask;
        };

        _recoverySucceededAsyncHandler = (sender, args) =>
        {
            RecoverySucceeded?.Invoke(sender, args);
            return Task.CompletedTask;
        };

        _connection.ConnectionShutdownAsync += _connectionShutdownAsyncHandler;
        _connection.CallbackExceptionAsync += _callbackExceptionAsyncHandler;
        _connection.ConnectionBlockedAsync += _connectionBlockedAsyncHandler;
        _connection.ConnectionUnblockedAsync += _connectionUnblockedAsyncHandler;
        _connection.ConnectionRecoveryErrorAsync += _connectionRecoveryErrorAsyncHandler;
        _connection.RecoverySucceededAsync += _recoverySucceededAsyncHandler;
    }

    /// <inheritdoc/>
    public bool IsOpen => _connection.IsOpen;

    /// <inheritdoc/>
    public string EndpointHostName => _connection.Endpoint.HostName;

    /// <inheritdoc/>
    public int EndpointPort => _connection.Endpoint.Port;

    /// <inheritdoc/>
    public string VirtualHost => _virtualHost;

    /// <inheritdoc/>
    public string ClientProvidedName => _clientProvidedName;

    /// <inheritdoc/>
    public event EventHandler<ShutdownEventArgs>? ConnectionShutdown;

    /// <inheritdoc/>
    public event EventHandler<CallbackExceptionEventArgs>? CallbackException;

    /// <inheritdoc/>
    public event EventHandler<ConnectionBlockedEventArgs>? ConnectionBlocked;

    /// <inheritdoc/>
    public event EventHandler<AsyncEventArgs>? ConnectionUnblocked;

    /// <inheritdoc/>
    public event EventHandler<ConnectionRecoveryErrorEventArgs>? ConnectionRecoveryError;

    /// <inheritdoc/>
    public event EventHandler<AsyncEventArgs>? RecoverySucceeded;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _connection.ConnectionShutdownAsync -= _connectionShutdownAsyncHandler;
        _connection.CallbackExceptionAsync -= _callbackExceptionAsyncHandler;
        _connection.ConnectionBlockedAsync -= _connectionBlockedAsyncHandler;
        _connection.ConnectionUnblockedAsync -= _connectionUnblockedAsyncHandler;
        _connection.ConnectionRecoveryErrorAsync -= _connectionRecoveryErrorAsyncHandler;
        _connection.RecoverySucceededAsync -= _recoverySucceededAsyncHandler;

        await _connection.CloseAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
