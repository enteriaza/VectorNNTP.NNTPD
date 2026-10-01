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
/// <param name="SegmentDir">Resolved SATA segment root (same path as <c>Storage:CacheDir</c>).</param>
/// <param name="JournalSoftLimitBytes">Journal soft pressure threshold.</param>
/// <param name="JournalHardLimitBytes">Journal hard reject threshold.</param>
/// <param name="SegmentTargetSizeBytes">Target closed-segment size.</param>
/// <param name="CapacityAdmissionEnabled">Process-local capacity admission (Phase 5E.1 / 5E.2).</param>
/// <param name="CapacityMaximumUtilization">
/// Article Accept ceiling: max <c>(Used + ArtRes + CompRes + CheckpointRes + Required) / Total</c>.
/// </param>
/// <param name="CapacityCompactionHeadroom">
/// Utilisation delta added to <paramref name="CapacityMaximumUtilization"/> for compaction
/// destination appends only (Phase 5E.2).
/// </param>
public sealed record ArticleStorageRuntimeOptions(
    string ControlDir,
    string SegmentDir,
    long JournalSoftLimitBytes,
    long JournalHardLimitBytes,
    long SegmentTargetSizeBytes,
    bool CapacityAdmissionEnabled = false,
    double CapacityMaximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization,
    double CapacityCompactionHeadroom = ArticleCapacityOptions.DefaultCompactionHeadroom);

/// <summary>Validated listener bounds.</summary>
public sealed record StorageServerListenerRuntimeOptions(
    int ParserAccumulationMaxBytes,
    TimeSpan TlsHandshakeTimeout,
    TimeSpan IoProgressTimeout,
    int MaxActiveConnections);
