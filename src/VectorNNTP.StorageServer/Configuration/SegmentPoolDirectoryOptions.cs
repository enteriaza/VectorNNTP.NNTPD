using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// One independently managed segment spool under <c>StorageServer:Storage:SegmentPools:Directories</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CacheDir"/> is the directory that holds that spool's segment files. It is bulk
/// retention, not a disposable read cache. <see cref="MaxCapacity"/> is the spool's hard byte
/// ceiling. The filesystem that contains the directory is a second, independent limit.
/// </para>
/// <para>
/// Several directories may share one volume. Capacity accounting groups them by filesystem
/// identity so they share one physical free-space budget.
/// </para>
/// </remarks>
public sealed class SegmentPoolDirectoryOptions
{
    /// <summary>Default relative directory for the first segment spool.</summary>
    public const string DefaultCacheDir = "spool/cache";

    /// <summary>Default hard capacity for one spool, 50 GiB.</summary>
    public const long DefaultMaxCapacityBytes = 50L * 1024 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the segment-file directory for this spool.
    /// </summary>
    /// <remarks>
    /// Relative paths resolve through
    /// <see cref="VectorNNTP.Common.Configuration.ApplicationLocalPath.ResolveApplicationLocalPath"/>
    /// against <see cref="AppContext.BaseDirectory"/>. Absolute paths stay absolute.
    /// The directory need not exist at validation time. Validation does not create it.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string CacheDir { get; set; } = DefaultCacheDir;

    /// <summary>
    /// Gets or sets the hard capacity of this spool, in bytes.
    /// </summary>
    /// <remarks>
    /// Physically allocated segment bytes plus outstanding reservations for this spool must not
    /// exceed this value. It is not a filesystem measurement and not a retention age.
    /// </remarks>
    [Range(1, long.MaxValue)]
    public long MaxCapacity { get; set; } = DefaultMaxCapacityBytes;
}
