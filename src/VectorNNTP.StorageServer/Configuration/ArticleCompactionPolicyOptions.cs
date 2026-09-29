using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Bindable compaction victim-selection policy under
/// <c>StorageServer:Storage:Compaction</c>.
/// </summary>
/// <remarks>
/// <para>
/// Pure policy thresholds. Does not execute compaction, retirement, or reclamation.
/// <see cref="Enabled"/> defaults to <see langword="false"/> so omission does not trigger
/// compaction selection.
/// </para>
/// <para>
/// When <see cref="Enabled"/> is <see langword="true"/> and both thresholds are <c>0</c>,
/// every Closed segment with <c>SizeBytes &gt; 0</c> is eligible (including zero-dead).
/// That mode is explicit and aggressive; leave <see cref="Enabled"/> false to disable.
/// </para>
/// </remarks>
public sealed class ArticleCompactionPolicyOptions
{
    /// <summary>Default minimum absolute dead bytes (64 MiB).</summary>
    public const long DefaultMinimumDeadBytes = 64L * 1024 * 1024;

    /// <summary>Default minimum dead-byte fraction of segment size (10%).</summary>
    public const double DefaultMinimumDeadRatio = 0.10;

    /// <summary>
    /// Gets or sets whether compaction victim selection is enabled.
    /// </summary>
    /// <remarks>
    /// Default <see langword="false"/>. When false, selection APIs return no victim.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the minimum absolute dead bytes required for compaction eligibility.
    /// </summary>
    /// <remarks>Must be ≥ <c>0</c>. Combined with <see cref="MinimumDeadRatio"/> (AND).</remarks>
    [Range(0, long.MaxValue)]
    public long MinimumDeadBytes { get; set; } = DefaultMinimumDeadBytes;

    /// <summary>
    /// Gets or sets the minimum dead-byte ratio (<c>DeadBytes / SizeBytes</c>) in <c>[0, 1]</c>.
    /// </summary>
    /// <remarks>Must be in <c>[0, 1]</c>. Combined with <see cref="MinimumDeadBytes"/> (AND).</remarks>
    [Range(0, 1)]
    public double MinimumDeadRatio { get; set; } = DefaultMinimumDeadRatio;
}
