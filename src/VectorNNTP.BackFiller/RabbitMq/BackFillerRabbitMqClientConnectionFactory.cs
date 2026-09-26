using System.Security.Authentication;
using RabbitMQ.Client;
using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// Maps the validated RabbitMQ snapshot onto RabbitMQ.Client and opens one broker connection.
/// </summary>
/// <remarks>
/// This type does not own the connection after <see cref="ConnectAsync"/> returns.
/// Client automatic recovery and topology recovery stay disabled so
/// <see cref="BackFillerRabbitMqService"/> remains the only lifecycle owner.
/// </remarks>
internal sealed class BackFillerRabbitMqClientConnectionFactory : IBackFillerRabbitMqConnectionFactory
{
    private readonly ILogger<BackFillerRabbitMqClientConnectionFactory> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackFillerRabbitMqClientConnectionFactory"/> class.
    /// </summary>
    /// <param name="logger">Factory logger. Must never receive credentials.</param>
    public BackFillerRabbitMqClientConnectionFactory(ILogger<BackFillerRabbitMqClientConnectionFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IBackFillerRabbitMqConnection> ConnectAsync(
        BackFillerRabbitMqRuntimeOptions options,
        string connectionName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        var factory = CreateClientFactory(options, connectionName);
        var connection = await factory.CreateConnectionAsync(options.Hosts, cancellationToken).ConfigureAwait(false);
        try
        {
            return new BackFillerRabbitMqClientConnection(connection, options.VirtualHost, _logger);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Builds a RabbitMQ.Client factory with application-managed recovery disabled.
    /// </summary>
    /// <param name="options">Validated RabbitMQ snapshot values.</param>
    /// <param name="connectionName">Client-provided name advertised to the broker.</param>
    /// <returns>A configured client factory. The password property is set when present and must not be logged.</returns>
    internal static ConnectionFactory CreateClientFactory(
        BackFillerRabbitMqRuntimeOptions options,
        string connectionName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        var factory = new ConnectionFactory
        {
            Port = options.Port,
            VirtualHost = options.VirtualHost,
            ClientProvidedName = connectionName,
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(options.NetworkRecoveryIntervalSeconds),
            RequestedHeartbeat = TimeSpan.FromSeconds(options.RequestedHeartbeatSeconds),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(options.ConnectionBlockedTimeoutSeconds),
            ContinuationTimeout = TimeSpan.FromSeconds(options.RpcTimeoutSeconds),
            HandshakeContinuationTimeout = TimeSpan.FromSeconds(options.RpcTimeoutSeconds),
            SocketReadTimeout = TimeSpan.FromSeconds(options.SocketTimeoutSeconds),
            SocketWriteTimeout = TimeSpan.FromSeconds(options.SocketTimeoutSeconds),
            RequestedChannelMax = (ushort)options.RequestedChannelMax,
        };

        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            factory.UserName = options.Username;
        }

        if (!string.IsNullOrWhiteSpace(options.Password))
        {
            factory.Password = options.Password;
        }

        factory.Ssl.Enabled = options.EnableSsl;
        if (options.EnableSsl)
        {
            factory.Ssl.Version = SslProtocols.Tls12 | SslProtocols.Tls13;
            factory.Ssl.ServerName = options.Hosts.Count > 0 ? options.Hosts[0] : string.Empty;
        }

        return factory;
    }
}
