using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Process-local SATA capacity admission under <c>StorageServer:Storage:Capacity</c>
/// (Phase 5E.1 / 5E.2).
/// </summary>
/// <remarks>
/// <para>
/// Reservations are process-local only. They are not kernel, cross-process, or cross-host
/// filesystem reservations. External writers and OS free-space races remain possible.
/// </para>
/// <para>
/// When <see cref="Enabled"/> is <see langword="false"/> (default), Accept and compaction
/// destination admission are unrestricted.
/// When enabled:
/// </para>
/// <list type="bullet">
/// <item>
/// Article Accept:
/// <c>(Used + ArticleReserved + CompactionReserved + Required) ≤ MaximumUtilization × Total</c>
/// </item>
/// <item>
/// Compaction relocation destination append:
/// <c>(Used + ArticleReserved + CompactionReserved + Required) ≤ (MaximumUtilization + CompactionHeadroom) × Total</c>
/// </item>
/// </list>
/// <para>
/// <see cref="CompactionHeadroom"/> is a utilization <em>delta</em> (temporary compaction admission
/// headroom), not a permanently reserved fraction of the volume.
/// </para>
/// </remarks>
public sealed class ArticleCapacityOptions
{
    /// <summary>Default maximum utilisation of the cache volume for article admission (80%).</summary>
    public const double DefaultMaximumUtilization = 0.80;

    /// <summary>
    /// Default additional utilisation delta available only to compaction destination appends (10%).
    /// </summary>
    public const double DefaultCompactionHeadroom = 0.10;

    /// <summary>
    /// Gets or sets whether process-local capacity admission is enabled.
    /// </summary>
    /// <remarks>Default <see langword="false"/> so existing deployments are unchanged.</remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum allowed utilisation for article Accept, including process-local
    /// article and compaction reservations and the candidate article's physical record length.
    /// </summary>
    /// <remarks>Must be finite and in the open interval <c>(0, 1)</c>.</remarks>
    [Range(double.Epsilon, 1.0 - double.Epsilon)]
    public double MaximumUtilization { get; set; } = DefaultMaximumUtilization;

    /// <summary>
    /// Gets or sets the utilisation delta added to <see cref="MaximumUtilization"/> for
    /// compaction destination append admission only.
    /// </summary>
    /// <remarks>
    /// Must be finite and <c>&gt; 0</c>, and
    /// <c>MaximumUtilization + CompactionHeadroom &lt; 1</c>.
    /// Default <see cref="DefaultCompactionHeadroom"/>.
    /// </remarks>
    public double CompactionHeadroom { get; set; } = DefaultCompactionHeadroom;
}
