using System.Security.Authentication;
using RabbitMQ.Client;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Converts validated RabbitMQ runtime options into client-library connection settings and log-safe snapshots.
/// </summary>
internal static class RabbitMqConnectionFactoryBuilder
{
    /// <summary>
    /// Builds a RabbitMQ <see cref="ConnectionFactory"/> configured from the validated runtime snapshot.
    /// </summary>
    /// <param name="options">Validated immutable RabbitMQ runtime options.</param>
    /// <param name="clientProvidedConnectionName">Application-supplied connection name exposed by the broker.</param>
    /// <returns>A connection factory with application-managed recovery and the configured timeouts, heartbeats, and TLS settings.</returns>
    internal static ConnectionFactory BuildConnectionFactory(
        RabbitMqRuntimeOptions options,
        string clientProvidedConnectionName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientProvidedConnectionName);

        var factory = new ConnectionFactory
        {
            Port = options.Port,
            VirtualHost = options.VirtualHost,
            ClientProvidedName = clientProvidedConnectionName,
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

    /// <summary>
    /// Returns the ordered broker host list used when opening a RabbitMQ connection.
    /// </summary>
    /// <param name="options">Validated immutable RabbitMQ runtime options.</param>
    /// <returns>The distinct host list preserved in the validated connection-preference order.</returns>
    internal static IReadOnlyList<string> BuildHostList(RabbitMqRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Hosts;
    }

    /// <summary>
    /// Builds a sanitized settings snapshot suitable for structured logging and diagnostics.
    /// </summary>
    /// <param name="options">Validated immutable RabbitMQ runtime options.</param>
    /// <param name="clientProvidedConnectionName">Application-supplied connection name exposed by the broker.</param>
    /// <returns>A snapshot that preserves operational settings without copying credential material into logs.</returns>
    internal static RabbitMqConnectionFactorySnapshot BuildSanitizedSnapshot(
        RabbitMqRuntimeOptions options,
        string clientProvidedConnectionName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientProvidedConnectionName);

        return new RabbitMqConnectionFactorySnapshot(
            Hosts: [.. options.Hosts],
            Port: options.Port,
            VirtualHost: options.VirtualHost,
            EnableSsl: options.EnableSsl,
            RequestedHeartbeatSeconds: options.RequestedHeartbeatSeconds,
            RequestedConnectionTimeoutSeconds: options.ConnectionBlockedTimeoutSeconds,
            RpcTimeoutSeconds: options.RpcTimeoutSeconds,
            SocketTimeoutSeconds: options.SocketTimeoutSeconds,
            RequestedChannelMax: options.RequestedChannelMax,
            AutomaticRecoveryEnabled: false,
            TopologyRecoveryEnabled: false,
            NetworkRecoveryIntervalSeconds: options.NetworkRecoveryIntervalSeconds,
            ClientProvidedConnectionName: clientProvidedConnectionName,
            UsesUsernameAuthentication: !string.IsNullOrWhiteSpace(options.Username),
            HasPassword: !string.IsNullOrWhiteSpace(options.Password));
    }
}

/// <summary>
/// Sanitized RabbitMQ connection-factory settings that can be emitted in logs without exposing secrets.
/// </summary>
/// <param name="Hosts">Configured broker hosts in connection-attempt order.</param>
/// <param name="Port">Configured broker port.</param>
/// <param name="VirtualHost">Configured RabbitMQ virtual host.</param>
/// <param name="EnableSsl">Indicates whether TLS is requested for the broker connection.</param>
/// <param name="RequestedHeartbeatSeconds">Requested broker heartbeat interval in seconds.</param>
/// <param name="RequestedConnectionTimeoutSeconds">Requested AMQP connection timeout in seconds.</param>
/// <param name="RpcTimeoutSeconds">Requested continuation and handshake RPC timeout in seconds.</param>
/// <param name="SocketTimeoutSeconds">Configured socket read and write timeout in seconds.</param>
/// <param name="RequestedChannelMax">Requested maximum channel count.</param>
/// <param name="AutomaticRecoveryEnabled">Indicates whether client automatic connection recovery is enabled.</param>
/// <param name="TopologyRecoveryEnabled">Indicates whether client automatic topology recovery is enabled.</param>
/// <param name="NetworkRecoveryIntervalSeconds">Configured client recovery interval in seconds.</param>
/// <param name="ClientProvidedConnectionName">Connection name that will be advertised to the broker.</param>
/// <param name="UsesUsernameAuthentication">Indicates whether a username was configured.</param>
/// <param name="HasPassword">Indicates whether a password value was configured.</param>
internal sealed record RabbitMqConnectionFactorySnapshot(
    IReadOnlyList<string> Hosts,
    int Port,
    string VirtualHost,
    bool EnableSsl,
    int RequestedHeartbeatSeconds,
    int RequestedConnectionTimeoutSeconds,
    int RpcTimeoutSeconds,
    int SocketTimeoutSeconds,
    int RequestedChannelMax,
    bool AutomaticRecoveryEnabled,
    bool TopologyRecoveryEnabled,
    int NetworkRecoveryIntervalSeconds,
    string ClientProvidedConnectionName,
    bool UsesUsernameAuthentication,
    bool HasPassword);
