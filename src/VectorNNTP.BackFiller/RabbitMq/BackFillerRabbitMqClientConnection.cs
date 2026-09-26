using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>Owns one RabbitMQ.Client <see cref="IConnection"/>.</summary>
internal sealed class BackFillerRabbitMqClientConnection : IBackFillerRabbitMqConnection
{
    private readonly IConnection _connection;
    private readonly ILogger _logger;
    private readonly AsyncEventHandler<ShutdownEventArgs> _shutdownHandler;
    private readonly AsyncEventHandler<CallbackExceptionEventArgs> _callbackHandler;
    private readonly AsyncEventHandler<ConnectionBlockedEventArgs> _blockedHandler;
    private readonly AsyncEventHandler<AsyncEventArgs> _unblockedHandler;
    private int _disposed;

    /// <summary>Initializes a new wrapper around an opened broker connection.</summary>
    /// <param name="connection">Opened RabbitMQ.Client connection.</param>
    /// <param name="virtualHost">Virtual host used to establish the connection.</param>
    /// <param name="logger">Connection logger. Must never receive credentials.</param>
    internal BackFillerRabbitMqClientConnection(IConnection connection, string virtualHost, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualHost);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _logger = logger;
        VirtualHost = virtualHost;
        ClientProvidedName = !string.IsNullOrWhiteSpace(connection.ClientProvidedName)
            ? connection.ClientProvidedName
            : throw new InvalidOperationException(
                "RabbitMQ connection invariant violated: IConnection.ClientProvidedName must be non-null and non-whitespace.");

        _shutdownHandler = OnShutdownAsync;
        _callbackHandler = OnCallbackExceptionAsync;
        _blockedHandler = OnBlockedAsync;
        _unblockedHandler = OnUnblockedAsync;

        _connection.ConnectionShutdownAsync += _shutdownHandler;
        _connection.CallbackExceptionAsync += _callbackHandler;
        _connection.ConnectionBlockedAsync += _blockedHandler;
        _connection.ConnectionUnblockedAsync += _unblockedHandler;
    }

    /// <inheritdoc />
    public bool IsOpen => _connection.IsOpen;

    /// <inheritdoc />
    public string Host => _connection.Endpoint.HostName;

    /// <inheritdoc />
    public int Port => _connection.Endpoint.Port;

    /// <inheritdoc />
    public string VirtualHost { get; }

    /// <inheritdoc />
    public string ClientProvidedName { get; }

    /// <inheritdoc />
    public event EventHandler<BackFillerRabbitMqConnectionLostEventArgs>? ConnectionLost;

    /// <inheritdoc />
    public async Task<IBackFillerRabbitMqChannel> CreateChannelAsync(long generation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (!_connection.IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ connection is not open for channel creation.");
        }

        var options = new CreateChannelOptions(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false);
        var channel = await _connection
            .CreateChannelAsync(options: options, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new BackFillerRabbitMqClientChannel(channel, generation);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _connection.ConnectionShutdownAsync -= _shutdownHandler;
        _connection.CallbackExceptionAsync -= _callbackHandler;
        _connection.ConnectionBlockedAsync -= _blockedHandler;
        _connection.ConnectionUnblockedAsync -= _unblockedHandler;

        try
        {
            await _connection.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BackFillerRabbitMqLogMessages.ConnectionCloseFailed(_logger, ex.Message);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private Task OnShutdownAsync(object sender, ShutdownEventArgs eventArgs)
    {
        ConnectionLost?.Invoke(
            this,
            new BackFillerRabbitMqConnectionLostEventArgs(
                eventArgs.ReplyCode,
                eventArgs.ReplyText,
                eventArgs.Initiator.ToString()));
        return Task.CompletedTask;
    }

    private Task OnCallbackExceptionAsync(object sender, CallbackExceptionEventArgs eventArgs)
    {
        BackFillerRabbitMqLogMessages.CallbackException(_logger, eventArgs.Exception.Message);
        return Task.CompletedTask;
    }

    private Task OnBlockedAsync(object sender, ConnectionBlockedEventArgs eventArgs)
    {
        BackFillerRabbitMqLogMessages.ConnectionBlocked(_logger, eventArgs.Reason);
        return Task.CompletedTask;
    }

    private Task OnUnblockedAsync(object sender, AsyncEventArgs eventArgs)
    {
        BackFillerRabbitMqLogMessages.ConnectionUnblocked(_logger);
        return Task.CompletedTask;
    }
}
