using System.ComponentModel.DataAnnotations;
using VectorNNTP.Common.Core;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Bindable StorageServer configuration under section <see cref="SectionName"/>.
/// </summary>
/// <remarks>
/// <para>
/// Property names are PascalCase. Generated <see cref="Fqdn"/> cannot be bound.
/// Never log a complete instance: Cloudflare and ACME PKCS#12 secrets overlay from
/// root <c>VECTOR__*</c> keys via <see cref="StorageServerAcmeCloudflareOptionsAdapter"/>.
/// </para>
/// <para>
/// StorageServer is TLS-only. <see cref="BindPort"/> exists for shared option shape
/// (default <c>0</c> = unused) and is never listened on. The listener port is
/// <see cref="BindPortTls"/> only. The FQDN is
/// <c>cache{ServerId:00}.{DnsSuffix}</c>.
/// </para>
/// </remarks>
public sealed class StorageServerOptions : IApplicationLifecycleOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "StorageServer";

    /// <summary>Canonical VectorNNTP environment-variable prefix.</summary>
    public const string EnvironmentVariablePrefix = VectorEnvironment.Prefix;

    /// <summary>Fixed FQDN host-label prefix. Not configuration.</summary>
    public const string ApplicationPrefix = "cache";

    /// <summary>Default DNS suffix when the key is omitted.</summary>
    public const string DefaultDnsSuffix = "usenet.ninja";

    /// <summary>Default application log directory.</summary>
    public const string DefaultLogDir = "/logs";

    /// <summary>Default relative certificate directory.</summary>
    public const string DefaultCertificateDirectory = "certs";

    /// <summary>Default Let's Encrypt staging ACME directory URL.</summary>
    public const string DefaultAcmeDirectoryUrl = AcmeCloudflareOptions.DefaultAcmeDirectoryUrl;

    /// <summary>Default relative ACME state directory.</summary>
    public const string DefaultAcmeStateDir = AcmeCloudflareOptions.DefaultAcmeStateDir;

    /// <summary>Default certificate renewal lead time in days.</summary>
    public const int DefaultAcmeRenewalThresholdDays = AcmeCloudflareOptions.DefaultAcmeRenewalThresholdDays;

    /// <summary>
    /// Gets or sets the StorageServer server identifier.
    /// </summary>
    /// <remarks>
    /// Required. No default. Must be explicitly configured as an integer in
    /// <c>1–255</c> via <c>StorageServer:ServerId</c>. Typed as <see cref="Nullable{T}"/>
    /// so a missing value remains distinguishable from an explicit <c>0</c>.
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
    public string CloudFlareZoneId { get; set; } = string.Empty;

    /// <summary>
    /// Gets the generated FQDN <c>cache{ServerId:00}.{DnsSuffix}</c>.
    /// </summary>
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
    /// Gets or sets the cleartext TCP port.
    /// </summary>
    /// <remarks>
    /// Default <c>0</c> (unused). StorageServer never binds a cleartext listener.
    /// Range when set: 0–65535. Kept for shared configuration shape only.
    /// </remarks>
    [Range(0, 65535)]
    public int BindPort { get; set; }

    /// <summary>
    /// Gets or sets the TLS TCP port used by the VATP listener.
    /// </summary>
    /// <remarks>
    /// Required. Range 1–65535. StorageServer is TLS-only; there is no cleartext fallback.
    /// Nullable so missing is distinct from 0.
    /// </remarks>
    public int? BindPortTls { get; set; }

    /// <summary>
    /// Gets or sets the ACME directory URL.
    /// </summary>
    public string AcmeDirectoryUrl { get; set; } = DefaultAcmeDirectoryUrl;

    /// <summary>
    /// Gets or sets how many days before expiry a certificate is due for renewal.
    /// </summary>
    public int AcmeRenewalThresholdDays { get; set; } = DefaultAcmeRenewalThresholdDays;

    /// <summary>
    /// Gets or sets the ACME account and certificate state directory.
    /// </summary>
    public string AcmeStateDir { get; set; } = DefaultAcmeStateDir;

    /// <summary>
    /// Gets or sets the directory used for application log files.
    /// </summary>
    public string LogDir { get; set; } = DefaultLogDir;

    /// <summary>
    /// Gets or sets article-storage engine options (NVMe control tier, SATA cache tier, and journal bounds).
    /// </summary>
    [Required]
    public ArticleStorageOptions Storage { get; set; } = new();

    /// <summary>
    /// Gets or sets the directory used for ACME and TLS certificate artifacts.
    /// </summary>
    public string CertificateDirectory { get; set; } = DefaultCertificateDirectory;

    /// <summary>
    /// Gets or sets the application display name used by lifecycle messages.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="ApplicationJsonConfiguration.EntryAssemblyName"/>.
    /// Log file names, the Serilog application property, the Windows service name,
    /// and the RabbitMQ connection prefix use the entry assembly name directly.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(128)]
    public string ApplicationName { get; set; } = ApplicationJsonConfiguration.EntryAssemblyName;

    /// <inheritdoc />
    public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public TimeSpan? StartupTimeout { get; set; }

    /// <inheritdoc />
    public bool StopHostOnUnexpectedServiceTermination { get; set; } = true;

    /// <summary>
    /// Gets or sets Listener resource-safety bounds.
    /// </summary>
    public StorageServerListenerOptions Listener { get; set; } = new();

    /// <summary>
    /// Gets or sets Linux systemd notify/watchdog options.
    /// </summary>
    [Required]
    public StorageServerSystemdOptions Systemd { get; set; } = new();

    /// <summary>
    /// Returns whether a bind-address token is a wildcard.
    /// </summary>
    /// <param name="entry">Configured token.</param>
    /// <returns><see langword="true"/> for all-interface wildcards.</returns>
    public static bool IsBindAddressWildcard(string entry) =>
        AcmeCloudflareOptions.IsBindAddressWildcard(entry);
}

/// <summary>Listener resource-safety bounds.</summary>
public sealed class StorageServerListenerOptions
{
    /// <summary>Maximum incomplete inbound protocol bytes per connection.</summary>
    public int ParserAccumulationMaxBytes { get; set; } = 262144;

    /// <summary>TLS handshake timeout in seconds.</summary>
    public int TlsHandshakeTimeoutSeconds { get; set; } = 30;

    /// <summary>Per-operation I/O no-progress timeout in seconds.</summary>
    public int IoProgressTimeoutSeconds { get; set; } = 60;

    /// <summary>Maximum concurrently active accepted connections.</summary>
    public int MaxActiveConnections { get; set; } = 1024;
}

/// <summary>
/// Optional systemd-specific hosting behaviour for Linux deployments.
/// </summary>
public sealed class StorageServerSystemdOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether watchdog keep-alives may be sent when systemd
    /// has configured <c>WatchdogSec=</c> for this process.
    /// </summary>
    public bool EnableWatchdog { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether lifecycle <c>STATUS=</c> notifications are sent
    /// when systemd notify is enabled.
    /// </summary>
    public bool ReportLifecycleStatus { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the application sends an explicit <c>READY=1</c>
    /// when the application lifecycle reaches <see cref="ApplicationState.Running"/>.
    /// </summary>
    public bool NotifyReadyOnApplicationRunning { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the application sends an explicit <c>STOPPING=1</c>
    /// when graceful shutdown begins at the application lifecycle boundary.
    /// </summary>
    public bool NotifyStoppingOnApplicationShutdown { get; set; } = true;

    /// <summary>
    /// Gets or sets the fraction of the systemd watchdog deadline used as the heartbeat interval.
    /// </summary>
    [Range(0.05, 0.9)]
    public double WatchdogIntervalFraction { get; set; } = 0.5;
}
