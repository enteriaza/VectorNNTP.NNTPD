using System.ComponentModel.DataAnnotations;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Bindable BackFiller configuration under section <see cref="SectionName"/>.
/// </summary>
/// <remarks>
/// <para>
/// Property names are PascalCase. Generated <see cref="Fqdn"/> cannot be bound.
/// Never log a complete instance: RabbitMQ and GrabberDB secrets live here.
/// Cloudflare and ACME PKCS#12 secrets stay on root <c>VECTOR__*</c> keys and
/// are copied onto <see cref="AcmeCloudflareOptions"/> by
/// <see cref="BackFillerAcmeCloudflareOptionsAdapter"/>.
/// </para>
/// <para>
/// Bind, ACME directory, and DNS-suffix values are BackFiller-owned and bind
/// only from section <see cref="SectionName"/>. Root-level
/// <c>BindAddress</c>, <c>BindPort</c>, <c>BindPortTls</c>, <c>DnsSuffix</c>,
/// and ACME directory keys are not used. There is no
/// <c>BackFiller:BindPort</c>; the listener port is
/// <see cref="BindPortTls"/> only. <see cref="ServerId"/> binds from
/// <c>BackFiller:ServerId</c> only. The FQDN is
/// <c>backfiller{ServerId:00}.{DnsSuffix}</c>; there is no configurable Name.
/// RabbitMQ uses
/// <c>VECTOR__RABBITMQ__*</c>. GrabberDB uses
/// <c>VECTOR__CONNECTIONSTRINGS__GRABBERDB</c>. Cloudflare secrets use
/// <c>VECTOR__CLOUDFLAREAPIKEY</c>, <c>VECTOR__CLOUDFLAREZONEID</c>,
/// <c>VECTOR__ACMECERTIFICATEPASSWORD</c>, and
/// <c>VECTOR__ACMEACCOUNT</c>.
/// </para>
/// <para>
/// Intentional key rename from the old worker: <c>BackFiller:Id</c> is now
/// <c>BackFiller:ServerId</c>. <c>DirLogs</c> is <see cref="LogDirectory"/>;
/// <c>DirCerts</c> is <see cref="CertificateDirectory"/> /
/// <see cref="AcmeStateDir"/>.
/// </para>
/// </remarks>
public sealed class BackFillerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "BackFiller";

    /// <summary>Canonical VectorNNTP environment-variable prefix.</summary>
    public const string EnvironmentVariablePrefix = VectorEnvironment.Prefix;

    /// <summary>Fixed FQDN host-label prefix. Not configuration.</summary>
    public const string ApplicationPrefix = "backfiller";

    /// <summary>Canonical environment variable that supplies RabbitMQ username.</summary>
    public const string RabbitMqUsernameEnvironmentVariable = "VECTOR__RABBITMQ__USERNAME";

    /// <summary>Canonical environment variable that supplies RabbitMQ password.</summary>
    public const string RabbitMqPasswordEnvironmentVariable = "VECTOR__RABBITMQ__PASSWORD";

    /// <summary>Canonical environment variable that supplies GrabberDB.</summary>
    public const string GrabberDbEnvironmentVariable = "VECTOR__CONNECTIONSTRINGS__GRABBERDB";

    /// <summary>Default DNS suffix when the key is omitted.</summary>
    public const string DefaultDnsSuffix = "usenet.ninja";

    /// <summary>Default relative log directory.</summary>
    public const string DefaultLogDirectory = "logs";

    /// <summary>Default relative certificate directory.</summary>
    public const string DefaultCertificateDirectory = "certs";

    /// <summary>Default Let's Encrypt staging ACME directory URL.</summary>
    public const string DefaultAcmeDirectoryUrl = AcmeCloudflareOptions.DefaultAcmeDirectoryUrl;

    /// <summary>Default relative ACME state directory.</summary>
    public const string DefaultAcmeStateDir = AcmeCloudflareOptions.DefaultAcmeStateDir;

    /// <summary>Default certificate renewal lead time in days.</summary>
    public const int DefaultAcmeRenewalThresholdDays = AcmeCloudflareOptions.DefaultAcmeRenewalThresholdDays;

    /// <summary>Default MySQL account-refresh poll interval in seconds.</summary>
    public const int DefaultAccountRefreshIntervalSeconds = 60;

    /// <summary>Minimum MySQL account-refresh poll interval in seconds.</summary>
    public const int MinimumAccountRefreshIntervalSeconds = 5;

    /// <summary>Maximum MySQL account-refresh poll interval in seconds.</summary>
    public const int MaximumAccountRefreshIntervalSeconds = 3600;

    /// <summary>
    /// Gets or sets the BackFiller server identifier.
    /// </summary>
    /// <remarks>
    /// Required. No default. Must be explicitly configured as an integer in
    /// <c>1–99</c> via <c>BackFiller:ServerId</c> (same bounds as NNTPD).
    /// Typed as <see cref="Nullable{T}"/> so a missing value remains distinguishable
    /// from an explicit <c>0</c> (both fail validation).
    /// </remarks>
    [Required]
    [Range(ServerIdRules.MinimumInclusive, ServerIdRules.MaximumInclusive)]
    public int? ServerId { get; set; }

    /// <summary>
    /// Gets or sets the DNS suffix used to generate <see cref="Fqdn"/>.
    /// </summary>
    public string DnsSuffix { get; set; } = DefaultDnsSuffix;

    /// <summary>
    /// Gets or sets the Cloudflare zone identifier.
    /// </summary>
    /// <remarks>
    /// Required. Binds from <c>BackFiller:CloudFlareZoneId</c> only.
    /// </remarks>
    public string CloudFlareZoneId { get; set; } = string.Empty;

    /// <summary>
    /// Gets the generated FQDN <c>backfiller{ServerId:00}.{DnsSuffix}</c>.
    /// </summary>
    /// <remarks>Not independently configurable. The <c>backfiller</c> prefix is fixed.</remarks>
    public string Fqdn =>
        ServerId is { } serverId
        && ServerIdRules.IsInRange(serverId)
        && !string.IsNullOrWhiteSpace(DnsSuffix)
            ? ApplicationFqdn.Build(ApplicationPrefix, serverId, DnsSuffix)
            : string.Empty;

    /// <summary>
    /// Gets or sets listener bind addresses.
    /// </summary>
    /// <remarks>
    /// Optional. Omitted or empty means all interfaces (IPv4 and IPv6 any-address).
    /// Explicit addresses must be locally assigned. Wildcards: <c>*</c>, <c>0.0.0.0</c>, <c>::</c>.
    /// </remarks>
    public string[]? BindAddress { get; set; }

    /// <summary>
    /// Gets or sets the TLS TCP port used by the Cache Listener.
    /// </summary>
    /// <remarks>
    /// Required. Range 1–65535. BackFiller is TLS-only; there is no cleartext fallback.
    /// Nullable so missing is distinct from 0.
    /// </remarks>
    public int? BindPortTls { get; set; }

    /// <summary>
    /// Gets or sets the ACME directory URL.
    /// </summary>
    /// <remarks>Absolute HTTPS URL. Default is Let's Encrypt staging.</remarks>
    public string AcmeDirectoryUrl { get; set; } = DefaultAcmeDirectoryUrl;

    /// <summary>
    /// Gets or sets how many days before expiry a certificate is due for renewal.
    /// </summary>
    public int AcmeRenewalThresholdDays { get; set; } = DefaultAcmeRenewalThresholdDays;

    /// <summary>
    /// Gets or sets the ACME account and certificate state directory.
    /// </summary>
    /// <remarks>
    /// Relative paths resolve against the host content root.
    /// When empty, <see cref="CertificateDirectory"/> is used.
    /// </remarks>
    public string AcmeStateDir { get; set; } = DefaultAcmeStateDir;

    /// <summary>
    /// Gets or sets the directory used for application log files.
    /// </summary>
    /// <remarks>
    /// Old key: <c>DirLogs</c>. Relative paths resolve against the host content root
    /// (<see cref="AppContext.BaseDirectory"/> by default), not the process working directory.
    /// The current Serilog configuration writes to stdout only; this directory is reserved
    /// for operators and future file sinks.
    /// </remarks>
    public string LogDirectory { get; set; } = DefaultLogDirectory;

    /// <summary>
    /// Gets or sets the directory used for ACME and TLS certificate artifacts.
    /// </summary>
    /// <remarks>
    /// Old key: <c>DirCerts</c>. Relative paths resolve against the host content root.
    /// The Listener loads <c>backfiller-listener.pfx</c> from this directory.
    /// </remarks>
    public string CertificateDirectory { get; set; } = DefaultCertificateDirectory;

    /// <summary>
    /// Gets or sets RabbitMQ settings.
    /// </summary>
    public BackFillerRabbitMqOptions RabbitMQ { get; set; } = new();

    /// <summary>
    /// Gets or sets TransitServer connection settings.
    /// </summary>
    public BackFillerTransitServerOptions TransitServer { get; set; } = new();

    /// <summary>
    /// Gets or sets in-memory article retention settings.
    /// </summary>
    public BackFillerArticleRetentionOptions ArticleRetention { get; set; } = new();

    /// <summary>
    /// Gets or sets graceful shutdown policy.
    /// </summary>
    public BackFillerShutdownOptions Shutdown { get; set; } = new();

    /// <summary>
    /// Gets or sets Listener resource-safety bounds.
    /// </summary>
    public BackFillerListenerOptions Listener { get; set; } = new();

    /// <summary>
    /// Gets or sets how often BackFiller polls GrabberDB for
    /// <c>nntpbackfilleraccounts</c> changes.
    /// </summary>
    /// <remarks>
    /// Default 60. Range 5–3600. This is not the per-account NNTP DATE
    /// keepalive; that value comes from each MySQL <c>keepalive</c> column.
    /// There is no dedicated environment-variable mapping for this key.
    /// </remarks>
    public int BackFillerAccountRefreshIntervalSeconds { get; set; } = DefaultAccountRefreshIntervalSeconds;

    /// <summary>
    /// Returns whether a bind-address token is a wildcard.
    /// </summary>
    /// <param name="entry">Configured token.</param>
    /// <returns><see langword="true"/> for all-interface wildcards.</returns>
    public static bool IsBindAddressWildcard(string entry) =>
        AcmeCloudflareOptions.IsBindAddressWildcard(entry);
}

/// <summary>Graceful shutdown policy.</summary>
public sealed class BackFillerShutdownOptions
{
    /// <summary>Minimum grace period in seconds.</summary>
    public const int MinimumGracePeriodSeconds = 5;

    /// <summary>Maximum grace period in seconds.</summary>
    public const int MaximumGracePeriodSeconds = 600;

    /// <summary>
    /// Gets or sets the complete application shutdown budget in seconds.
    /// </summary>
    /// <remarks>Used for worker drain and Generic Host <c>ShutdownTimeout</c>. Default 30. Range 5–600.</remarks>
    public int GracePeriodSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets whether already-admitted queued (not-yet-started) Article Work may
    /// proceed to start during shutdown.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="FinishActiveArticles"/>. When <see langword="false"/>,
    /// admitted work that has not entered <c>ProcessAsync</c> is settled as cancelled
    /// (NACK requeue) and does not start. Broker-prefetched deliveries that were never
    /// admitted are not drained by this setting.
    /// </remarks>
    public bool DrainQueuedWork { get; set; } = true;

    /// <summary>
    /// Gets or sets whether Article Work that has already started processing may finish
    /// during shutdown.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="DrainQueuedWork"/>. When <see langword="false"/>, active
    /// work is cooperatively cancelled through the existing pipeline. Cancellation never
    /// ACKs the RabbitMQ delivery.
    /// </remarks>
    public bool FinishActiveArticles { get; set; } = true;
}

/// <summary>Listener resource-safety bounds.</summary>
public sealed class BackFillerListenerOptions
{
    /// <summary>Maximum incomplete inbound protocol bytes per connection.</summary>
    public int ParserAccumulationMaxBytes { get; set; } = 262144;

    /// <summary>TLS handshake timeout in seconds.</summary>
    public int TlsHandshakeTimeoutSeconds { get; set; } = 30;

    /// <summary>Per-operation I/O no-progress timeout in seconds.</summary>
    public int IoProgressTimeoutSeconds { get; set; } = 60;

    /// <summary>ReceiptAck wait after a completed Found transfer, in seconds.</summary>
    public int AwaitingReceiptAckTimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum queued Found payload bytes per connection.</summary>
    public int MaxQueuedFoundPayloadBytes { get; set; } = 67108864;

    /// <summary>Maximum concurrently active accepted connections.</summary>
    public int MaxActiveConnections { get; set; } = 1024;
}

/// <summary>In-memory article retention policy.</summary>
public sealed class BackFillerArticleRetentionOptions
{
    /// <summary>Bytes in one gibibyte.</summary>
    public const long BytesPerGibibyte = 1024L * 1024L * 1024L;

    /// <summary>Fraction of physical memory allowed for retained payloads.</summary>
    public const double PhysicalMemoryCeilingRatio = 0.80;

    /// <summary>Maximum retained payload capacity in GiB.</summary>
    public int MaximumRetainedPayloadGigabytes { get; set; } = 4;

    /// <summary>Absolute retention TTL in seconds from insertion.</summary>
    public int RetentionTtlSeconds { get; set; } = 60;

    /// <summary>Sweep cadence in seconds.</summary>
    public int SweepIntervalSeconds { get; set; } = 1;
}

/// <summary>Downstream TransitServer endpoint.</summary>
public sealed class BackFillerTransitServerOptions
{
    /// <summary>TransitServer hostname or IP.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>TransitServer NNTP port.</summary>
    public int Port { get; set; } = 119;

    /// <summary>Whether TransitServer connections use TLS.</summary>
    public bool UseSsl { get; set; }
}
