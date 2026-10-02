using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace VectorNNTP.Common.Messaging.RabbitMq;

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
    /// <summary>Hostname used when the operating system does not report one.</summary>
    internal const string UnknownHostName = "unknown-host";

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

        PopulateClientProperties(factory);
        return factory;
    }

    /// <summary>
    /// Resolves a hostname, using <see cref="UnknownHostName"/> when the reported name is missing.
    /// </summary>
    /// <param name="readMachineName">Reads the operating-system hostname.</param>
    /// <returns>A non-empty hostname.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="readMachineName"/> is null.</exception>
    internal static string ResolveClientMachineName(Func<string?> readMachineName)
    {
        ArgumentNullException.ThrowIfNull(readMachineName);

        string? name;
        try
        {
            name = readMachineName();
        }
        catch (InvalidOperationException)
        {
            name = null;
        }

        return string.IsNullOrWhiteSpace(name) ? UnknownHostName : name;
    }

    /// <summary>
    /// Writes diagnostic AMQP client properties for the RabbitMQ management UI.
    /// </summary>
    /// <param name="factory">Factory whose <see cref="ConnectionFactory.ClientProperties"/> are updated.</param>
    /// <remarks>
    /// Library defaults <c>product</c>, <c>version</c>, <c>copyright</c>, and <c>information</c> stay in place.
    /// <c>platform</c> is replaced with <see cref="Environment.OSVersion"/>.
    /// The application name and compiled version come from <see cref="Assembly.GetEntryAssembly"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The entry assembly has no short name.</exception>
    private static void PopulateClientProperties(ConnectionFactory factory)
    {
        var applicationName = ResolveEntryApplicationName();
        var machineName = ResolveClientMachineName(static () => Environment.MachineName);
        var processId = Environment.ProcessId;
        factory.ClientProperties["application"] =
            $"{applicationName}-{machineName}-{processId}".ToUpperInvariant();
        factory.ClientProperties["application_version"] = ResolveEntryApplicationVersion();
        factory.ClientProperties["connected_at"] =
            DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        factory.ClientProperties["machine"] = machineName;
        factory.ClientProperties["os_version"] = Environment.OSVersion.VersionString;
        factory.ClientProperties["platform"] = Environment.OSVersion.Platform.ToString();
        factory.ClientProperties["process_id"] = processId.ToString(CultureInfo.InvariantCulture);
        factory.ClientProperties["runtime"] = RuntimeInformation.FrameworkDescription;
    }

    /// <summary>Returns the entry assembly short name.</summary>
    /// <returns>The entry assembly's <see cref="AssemblyName.Name"/>.</returns>
    /// <exception cref="InvalidOperationException">The entry assembly or its name is missing.</exception>
    private static string ResolveEntryApplicationName()
    {
        var name = Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                "The entry assembly name is required for RabbitMQ client properties.");
        }

        return name;
    }

    /// <summary>Returns the entry assembly's compiled version.</summary>
    /// <returns>
    /// <see cref="Version.ToString()"/> for the entry assembly, or <c>0.0.0</c> when that version is absent.
    /// </returns>
    private static string ResolveEntryApplicationVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version?.ToString() ?? "0.0.0";
    }
}
