using System.Net;

namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Immutable validated runtime snapshot consumed by application services.
/// </summary>
/// <remarks>
/// Produced once after successful validation. Services must not re-read
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> for these values.
/// Never log a complete instance: it contains RabbitMQ, ACME, and GrabberDB secrets.
/// </remarks>
public sealed record BackFillerRuntimeOptions(
    string Name,
    int ServerId,
    string DnsSuffix,
    string Fqdn,
    IReadOnlyList<string> BindAddressTokens,
    IReadOnlyList<IPAddress> CanonicalBindAddresses,
    int BindPort,
    string LogDirectory,
    string CertificateDirectory,
    BackFillerShutdownRuntimeOptions Shutdown,
    BackFillerListenerRuntimeOptions Listener,
    BackFillerArticleRetentionRuntimeOptions ArticleRetention,
    BackFillerTransitServerRuntimeOptions TransitServer,
    BackFillerRabbitMqRuntimeOptions RabbitMq,
    IReadOnlyList<string> CertificateDomainNames,
    string CertificatePassword,
    GrabberDbRuntimeOptions GrabberDb,
    BackFillerAccountsRuntimeOptions Accounts);

/// <summary>Validated shutdown policy captured in the runtime snapshot.</summary>
/// <param name="GracePeriod">Complete application shutdown budget. Also drives host <c>ShutdownTimeout</c>.</param>
/// <param name="DrainQueuedWork">
/// When <see langword="true"/>, admitted-but-not-yet-started Article Work may acquire
/// the session dispatch lock and start. When <see langword="false"/>, that work is
/// cancelled without starting and settled as <c>Cancelled</c> (NACK requeue).
/// </param>
/// <param name="FinishActiveArticles">
/// When <see langword="true"/>, work that has entered <c>ProcessAsync</c> may finish
/// inside <paramref name="GracePeriod"/>. When <see langword="false"/>, that work is
/// cooperatively cancelled through the existing pipeline.
/// </param>
public sealed record BackFillerShutdownRuntimeOptions(
    TimeSpan GracePeriod,
    bool DrainQueuedWork,
    bool FinishActiveArticles);

/// <summary>Validated listener bounds.</summary>
public sealed record BackFillerListenerRuntimeOptions(
    int ParserAccumulationMaxBytes,
    TimeSpan TlsHandshakeTimeout,
    TimeSpan IoProgressTimeout,
    TimeSpan AwaitingReceiptAckTimeout,
    int MaxQueuedFoundPayloadBytes,
    int MaxActiveConnections);

/// <summary>Validated retention policy.</summary>
public sealed record BackFillerArticleRetentionRuntimeOptions(
    long MaximumRetainedPayloadBytes,
    TimeSpan RetentionTtl,
    TimeSpan SweepInterval);

/// <summary>Validated TransitServer endpoint.</summary>
public sealed record BackFillerTransitServerRuntimeOptions(
    string Host,
    int Port,
    bool UseSsl);

/// <summary>Validated GrabberDB projection without exposing the raw password in property names used for logs.</summary>
/// <param name="ConnectionString">Full connection string. Secret.</param>
/// <param name="Server">Server host.</param>
/// <param name="Database">Database name.</param>
/// <param name="UserId">User id.</param>
public sealed record GrabberDbRuntimeOptions(
    string ConnectionString,
    string Server,
    string Database,
    string UserId);

/// <summary>Validated RabbitMQ runtime projection.</summary>
public sealed record BackFillerRabbitMqRuntimeOptions(
    IReadOnlyList<string> Hosts,
    int Port,
    string? Username,
    string? Password,
    string VirtualHost,
    bool EnableSsl,
    int WorkRequestMaxPayloadBytes,
    int ChannelLeaseTimeoutSeconds,
    int RpcTimeoutSeconds,
    int ConnectionBlockedTimeoutSeconds,
    int ChannelPoolSize,
    int MinConnections,
    int MaxConnections,
    int MaxConsecutiveRecoveryFailures,
    int MaxPendingLeaseWaiters,
    int ConnectionScaleDownIdleSeconds,
    int ScaleDownCooldownSeconds,
    int NetworkRecoveryIntervalSeconds,
    int PoolReconnectBaseDelayMs,
    int PoolReconnectMaxDelayMs,
    int MinimumConnectionLifetimeSeconds,
    int PublishConfirmTimeoutSeconds,
    int MaximumShutdownDrainTimeoutSeconds,
    double DegradedThreshold,
    int UnhealthyThreshold,
    int RequestedHeartbeatSeconds,
    int SocketTimeoutSeconds,
    int RequestedChannelMax,
    ushort? ConsumerPrefetchCount,
    string? DiagnosticPayloadCorrelationId);

/// <summary>Validated MySQL provider-account control-plane policy.</summary>
public sealed record BackFillerAccountsRuntimeOptions(
    TimeSpan RefreshInterval,
    TimeSpan CommandTimeout);
