using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Maps validated RabbitMQ options onto RabbitMQ.Client and opens one broker connection.
/// </summary>
/// <remarks>
/// This type does not own the connection after <see cref="ConnectAsync"/> returns.
/// Client automatic recovery and topology recovery stay disabled so
/// <see cref="RabbitMqService"/> remains the only lifecycle owner.
/// </remarks>
public sealed class RabbitMqClientConnectionFactory : IRabbitMqConnectionFactory
{
    private readonly ILogger<RabbitMqClientConnectionFactory> _logger;

    /// <summary>Initializes a new instance of the <see cref="RabbitMqClientConnectionFactory"/> class.</summary>
    public RabbitMqClientConnectionFactory(ILogger<RabbitMqClientConnectionFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IRabbitMqConnection> ConnectAsync(
        RabbitMqOptions options,
        string connectionName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        var runtime = options.ToRuntimeOptions();
        var factory = CreateClientFactory(runtime, connectionName);
        var connection = await factory.CreateConnectionAsync(runtime.Hosts, cancellationToken).ConfigureAwait(false);
        try
        {
            return new RabbitMqClientConnection(connection, runtime.VirtualHost, _logger);
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
    internal static ConnectionFactory CreateClientFactory(
        RabbitMqRuntimeOptions options,
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
