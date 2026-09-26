namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Bindable BackFiller configuration under section <see cref="SectionName"/>.
/// </summary>
/// <remarks>
/// <para>
/// Property names are PascalCase. Generated <see cref="Fqdn"/> cannot be bound.
/// Never log a complete instance: RabbitMQ, Let's Encrypt, and GrabberDB secrets live here.
/// </para>
/// <para>
/// There is one canonical environment contract: prefix <see cref="EnvironmentVariablePrefix"/>
/// plus the configuration path with <c>__</c> separators. RabbitMQ and Let's Encrypt are nested
/// under this section, so the prefixed paths include <c>BackFiller__</c>. Short-form names such
/// as <c>backfiller__RabbitMQ__Username</c> are not aliases and do not bind here. The old worker
/// used unprefixed <c>BackFiller__*</c> / <c>ConnectionStrings__GrabberDB</c> with no custom
/// prefix; that is not a second first-class contract.
/// </para>
/// <para>
/// Intentional key rename from the old worker: <c>BackFiller:Id</c> is now
/// <c>BackFiller:ServerId</c>. <c>DirLogs</c> is <see cref="LogDirectory"/>;
/// <c>DirCerts</c> is <see cref="CertificateDirectory"/>.
/// </para>
/// </remarks>
public sealed class BackFillerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "BackFiller";

    /// <summary>Environment-variable prefix, including the trailing separator.</summary>
    public const string EnvironmentVariablePrefix = "backfiller__";

    /// <summary>Canonical environment variable that supplies <see cref="Name"/>.</summary>
    public const string NameEnvironmentVariable = "backfiller__BackFiller__Name";

    /// <summary>Canonical environment variable that supplies <see cref="ServerId"/>.</summary>
    public const string ServerIdEnvironmentVariable = "backfiller__BackFiller__ServerId";

    /// <summary>Canonical environment variable that supplies RabbitMQ username.</summary>
    public const string RabbitMqUsernameEnvironmentVariable = "backfiller__BackFiller__RabbitMQ__Username";

    /// <summary>Canonical environment variable that supplies RabbitMQ password.</summary>
    public const string RabbitMqPasswordEnvironmentVariable = "backfiller__BackFiller__RabbitMQ__Password";

    /// <summary>Canonical environment variable that supplies GrabberDB.</summary>
    public const string GrabberDbEnvironmentVariable = "backfiller__ConnectionStrings__GrabberDB";

    /// <summary>Canonical environment variable that supplies the Cloudflare API token.</summary>
    public const string CloudFlareApiTokenEnvironmentVariable = "backfiller__BackFiller__LetsEncrypt__CloudFlareApiToken";

    /// <summary>Canonical environment variable that supplies the PKCS#12 export password.</summary>
    public const string PfxExportPasswordEnvironmentVariable = "backfiller__BackFiller__LetsEncrypt__PfxExportPassword";

    /// <summary>Default DNS suffix when the key is omitted.</summary>
    public const string DefaultDnsSuffix = "usenet.ninja";

    /// <summary>Default relative log directory.</summary>
    public const string DefaultLogDirectory = "logs";

    /// <summary>Default relative certificate directory.</summary>
    public const string DefaultCertificateDirectory = "certs";

    /// <summary>
    /// Gets or sets the instance name used as the FQDN host-label prefix.
    /// </summary>
    /// <remarks>Required. Must be a DNS label. Canonicalized to lowercase for FQDN construction.</remarks>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the BackFiller server identifier.
    /// </summary>
    /// <remarks>
    /// Required. Range 0–99 (old <c>BackFiller:Id</c> contract). Nullable so missing is distinct from 0.
    /// </remarks>
    public int? ServerId { get; set; }

    /// <summary>
    /// Gets or sets the DNS suffix used to generate <see cref="Fqdn"/>.
    /// </summary>
    public string DnsSuffix { get; set; } = DefaultDnsSuffix;

    /// <summary>
    /// Gets the generated FQDN. Not independently configurable.
    /// </summary>
    public string Fqdn =>
        !string.IsNullOrWhiteSpace(Name)
        && ServerId is { } serverId
        && serverId is >= BackFillerIdentity.MinimumServerId and <= BackFillerIdentity.MaximumServerId
        && !string.IsNullOrWhiteSpace(DnsSuffix)
            ? BackFillerIdentity.BuildFqdn(Name, serverId, DnsSuffix)
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
    /// Gets or sets the TCP port used by all listener bind addresses.
    /// </summary>
    /// <remarks>Required. Range 1–65535. No silent default.</remarks>
    public int? BindPort { get; set; }

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
    /// Gets or sets Let's Encrypt / ACME settings.
    /// </summary>
    public BackFillerLetsEncryptOptions LetsEncrypt { get; set; } = new();

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
    /// Gets or sets MySQL provider-account control-plane settings.
    /// </summary>
    public BackFillerAccountsOptions Accounts { get; set; } = new();

    /// <summary>
    /// Returns whether a bind-address token is a wildcard.
    /// </summary>
    /// <param name="entry">Configured token.</param>
    /// <returns><see langword="true"/> for all-interface wildcards.</returns>
    public static bool IsBindAddressWildcard(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var trimmed = entry.Trim();
        if (trimmed is "*" or "+")
        {
            return true;
        }

        return System.Net.IPAddress.TryParse(trimmed, out var address)
               && (address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any));
    }
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

/// <summary>MySQL provider-account control-plane bounds.</summary>
public sealed class BackFillerAccountsOptions
{
    /// <summary>Old ControlPlaneService refresh cadence.</summary>
    public const int DefaultRefreshIntervalSeconds = 60;

    /// <summary>Per-command MySQL timeout.</summary>
    public const int DefaultCommandTimeoutSeconds = 15;

    /// <summary>Seconds between successful-or-failed refresh attempts after the initial load.</summary>
    public int RefreshIntervalSeconds { get; set; } = DefaultRefreshIntervalSeconds;

    /// <summary>MySQL command timeout in seconds for the accounts query.</summary>
    public int CommandTimeoutSeconds { get; set; } = DefaultCommandTimeoutSeconds;
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
