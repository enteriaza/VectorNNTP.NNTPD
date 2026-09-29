using System.Net;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Immutable validated runtime snapshot consumed by application services.
/// </summary>
/// <remarks>
/// Produced once after successful validation. Services must not re-read
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> for these values.
/// Never log a complete instance: it may contain certificate-directory material paths
/// used alongside ACME secrets held separately on Common options.
/// </remarks>
public sealed record StorageServerRuntimeOptions(
    int ServerId,
    string DnsSuffix,
    string Fqdn,
    IReadOnlyList<string> BindAddressTokens,
    IReadOnlyList<IPAddress> CanonicalBindAddresses,
    int BindPort,
    int BindPortTls,
    string LogDir,
    string CacheDir,
    string ControlDir,
    ArticleStorageRuntimeOptions Storage,
    string CertificateDirectory,
    TimeSpan GracefulShutdownTimeout,
    TimeSpan? StartupTimeout,
    bool StopHostOnUnexpectedServiceTermination,
    StorageServerListenerRuntimeOptions Listener,
    IReadOnlyList<string> CertificateDomainNames);

/// <summary>Validated article-storage engine runtime bounds.</summary>
/// <param name="ControlDir">Resolved NVMe control-tier root.</param>
/// <param name="SegmentDir">Resolved SATA segment root (same as <c>CacheDir</c>).</param>
/// <param name="JournalSoftLimitBytes">Journal soft pressure threshold.</param>
/// <param name="JournalHardLimitBytes">Journal hard reject threshold.</param>
/// <param name="SegmentTargetSizeBytes">Target closed-segment size.</param>
public sealed record ArticleStorageRuntimeOptions(
    string ControlDir,
    string SegmentDir,
    long JournalSoftLimitBytes,
    long JournalHardLimitBytes,
    long SegmentTargetSizeBytes);

/// <summary>Validated listener bounds.</summary>
public sealed record StorageServerListenerRuntimeOptions(
    int ParserAccumulationMaxBytes,
    TimeSpan TlsHandshakeTimeout,
    TimeSpan IoProgressTimeout,
    int MaxActiveConnections);
