using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Process-local SATA capacity admission under <c>StorageServer:Storage:Capacity</c> (Phase 5E.1).
/// </summary>
/// <remarks>
/// <para>
/// Reservations are process-local only. They are not kernel, cross-process, or cross-host
/// filesystem reservations. External writers and OS free-space races remain possible.
/// </para>
/// <para>
/// When <see cref="Enabled"/> is <see langword="false"/> (default), Accept behavior is unchanged.
/// When enabled, Accept admits only when
/// <c>(UsedBytes + ReservedBytes + RequiredBytes) ≤ MaximumUtilization × TotalBytes</c>
/// using <see cref="Storage.IStorageCapacityReader"/> and
/// <c>SegmentRecordCodec.RecordLengthForArtSize</c>.
/// </para>
/// </remarks>
public sealed class ArticleCapacityOptions
{
    /// <summary>Default maximum utilisation of the cache volume (80%).</summary>
    public const double DefaultMaximumUtilization = 0.80;

    /// <summary>
    /// Gets or sets whether process-local capacity admission is enabled.
    /// </summary>
    /// <remarks>Default <see langword="false"/> so existing deployments are unchanged.</remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum allowed <c>UsedBytes / TotalBytes</c> including process-local
    /// reservations and the candidate article's physical record length.
    /// </summary>
    /// <remarks>Must be finite and in the open interval <c>(0, 1)</c>.</remarks>
    [Range(double.Epsilon, 1.0 - double.Epsilon)]
    public double MaximumUtilization { get; set; } = DefaultMaximumUtilization;
}
