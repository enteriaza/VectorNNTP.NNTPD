using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Bindable compaction victim-selection and maintenance-interval options under
/// <c>StorageServer:Storage:Compaction</c>.
/// </summary>
/// <remarks>
/// <para>
/// Maintenance always runs and compaction is always part of it. There is no switch that
/// disables either. <see cref="MinimumDeadBytes"/> and <see cref="MinimumDeadRatio"/> decide
/// whether a Closed segment is eligible for ordinary compaction. They do not turn compaction
/// off. Capacity-pressure recovery can still compact a Closed segment that holds logically
/// evicted bytes when those ordinary thresholds are not met.
/// </para>
/// <para>
/// When both thresholds are <c>0</c>, every Closed segment with <c>SizeBytes &gt; 0</c> is
/// eligible, including zero-dead segments.
/// </para>
/// </remarks>
public sealed class ArticleCompactionPolicyOptions
{
    /// <summary>Default minimum absolute dead bytes (64 MiB).</summary>
    public const long DefaultMinimumDeadBytes = 64L * 1024 * 1024;

    /// <summary>Default minimum dead-byte percentage of segment size (10%).</summary>
    public const int DefaultMinimumDeadRatio = 10;

    /// <summary>Default delay between maintenance runs (1 minute).</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the minimum delay after each <c>RunOnceAsync</c> completes before the next run.
    /// </summary>
    /// <remarks>
    /// Must be greater than <see cref="TimeSpan.Zero"/>. First run after worker start is immediate
    /// (no initial delay). The worker always runs; this interval is not an enable switch.
    /// </remarks>
    public TimeSpan Interval { get; set; } = DefaultInterval;

    /// <summary>
    /// Gets or sets the minimum absolute dead bytes required for compaction eligibility.
    /// </summary>
    /// <remarks>Must be ≥ <c>0</c>. Combined with <see cref="MinimumDeadRatio"/> (AND).</remarks>
    [Range(0, long.MaxValue)]
    public long MinimumDeadBytes { get; set; } = DefaultMinimumDeadBytes;

    /// <summary>
    /// Gets or sets the minimum dead-byte percentage of segment size, from 0 to 100.
    /// </summary>
    /// <remarks>
    /// <c>10</c> means 10 percent. Eligibility is <c>DeadBytes * 100 &gt;= SizeBytes * MinimumDeadRatio</c>.
    /// Combined with <see cref="MinimumDeadBytes"/> (AND). Fractional values such as <c>0.10</c> are not
    /// accepted.
    /// </remarks>
    [Range(0, 100)]
    public int MinimumDeadRatio { get; set; } = DefaultMinimumDeadRatio;
}
