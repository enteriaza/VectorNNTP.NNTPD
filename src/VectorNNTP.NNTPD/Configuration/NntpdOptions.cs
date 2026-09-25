using System.ComponentModel.DataAnnotations;
using System.Net;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Strongly typed lifecycle, hosting, and NNTPD listener options for VectorNNTP.NNTPD.
/// </summary>
/// <remarks>
/// <para>
/// Listener and Cloudflare settings use PascalCase configuration names that match the
/// property names (<c>BindAddress</c>, <c>CloudFlareZoneId</c>, …). The configuration
/// section name is <see cref="SectionName"/> (<c>Nntpd</c>; case-insensitive).
/// </para>
/// <para>
/// <see cref="Fqdn"/> is generated from <see cref="ServerId"/> and <see cref="DnsSuffix"/> and cannot
/// be bound or overridden from configuration or environment variables.
/// </para>
/// <para>
/// Never log complete <see cref="NntpdOptions"/> instances:
/// <see cref="CloudFlareApiKey"/>, <see cref="AcmeCertificatePassword"/>,
/// <see cref="XTraceKey"/> / <see cref="XTracePreviousKey"/>, and
/// <see cref="NewsmasterPassword"/> are secrets.
/// </para>
/// </remarks>
public sealed class NntpdOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Nntpd";

    /// <summary>Configuration key for the Cloudflare API key secret.</summary>
    public const string CloudFlareApiKeyConfigurationKey = "CloudFlareApiKey";

    /// <summary>Configuration key for the ACME PKCS#12 password secret.</summary>
    public const string AcmeCertificatePasswordConfigurationKey = "AcmeCertificatePassword";

    /// <summary>Configuration key for the Cloudflare zone id.</summary>
    public const string CloudFlareZoneIdConfigurationKey = "CloudFlareZoneId";

    /// <summary>
    /// Environment variable that supplies <see cref="CloudFlareApiKey"/>
    /// (<c>nntpd__cloudflareapikey</c>).
    /// </summary>
    public const string CloudFlareApiKeyEnvironmentVariable = "nntpd__cloudflareapikey";

    /// <summary>
    /// Environment variable that supplies <see cref="AcmeCertificatePassword"/>
    /// (<c>nntpd__AcmeCertificatePassword</c>).
    /// </summary>
    public const string AcmeCertificatePasswordEnvironmentVariable = "nntpd__AcmeCertificatePassword";

    /// <summary>
    /// Environment variable that supplies <see cref="CloudFlareZoneId"/>
    /// (<c>nntpd__CloudFlareZoneId</c>).
    /// </summary>
    public const string CloudFlareZoneIdEnvironmentVariable = "nntpd__CloudFlareZoneId";

    /// <summary>
    /// Environment variable that supplies <see cref="ServerId"/>
    /// (<c>nntpd__ServerId</c>).
    /// </summary>
    public const string ServerIdEnvironmentVariable = "nntpd__ServerId";

    /// <summary>Configuration key for the POST <c>X-Trace</c> AES-256 key secret.</summary>
    public const string XTraceKeyConfigurationKey = "XTraceKey";

    /// <summary>Configuration key for the optional previous POST <c>X-Trace</c> AES-256 key.</summary>
    public const string XTracePreviousKeyConfigurationKey = "XTracePreviousKey";

    /// <summary>
    /// Environment variable that supplies <see cref="XTraceKey"/>
    /// (<c>nntpd__XTraceKey</c>).
    /// </summary>
    public const string XTraceKeyEnvironmentVariable = "nntpd__XTraceKey";

    /// <summary>
    /// Environment variable that supplies <see cref="XTracePreviousKey"/>
    /// (<c>nntpd__XTracePreviousKey</c>).
    /// </summary>
    public const string XTracePreviousKeyEnvironmentVariable = "nntpd__XTracePreviousKey";

    /// <summary>Configuration key for the optional newsmaster AUTHINFO username.</summary>
    public const string NewsmasterUserConfigurationKey = "NewsmasterUser";

    /// <summary>Configuration key for the optional newsmaster AUTHINFO password secret.</summary>
    public const string NewsmasterPasswordConfigurationKey = "NewsmasterPassword";

    /// <summary>
    /// Environment variable that supplies <see cref="NewsmasterUser"/>
    /// (<c>nntpd__NewsmasterUser</c>).
    /// </summary>
    public const string NewsmasterUserEnvironmentVariable = "nntpd__NewsmasterUser";

    /// <summary>
    /// Environment variable that supplies <see cref="NewsmasterPassword"/>
    /// (<c>nntpd__NewsmasterPassword</c>).
    /// </summary>
    public const string NewsmasterPasswordEnvironmentVariable = "nntpd__NewsmasterPassword";

    /// <summary>Gets or sets the application display name used in logs and service registration metadata.</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(128)]
    public string ApplicationName { get; set; } = "VectorNNTP.NNTPD";

    /// <summary>
    /// Gets or sets the maximum time allowed for the overall graceful shutdown of application services.
    /// </summary>
    /// <remarks>
    /// This is a single wall-clock budget for the entire reverse-order stop sequence, not a fresh
    /// timeout granted independently to each service. <see cref="Core.ApplicationServiceManager"/>
    /// awaits each service stop (no abandon). Services that ignore cancellation can block that await
    /// until they return; the host <c>ShutdownTimeout</c> (configured from this value) and external
    /// supervisors remain the process-level backstop.
    /// </remarks>
    public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum time allowed for application startup before cancellation is considered a failure.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/>, startup is bounded only by the host cancellation token.
    /// </remarks>
    public TimeSpan? StartupTimeout { get; set; }

    /// <summary>
    /// Gets or sets the maximum wall-clock duration for a single Cloudflare reconcile or clean-up operation.
    /// </summary>
    /// <remarks>
    /// Default is two minutes. Nested HTTP 429 retries and reconciler attempt backoffs share this budget
    /// together with the caller's cancellation token; the earlier deadline wins. Failed-start clean-up uses
    /// a shorter dedicated budget (15 seconds).
    /// </remarks>
    public TimeSpan CloudFlareOperationTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets a value indicating whether the process should exit if an application service
    /// terminates unexpectedly while running.
    /// </summary>
    public bool StopHostOnUnexpectedServiceTermination { get; set; } = true;

    /// <summary>
    /// Gets or sets systemd-specific notification and watchdog options.
    /// </summary>
    [Required]
    public SystemdOptions Systemd { get; set; } = new();

    /// <summary>
    /// Gets or sets trusted HAProxy PROXY-protocol peer addresses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optional. Each entry must be a literal IPv4 or IPv6 address (not a DNS name, not a CIDR).
    /// When empty or omitted, PROXY protocol processing is disabled and every connection's
    /// effective client endpoint is the TCP peer.
    /// </para>
    /// <para>
    /// When non-empty, a TCP peer whose address matches an entry is treated as a trusted proxy
    /// and must present a valid PROXY v1/v2 header before TLS/NNTP. Untrusted peers are not
    /// permitted to supply PROXY metadata that replaces their TCP identity.
    /// </para>
    /// </remarks>
    public string[] ProxyHosts { get; set; } = [];

    /// <summary>
    /// Gets or sets the local listen addresses for NNTPD.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prefer a JSON array. Each entry is either:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>*</c> — wildcard for all local interfaces.</description></item>
    /// <item><description><c>0.0.0.0</c> / <c>::</c> — IPv4 / IPv6 any-address wildcards.</description></item>
    /// <item><description>An explicit IPv4 or IPv6 address that must be assigned to a local NIC.</description></item>
    /// </list>
    /// <para>
    /// When omitted, post-configure normalization sets <c>["*"]</c>. Assigned public and private
    /// addresses are accepted. Configuration validation does not bind sockets.
    /// </para>
    /// </remarks>
    public string[] BindAddress { get; set; } = [];

    /// <summary>
    /// Gets or sets the cleartext NNTP TCP port.
    /// </summary>
    /// <remarks>Default is <c>119</c>. Valid range is 1–65535.</remarks>
    [Range(1, 65535)]
    public int BindPort { get; set; } = 119;

    /// <summary>
    /// Gets or sets the TLS NNTP TCP port.
    /// </summary>
    /// <remarks>
    /// Default and unset behaviour is <c>0</c>, which disables TLS listeners.
    /// Values <c>1–65535</c> enable TLS listener configuration.
    /// See <see cref="IsTlsListenerEnabled"/>.
    /// </remarks>
    [Range(0, 65535)]
    public int BindPortTls { get; set; }

    /// <summary>
    /// Gets or sets whether <c>AUTHINFO USER/PASS</c> is permitted when the connection is not TLS-protected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default is <see langword="true"/> so cleartext NNTP can authenticate. <c>AUTHINFO PASS</c>
    /// transmits the password in clear text at the NNTP protocol layer; prefer TLS in production.
    /// </para>
    /// <para>
    /// When <see langword="false"/> and the connection is not TLS-protected, AUTHINFO USER/PASS
    /// return <c>483</c> and <c>AUTHINFO USER</c> is not advertised in CAPABILITIES.
    /// TLS connections always permit AUTHINFO USER/PASS regardless of this setting.
    /// </para>
    /// </remarks>
    public bool AllowCleartextAuth { get; set; } = true;

    /// <summary>
    /// Gets a value indicating whether TLS listener configuration is enabled.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> when <see cref="BindPortTls"/> is <c>0</c> (disabled / unset default).
    /// <see langword="true"/> when <see cref="BindPortTls"/> is in <c>1–65535</c>.
    /// When enabled, ACME certificate acquisition is required (see <see cref="AcmeEmail"/>).
    /// </remarks>
    public bool IsTlsListenerEnabled => BindPortTls > 0;

    /// <summary>
    /// Default Let's Encrypt <strong>staging</strong> ACME directory URL.
    /// </summary>
    public const string DefaultAcmeDirectoryUrl =
        "https://acme-staging-v02.api.letsencrypt.org/directory";

    /// <summary>Default relative ACME state directory.</summary>
    public const string DefaultAcmeStateDir = "certs/";

    /// <summary>Default relative Serilog file-log directory.</summary>
    public const string DefaultLogDir = "logs/";

    /// <summary>Default certificate renewal lead time in days.</summary>
    public const int DefaultAcmeRenewalThresholdDays = 30;

    /// <summary>
    /// Gets or sets the ACME directory URL (Let's Encrypt staging by default).
    /// </summary>
    /// <remarks>
    /// The configured value is authoritative. Production Let's Encrypt requires an explicit
    /// override (for example <c>https://acme-v02.api.letsencrypt.org/directory</c>).
    /// Used only when <see cref="IsTlsListenerEnabled"/> is <see langword="true"/>.
    /// </remarks>
    public string AcmeDirectoryUrl { get; set; } = DefaultAcmeDirectoryUrl;

    /// <summary>
    /// Gets or sets the ACME account contact email.
    /// </summary>
    /// <remarks>
    /// No default. Required when <see cref="IsTlsListenerEnabled"/> is <see langword="true"/>.
    /// Ignored when TLS is disabled — missing email must not prevent non-TLS startup.
    /// </remarks>
    public string AcmeEmail { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the filesystem directory for ACME account and certificate state.
    /// </summary>
    /// <remarks>
    /// Default is <c>certs/</c>. The TLS server credential is a PKCS#12/PFX file; the ACME account
    /// private key is stored separately as PKCS#8 DER. The Windows Certificate Store is not used.
    /// </remarks>
    public string AcmeStateDir { get; set; } = DefaultAcmeStateDir;

    /// <summary>
    /// Gets or sets the filesystem directory for Serilog daily rolling application logs.
    /// </summary>
    /// <remarks>
    /// Default is <c>logs/</c>. Relative paths resolve with
    /// <see cref="System.IO.Path.GetFullPath(string)"/> of the trimmed value, matching
    /// <see cref="AcmeStateDir"/>.
    /// </remarks>
    public string LogDir { get; set; } = DefaultLogDir;

    /// <summary>
    /// Gets or sets how many days before <c>NotAfter</c> a certificate is considered due for renewal.
    /// </summary>
    /// <remarks>Default is <c>30</c>. Valid range is <c>1–90</c>.</remarks>
    [Range(1, 90)]
    public int AcmeRenewalThresholdDays { get; set; } = DefaultAcmeRenewalThresholdDays;

    /// <summary>
    /// Gets or sets the password used to protect and load the TLS server PKCS#12/PFX file.
    /// </summary>
    /// <remarks>
    /// No default. Required when <see cref="IsTlsListenerEnabled"/> is <see langword="true"/>.
    /// Supply via <see cref="AcmeCertificatePasswordEnvironmentVariable"/> or user/deployment secrets —
    /// never commit this value. Do not log it or include it in exception messages.
    /// Ignored when TLS is disabled.
    /// </remarks>
    public string AcmeCertificatePassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Cloudflare API key used for DNS integration.
    /// </summary>
    /// <remarks>
    /// Required. Supply via <see cref="CloudFlareApiKeyEnvironmentVariable"/> — never commit this value.
    /// Do not store in <c>appsettings.json</c>. Missing or blank values fail startup validation.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string CloudFlareApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Cloudflare DNS zone identifier.
    /// </summary>
    /// <remarks>
    /// Required. May also be supplied via <see cref="CloudFlareZoneIdEnvironmentVariable"/>.
    /// Missing or blank values fail startup validation.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string CloudFlareZoneId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the DNS suffix used when generating <see cref="Fqdn"/>.
    /// </summary>
    /// <remarks>
    /// Default is <c>usenet.ninja</c>. Must be a syntactically valid DNS name.
    /// This value is expected to correspond to the Cloudflare zone identified by
    /// <see cref="CloudFlareZoneId"/>; that correspondence is not verified by live API calls.
    /// </remarks>
    public string DnsSuffix { get; set; } = "usenet.ninja";

    /// <summary>
    /// Gets or sets the numeric server identity used when generating <see cref="Fqdn"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required. No default. Must be explicitly configured as an integer in <c>1–99</c>.
    /// </para>
    /// <para>
    /// Typed as <see cref="Nullable{T}"/> so a missing configuration value remains distinguishable
    /// from an explicitly configured <c>0</c> (both fail validation).
    /// </para>
    /// </remarks>
    [Required]
    [Range(1, 99)]
    public int? ServerId { get; set; }

    /// <summary>
    /// Gets the generated server FQDN <c>nntpd{ServerId:00}.{DnsSuffix}</c>.
    /// </summary>
    /// <remarks>
    /// Not independently configurable. There is no dot between <c>nntpd</c> and the two-digit id.
    /// Example: <c>ServerId</c> 1 → <c>nntpd01.usenet.ninja</c>.
    /// Returns an empty string when <see cref="ServerId"/> is unset so data-annotation validation
    /// can inspect the object; dependent services must use this only after validation succeeds.
    /// </remarks>
    public string Fqdn =>
        ServerId is { } serverId && !string.IsNullOrWhiteSpace(DnsSuffix)
            ? FormatFqdn(serverId, DnsSuffix)
            : string.Empty;

    /// <summary>
    /// Formats the generated FQDN for a validated server id and DNS suffix.
    /// </summary>
    /// <param name="serverId">Server id in 1–99.</param>
    /// <param name="dnsSuffix">DNS suffix without a trailing dot.</param>
    /// <returns>The FQDN string.</returns>
    public static string FormatFqdn(int serverId, string dnsSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dnsSuffix);
        return $"nntpd{serverId:00}.{dnsSuffix.Trim().TrimEnd('.')}";
    }

    /// <summary>
    /// Gets or sets NNTPD-local transit runtime options (STREAM TX depth).
    /// </summary>
    /// <remarks>
    /// Named Transit peers bind from the top-level <c>Transit</c> section
    /// (<see cref="TransitPeersOptions"/>), not from this property.
    /// </remarks>
    [Required]
    public TransitOptions Transit { get; set; } = new();

    /// <summary>
    /// Gets or sets SPEEDTEST diagnostic limits.
    /// </summary>
    /// <remarks>
    /// Optional. Defaults cap duration, bytes, and concurrency for the VectorNNTP
    /// <c>SPEEDTEST</c> extension. Not part of the article data plane.
    /// </remarks>
    [Required]
    public SpeedTestOptions SpeedTest { get; set; } = new();

    /// <summary>
    /// Gets or sets temporary real-feed observability.
    /// </summary>
    /// <remarks>
    /// Off by default. Periodic snapshots only; not per-article logging.
    /// Also enabled by environment variable <c>VECTORNNTP_FEED_DIAGNOSTICS</c>.
    /// </remarks>
    [Required]
    public FeedDiagnosticsOptions FeedDiagnostics { get; set; } = new();

    /// <summary>
    /// Gets or sets how long a HistoryDB marker is retained in local memory and Redis.
    /// </summary>
    /// <remarks>
    /// Default is two hours. Redis uses native key TTL. The in-memory store expires the same
    /// digest after this period. HistoryDB policy lives here, not on <see cref="RedisOptions"/>.
    /// </remarks>
    public TimeSpan HistoryTime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Default NNTP command idle timeout in seconds.</summary>
    public const int DefaultIdleTime = 300;

    /// <summary>Minimum accepted <see cref="IdleTime"/> in seconds.</summary>
    public const int MinIdleTime = 1;

    /// <summary>Maximum accepted <see cref="IdleTime"/> in seconds (24 hours).</summary>
    public const int MaxIdleTime = 86_400;

    /// <summary>
    /// Gets or sets how many seconds an established NNTP session may remain without
    /// executing an NNTP command before the session is disconnected.
    /// </summary>
    /// <remarks>
    /// Unit is seconds. Default is <see cref="DefaultIdleTime"/>. Valid range is
    /// <see cref="MinIdleTime"/>–<see cref="MaxIdleTime"/>. <c>0</c> is invalid
    /// (not “disabled”); it follows the same minimum-1-second rule as other
    /// required timeouts such as <see cref="HistoryTime"/>.
    /// The timer starts after session setup (greeting) and resets when a command
    /// is accepted for processing. In-flight commands (including pipelined CHECK
    /// and TAKETHIS) keep the session non-idle.
    /// </remarks>
    public int IdleTime { get; set; } = DefaultIdleTime;

    /// <summary>Default POST article size limit: 5 MiB.</summary>
    public const int DefaultMaxArticleSize = 5 * 1024 * 1024;

    /// <summary>Minimum accepted <see cref="MaxArticleSize"/> in bytes.</summary>
    public const int MinMaxArticleSize = 1;

    /// <summary>Maximum accepted <see cref="MaxArticleSize"/> in bytes (100 MiB).</summary>
    public const int MaxMaxArticleSize = 100 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum destuffed POST article size in bytes.
    /// </summary>
    /// <remarks>
    /// Default is <see cref="DefaultMaxArticleSize"/> (5,242,880). Valid range is
    /// <see cref="MinMaxArticleSize"/>–<see cref="MaxMaxArticleSize"/>. The limit is
    /// enforced while the multiline article is streamed (destuffed headers + blank
    /// separator + body; NNTP terminator excluded; stuffing dots are not counted).
    /// Distinct from <c>ArticleIngestion:MaxArticleBytes</c>, which bounds
    /// IHAVE/TAKETHIS ingest and IHAVE worker destuff only.
    /// </remarks>
    public int MaxArticleSize { get; set; } = DefaultMaxArticleSize;

    /// <summary>Default POST <c>Injection-Info</c> complaint mailbox.</summary>
    public const string DefaultMailComplaintsTo = "abuse@usenet.ninja";

    /// <summary>
    /// Gets or sets the mailbox emitted as <c>mail-complaints-to</c> on server-owned
    /// POST <c>Injection-Info</c>.
    /// </summary>
    /// <remarks>
    /// Default is <see cref="DefaultMailComplaintsTo"/>. Required; must be a plausible
    /// mailbox. Client-supplied <c>Injection-Info</c> is never used.
    /// </remarks>
    public string MailComplaintsTo { get; set; } = DefaultMailComplaintsTo;

    /// <summary>
    /// Gets or sets the persisted AES-256 key used to protect POST <c>X-Trace</c> values.
    /// </summary>
    /// <remarks>
    /// Required secret. Supply 32 bytes as 64 hex characters or Base64 via
    /// <see cref="XTraceKeyEnvironmentVariable"/> or deployment secrets — never commit
    /// this value. The process does not generate a key at startup; restarting with the
    /// same key keeps previously issued tokens decryptable.
    /// </remarks>
    public string XTraceKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the previous AES-256 key retained for one-generation <c>X-Trace</c> rotation.
    /// </summary>
    /// <remarks>
    /// Optional. When set, trusted decrypt accepts tokens produced with this key.
    /// New tokens always use <see cref="XTraceKey"/>. After this value is removed,
    /// tokens produced only with the retired key cannot be decrypted.
    /// </remarks>
    public string XTracePreviousKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the AUTHINFO username that may POST a well-formed cancel control article.
    /// </summary>
    /// <remarks>
    /// Optional. When omitted with <see cref="NewsmasterPassword"/>, AUTHINFO remains deny-all
    /// (unless another provider is registered). When set, <see cref="NewsmasterPassword"/> is
    /// required. Ordinary POST clients never receive this identity.
    /// </remarks>
    public string NewsmasterUser { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the AUTHINFO password for <see cref="NewsmasterUser"/>.
    /// </summary>
    /// <remarks>
    /// Optional secret. Required when <see cref="NewsmasterUser"/> is set. Supply via
    /// <see cref="NewsmasterPasswordEnvironmentVariable"/> or deployment secrets — never commit
    /// this value. Do not log it or include it in exception messages.
    /// </remarks>
    public string NewsmasterPassword { get; set; } = string.Empty;

    /// <summary>
    /// Default Transit article-queue memory budget: 1 GiB (1,073,741,824 bytes).
    /// </summary>
    public const long DefaultTransitQueueMemoryLimit = 1_073_741_824L;

    /// <summary>
    /// Gets or sets the Transit article-queue memory budget in bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounds the sum of owned queued article payload lengths
    /// (<c>InboundArticle.Payload.Length</c>): complete NNTP article bytes as
    /// queued (IHAVE: stuffed wire, terminating <c>CRLF . CRLF</c> excluded).
    /// It does not include object overhead and is not a process-wide memory cap.
    /// Memory is released when queued articles are consumed.
    /// </para>
    /// <para>
    /// Default is 1 GiB. Valid range is <c>1</c> through <see cref="long.MaxValue"/>
    /// (signed 64-bit accounting). An individual article larger than this budget
    /// is rejected rather than waited for, so admission cannot deadlock.
    /// </para>
    /// </remarks>
    public long TransitQueueMemoryLimit { get; set; } = DefaultTransitQueueMemoryLimit;

    /// <summary>
    /// Gets or sets article ingestion / incoming spool options.
    /// </summary>
    /// <remarks>
    /// Defaults: max article 4 MiB, directory <c>spool/incoming</c>.
    /// Queue admission is bounded by <see cref="TransitQueueMemoryLimit"/>, not
    /// an article-count cap. Used by <c>TAKETHIS</c> and <c>IHAVE</c>.
    /// </remarks>
    [Required]
    public ArticleIngestionOptions ArticleIngestion { get; set; } = new();

    /// <summary>
    /// Returns whether a bind-address entry is a wildcard (all interfaces / any-address).
    /// </summary>
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

        if (!IPAddress.TryParse(trimmed, out var address))
        {
            return false;
        }

        return address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
    }
}
