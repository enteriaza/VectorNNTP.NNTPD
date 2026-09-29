using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Bindable compaction victim-selection and maintenance-worker options under
/// <c>StorageServer:Storage:Compaction</c>.
/// </summary>
/// <remarks>
/// <para>
/// Pure policy thresholds plus optional automated maintenance scheduling.
/// <see cref="Enabled"/> defaults to <see langword="false"/> so omission does not trigger
/// compaction victim selection. <see cref="MaintenanceEnabled"/> is a separate switch for the
/// periodic worker and also defaults to <see langword="false"/>.
/// </para>
/// <para>
/// When <see cref="Enabled"/> is <see langword="true"/> and both thresholds are <c>0</c>,
/// every Closed segment with <c>SizeBytes &gt; 0</c> is eligible (including zero-dead).
/// That mode is explicit and aggressive; leave <see cref="Enabled"/> false to disable.
/// </para>
/// <para>
/// <see cref="MaintenanceEnabled"/> may be <see langword="true"/> while <see cref="Enabled"/>
/// is <see langword="false"/>: the worker still invokes the coordinator so durable pending
/// retirement/reclamation/open-compaction work can complete without selecting new Closed victims.
/// </para>
/// </remarks>
public sealed class ArticleCompactionPolicyOptions
{
    /// <summary>Default minimum absolute dead bytes (64 MiB).</summary>
    public const long DefaultMinimumDeadBytes = 64L * 1024 * 1024;

    /// <summary>Default minimum dead-byte fraction of segment size (10%).</summary>
    public const double DefaultMinimumDeadRatio = 0.10;

    /// <summary>Default delay between maintenance runs (1 minute).</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets whether compaction victim selection is enabled.
    /// </summary>
    /// <remarks>
    /// Default <see langword="false"/>. When false, selection APIs return no Closed victim.
    /// Does not disable the maintenance worker (see <see cref="MaintenanceEnabled"/>).
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets whether the StorageServer maintenance worker loop is enabled.
    /// </summary>
    /// <remarks>
    /// Default <see langword="false"/>. When false, <c>StorageMaintenanceService</c> does not
    /// invoke the coordinator. Manual coordinator invocation remains available.
    /// </remarks>
    public bool MaintenanceEnabled { get; set; }

    /// <summary>
    /// Gets or sets the minimum delay after each <c>RunOnceAsync</c> completes before the next run.
    /// </summary>
    /// <remarks>
    /// Must be greater than <see cref="TimeSpan.Zero"/>. First run after worker start is immediate
    /// (no initial delay). Cleared separately from <see cref="MaintenanceEnabled"/>.
    /// </remarks>
    public TimeSpan Interval { get; set; } = DefaultInterval;

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
