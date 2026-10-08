using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Bindable NVMe egress-cache limits under <c>StorageServer:Storage:EgressCache</c>.
/// </summary>
/// <remarks>
/// <para>
/// The cache lives at <c>{ControlDir}/egress</c>. It is a disposable acceleration copy.
/// <see cref="CapacityBytes"/> of <c>0</c> leaves the cache off and creates no files.
/// <see cref="ReserveBytes"/> is ignored while the cache is off.
/// </para>
/// <para>
/// These byte limits are independent of SATA <c>Storage:Capacity</c> reservations.
/// They share the ControlDir volume with the journal, and admission must leave the
/// journal safety floor free. There is no measured positive default.
/// </para>
/// </remarks>
public sealed class EgressCacheOptions
{
    /// <summary>Default capacity. <c>0</c> disables the cache.</summary>
    public const long DefaultCapacityBytes = 0;

    /// <summary>Default reserve. Ignored while <see cref="CapacityBytes"/> is <c>0</c>.</summary>
    public const long DefaultReserveBytes = 0;

    /// <summary>
    /// Gets or sets the maximum logical bytes the egress cache may occupy.
    /// </summary>
    /// <remarks>
    /// <c>0</c> disables the cache. Must not be negative. This is not a percent of the NVMe
    /// and it is not a SATA retention quota.
    /// </remarks>
    [Range(0, long.MaxValue)]
    public long CapacityBytes { get; set; } = DefaultCapacityBytes;

    /// <summary>
    /// Gets or sets the ControlDir free-space floor that cache occupancy must not consume.
    /// </summary>
    /// <remarks>
    /// Applied only when <see cref="CapacityBytes"/> is positive. <c>0</c> adds no extra floor.
    /// Journal hard limit, checkpoint thresholds, and the <c>MaximumUtilization</c> complement
    /// still apply. Must not be negative.
    /// </remarks>
    [Range(0, long.MaxValue)]
    public long ReserveBytes { get; set; } = DefaultReserveBytes;
}
