using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Bindable article-storage engine options under <c>StorageServer:Storage</c>.
/// </summary>
/// <remarks>
/// <para>
/// Separates the NVMe control/durability tier (<see cref="ControlDir"/>) from the SATA
/// segment tier (<see cref="StorageServerOptions.CacheDir"/>). Phase 1 validates and
/// resolves these paths; it does not create on-disk trees or select a database engine.
/// </para>
/// </remarks>
public sealed class ArticleStorageOptions
{
    /// <summary>Default relative NVMe control directory.</summary>
    public const string DefaultControlDir = "control/";

    /// <summary>Default soft journal pressure threshold in bytes (64 MiB).</summary>
    public const long DefaultJournalSoftLimitBytes = 64L * 1024 * 1024;

    /// <summary>Default hard journal reject threshold in bytes (128 MiB).</summary>
    public const long DefaultJournalHardLimitBytes = 128L * 1024 * 1024;

    /// <summary>Default target size for a closed segment in bytes (256 MiB).</summary>
    public const long DefaultSegmentTargetSizeBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the NVMe control-tier root (journal, index, segment catalogue, telemetry).
    /// </summary>
    /// <remarks>
    /// Relative paths resolve through
    /// <see cref="VectorNNTP.NNTPD.Configuration.ApplicationLocalPath.ResolveApplicationLocalPath"/>
    /// against <see cref="AppContext.BaseDirectory"/>. Distinct from
    /// <see cref="StorageServerOptions.CacheDir"/> and <see cref="StorageServerOptions.LogDir"/>.
    /// Need not exist at validation time; validation must not create contents.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string ControlDir { get; set; } = DefaultControlDir;

    /// <summary>
    /// Gets or sets the journal soft limit in bytes. Above this, write pressure rises.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long JournalSoftLimitBytes { get; set; } = DefaultJournalSoftLimitBytes;

    /// <summary>
    /// Gets or sets the journal hard limit in bytes. At or above this, Accept rejects writes.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long JournalHardLimitBytes { get; set; } = DefaultJournalHardLimitBytes;

    /// <summary>
    /// Gets or sets the target closed-segment size in bytes (future rollover hint).
    /// </summary>
    [Range(1, long.MaxValue)]
    public long SegmentTargetSizeBytes { get; set; } = DefaultSegmentTargetSizeBytes;

    /// <summary>
    /// Gets or sets process-local article memory-cache bounds under
    /// <c>StorageServer:Storage:ArticleCache</c>.
    /// </summary>
    public ArticleMemoryCacheOptions ArticleCache { get; set; } = new();

    /// <summary>
    /// Gets or sets Closed-segment compaction victim-selection thresholds under
    /// <c>StorageServer:Storage:Compaction</c>.
    /// </summary>
    /// <remarks>
    /// Policy only; does not schedule or execute compaction. Default
    /// <see cref="ArticleCompactionPolicyOptions.Enabled"/> is <see langword="false"/>.
    /// </remarks>
    public ArticleCompactionPolicyOptions Compaction { get; set; } = new();

    /// <summary>
    /// Gets or sets process-local SATA capacity admission under
    /// <c>StorageServer:Storage:Capacity</c>.
    /// </summary>
    /// <remarks>
    /// Default <see cref="ArticleCapacityOptions.Enabled"/> is <see langword="false"/>.
    /// Reservations are process-local only (Phase 5E.1).
    /// </remarks>
    public ArticleCapacityOptions Capacity { get; set; } = new();
}
