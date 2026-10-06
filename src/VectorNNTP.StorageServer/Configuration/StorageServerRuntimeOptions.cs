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
/// <param name="CapacityMaximumUtilization">
/// Hard article-admission ceiling as an integer percent of volume <c>TotalBytes</c>.
/// </param>
/// <param name="CapacityCompactionHeadroom">
/// Utilisation delta, in percentage points, added to <paramref name="CapacityMaximumUtilization"/>
/// for compaction destination appends only.
/// </param>
/// <param name="CapacityMaximumUsageCapacity">
/// Physical usage percent at which pressure recovery becomes active.
/// </param>
/// <param name="CapacityFreeCapacity">
/// Percentage points of physical usage pressure recovery must reclaim below
/// <paramref name="CapacityMaximumUsageCapacity"/>.
/// </param>
/// <param name="MaxSegmentSealDelay">
/// Maximum time an active segment may stay open after its first durable article.
/// <see cref="TimeSpan.Zero"/> disables age sealing. Size rollover is unchanged.
/// </param>
/// <param name="ActiveSegmentCount">
/// How many segment files may accept appends at once. <c>1</c>, <c>2</c>, or <c>4</c>.
/// <c>1</c> is a single active writer.
/// </param>
/// <param name="BulkPressure">
/// Cache-volume watermarks and reserves. Null uses the documented defaults.
/// </param>
public sealed record ArticleStorageRuntimeOptions(
    string ControlDir,
    string SegmentDir,
    long JournalSoftLimitBytes,
    long JournalHardLimitBytes,
    long SegmentTargetSizeBytes,
    int CapacityMaximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization,
    int CapacityCompactionHeadroom = ArticleCapacityOptions.DefaultCompactionHeadroom,
    int CapacityMaximumUsageCapacity = ArticleCapacityOptions.DefaultMaximumUsageCapacity,
    int CapacityFreeCapacity = ArticleCapacityOptions.DefaultFreeCapacity,
    TimeSpan MaxSegmentSealDelay = default,
    int ActiveSegmentCount = ArticleStorageOptions.DefaultActiveSegmentCount,
    BulkStoragePressureOptions? BulkPressure = null);

/// <summary>Validated listener bounds.</summary>
public sealed record StorageServerListenerRuntimeOptions(
    int ParserAccumulationMaxBytes,
    TimeSpan TlsHandshakeTimeout,
    TimeSpan IoProgressTimeout,
    int MaxActiveConnections);
