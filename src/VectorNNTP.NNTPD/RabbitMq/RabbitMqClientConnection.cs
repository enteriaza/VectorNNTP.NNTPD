using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>Owns one RabbitMQ.Client <see cref="IConnection"/>.</summary>
internal sealed class RabbitMqClientConnection : IRabbitMqConnection
{
    private readonly IConnection _connection;
    private readonly ILogger _logger;
    private readonly AsyncEventHandler<ShutdownEventArgs> _shutdownHandler;
    private readonly AsyncEventHandler<CallbackExceptionEventArgs> _callbackHandler;
    private readonly AsyncEventHandler<ConnectionBlockedEventArgs> _blockedHandler;
    private readonly AsyncEventHandler<AsyncEventArgs> _unblockedHandler;
    private int _disposed;

    /// <summary>Initializes a new wrapper around an opened broker connection.</summary>
    internal RabbitMqClientConnection(IConnection connection, string virtualHost, ILogger logger)
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
    public event EventHandler<RabbitMqConnectionLostEventArgs>? ConnectionLost;

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
            RabbitMqLogMessages.ConnectionCloseFailed(_logger, ex);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private Task OnShutdownAsync(object sender, ShutdownEventArgs eventArgs)
    {
        ConnectionLost?.Invoke(
            this,
            new RabbitMqConnectionLostEventArgs(
                eventArgs.ReplyCode,
                eventArgs.ReplyText,
                eventArgs.Initiator.ToString()));
        return Task.CompletedTask;
    }

    private Task OnCallbackExceptionAsync(object sender, CallbackExceptionEventArgs eventArgs)
    {
        RabbitMqLogMessages.CallbackException(_logger, eventArgs.Exception.Message);
        return Task.CompletedTask;
    }

    private Task OnBlockedAsync(object sender, ConnectionBlockedEventArgs eventArgs)
    {
        RabbitMqLogMessages.ConnectionBlocked(_logger, eventArgs.Reason);
        return Task.CompletedTask;
    }

    private Task OnUnblockedAsync(object sender, AsyncEventArgs eventArgs)
    {
        RabbitMqLogMessages.ConnectionUnblocked(_logger);
        return Task.CompletedTask;
    }
}
