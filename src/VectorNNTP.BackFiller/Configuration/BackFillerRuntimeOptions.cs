using System.Net;

namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Immutable validated runtime snapshot consumed by application services.
/// </summary>
/// <remarks>
/// Produced once after successful validation. Services must not re-read
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> for these values.
/// Never log a complete instance: it may contain ACME and NntpDB secrets.
/// RabbitMQ broker connectivity settings live on Common <c>RabbitMqOptions</c> /
/// <c>RabbitMqService</c>; this snapshot only carries Article Work application knobs.
/// </remarks>
internal sealed record BackFillerRuntimeOptions(
    int ServerId,
    string DnsSuffix,
    string Fqdn,
    IReadOnlyList<string> BindAddressTokens,
    IReadOnlyList<IPAddress> CanonicalBindAddresses,
    int BindPortTls,
    string LogDirectory,
    string CertificateDirectory,
    BackFillerShutdownRuntimeOptions Shutdown,
    BackFillerListenerRuntimeOptions Listener,
    BackFillerArticleRetentionRuntimeOptions ArticleRetention,
    BackFillerRabbitMqRuntimeOptions RabbitMq,
    IReadOnlyList<string> CertificateDomainNames,
    string CertificatePassword,
    NntpDbRuntimeOptions NntpDb,
    TimeSpan AccountRefreshInterval);

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
internal sealed record BackFillerShutdownRuntimeOptions(
    TimeSpan GracePeriod,
    bool DrainQueuedWork,
    bool FinishActiveArticles);

/// <summary>Validated listener bounds.</summary>
internal sealed record BackFillerListenerRuntimeOptions(
    int ParserAccumulationMaxBytes,
    TimeSpan TlsHandshakeTimeout,
    TimeSpan IoProgressTimeout,
    TimeSpan AwaitingReceiptAckTimeout,
    int MaxQueuedFoundPayloadBytes,
    int MaxActiveConnections);

/// <summary>Validated retention policy.</summary>
internal sealed record BackFillerArticleRetentionRuntimeOptions(
    long MaximumRetainedPayloadBytes,
    TimeSpan RetentionTtl,
    TimeSpan SweepInterval,
    int MaxOpenableRequestIdsPerArticle);

/// <summary>Validated NntpDB projection without exposing the raw password in property names used for logs.</summary>
/// <param name="ConnectionString">Full connection string. Secret.</param>
/// <param name="Server">Server host.</param>
/// <param name="Database">Database name.</param>
/// <param name="UserId">User id.</param>
internal sealed record NntpDbRuntimeOptions(
    string ConnectionString,
    string Server,
    string Database,
    string UserId);

/// <summary>
/// Slim Article Work RabbitMQ knobs projected from Common <c>RabbitMqOptions</c>.
/// </summary>
/// <param name="WorkRequestMaxPayloadBytes">Maximum admitted work-request envelope size.</param>
/// <param name="PublishConfirmTimeoutSeconds">Publisher-confirm wait for response publications.</param>
/// <param name="ConsumerPrefetchCount">Optional Basic.Qos prefetch for Article Work consumers.</param>
internal sealed record BackFillerRabbitMqRuntimeOptions(
    int WorkRequestMaxPayloadBytes,
    int PublishConfirmTimeoutSeconds,
    ushort? ConsumerPrefetchCount);
