using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Bindable article-storage engine options under <c>StorageServer:Storage</c>.
/// </summary>
/// <remarks>
/// <para>
/// Separates the NVMe control/durability tier (<see cref="ControlDir"/>) from the SATA
/// segment tier (<see cref="CacheDir"/>). Phase 1 validates and
/// resolves these paths; it does not create on-disk trees or select a database engine.
/// </para>
/// </remarks>
public sealed class ArticleStorageOptions
{
    /// <summary>Default relative SATA segment/cache directory.</summary>
    public const string DefaultCacheDir = "spool/cache";

    /// <summary>Default relative NVMe control directory.</summary>
    public const string DefaultControlDir = "spool/";

    /// <summary>Default soft journal pressure threshold in bytes (64 MiB).</summary>
    public const long DefaultJournalSoftLimitBytes = 64L * 1024 * 1024;

    /// <summary>Default hard journal reject threshold in bytes (128 MiB).</summary>
    public const long DefaultJournalHardLimitBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Default physical journal checkpoint threshold. <c>0</c> does not checkpoint.
    /// </summary>
    public const long DefaultJournalCheckpointThresholdBytes = 0;

    /// <summary>
    /// Default physical index checkpoint threshold. <c>0</c> does not checkpoint.
    /// </summary>
    public const long DefaultIndexCheckpointThresholdBytes = 0;

    /// <summary>Default target size for a closed segment in bytes (256 MiB).</summary>
    public const long DefaultSegmentTargetSizeBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Default maximum time an active segment may stay open after its first durable article.
    /// </summary>
    public static readonly TimeSpan DefaultMaxSegmentSealDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Default maximum retention age. <see cref="TimeSpan.Zero"/> disables age-based expiration.
    /// </summary>
    public static readonly TimeSpan DefaultMaxRetentionAge = TimeSpan.Zero;

    /// <summary>
    /// Default number of segment files that may accept appends at the same time.
    /// </summary>
    public const int DefaultActiveSegmentCount = 1;

    /// <summary>
    /// Gets or sets the SATA segment/cache root for append-oriented immutable article segments.
    /// </summary>
    /// <remarks>
    /// Relative paths resolve through
    /// <see cref="VectorNNTP.Common.Configuration.ApplicationLocalPath.ResolveApplicationLocalPath"/>
    /// against <see cref="AppContext.BaseDirectory"/>. Absolute paths stay absolute.
    /// Distinct from <see cref="ControlDir"/> and <see cref="StorageServerOptions.LogDir"/>.
    /// Need not exist at validation time; validation must not create contents.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string CacheDir { get; set; } = DefaultCacheDir;

    /// <summary>
    /// Gets or sets the NVMe control-tier root (journal, index, segment catalogue, telemetry).
    /// </summary>
    /// <remarks>
    /// Relative paths resolve through
    /// <see cref="VectorNNTP.Common.Configuration.ApplicationLocalPath.ResolveApplicationLocalPath"/>
    /// against <see cref="AppContext.BaseDirectory"/>. Distinct from
    /// <see cref="CacheDir"/> and <see cref="StorageServerOptions.LogDir"/>.
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
    /// Gets or sets the physical <c>article.journal</c> length, in bytes, at which maintenance
    /// checkpoints the journal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>0</c> disables checkpointing, including when the maintenance worker is enabled.
    /// A positive value checkpoints when <c>JournalPhysicalBytes</c> is greater than or equal
    /// to this threshold.
    /// </para>
    /// <para>
    /// This is the on-disk journal length. It is not <see cref="JournalSoftLimitBytes"/> or
    /// <see cref="JournalHardLimitBytes"/>, which bound outstanding recoverable payload.
    /// </para>
    /// </remarks>
    [Range(0, long.MaxValue)]
    public long JournalCheckpointThresholdBytes { get; set; } = DefaultJournalCheckpointThresholdBytes;

    /// <summary>
    /// Gets or sets the physical <c>article.index</c> frame-history length, in bytes, at which
    /// maintenance checkpoints the index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>0</c> disables checkpointing, including when the maintenance worker is enabled.
    /// A positive value checkpoints when <c>IndexPhysicalBytes</c> is greater than or equal
    /// to this threshold.
    /// </para>
    /// <para>
    /// <c>IndexPhysicalBytes</c> is the durable frame payload eligible for prefix retirement.
    /// It excludes a <c>VNID</c> header. It is not the in-memory row count and not the snapshot
    /// file length.
    /// </para>
    /// </remarks>
    [Range(0, long.MaxValue)]
    public long IndexCheckpointThresholdBytes { get; set; } = DefaultIndexCheckpointThresholdBytes;

    /// <summary>
    /// Gets or sets the target closed-segment size in bytes.
    /// The active segment seals when the next article would exceed this size, unless it is still empty.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long SegmentTargetSizeBytes { get; set; } = DefaultSegmentTargetSizeBytes;

    /// <summary>
    /// Gets or sets how long an active segment may stay open after its first durable article.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The segment still seals when the next article would exceed
    /// <see cref="SegmentTargetSizeBytes"/>. This delay is the second condition: a segment
    /// that never reaches the size target seals once it has held a durable article this long.
    /// </para>
    /// <para>
    /// <c>00:00:00</c> disables age sealing. Negative values are rejected. The delay is measured
    /// from the durable activation instant of that segment, not from process start, and an
    /// empty active segment is not sealed because the delay elapsed. A wall clock behind that
    /// instant does not add the backward step; the segment still seals after at most one delay.
    /// </para>
    /// </remarks>
    public TimeSpan MaxSegmentSealDelay { get; set; } = DefaultMaxSegmentSealDelay;

    /// <summary>
    /// Gets or sets the maximum age of a bulk-committed article before it is eligible to leave retention.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>00:00:00</c> disables age-based expiration. Negative values are rejected. There is no
    /// finite default. Age is measured from durable Accept arrival, not from a Message-ID or
    /// Date header and not from index <c>LastAccessUtc</c>.
    /// </para>
    /// <para>
    /// The option does not delete segments, rewrite articles, or change index state. A later
    /// executor would turn eligibility into an <c>Evicted</c> tombstone.
    /// An article whose arrival time cannot be reconstructed is not eligible.
    /// </para>
    /// </remarks>
    public TimeSpan MaxRetentionAge { get; set; } = DefaultMaxRetentionAge;

    /// <summary>
    /// Gets or sets how many segment files may accept appends concurrently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>1</c> keeps a single active segment. <c>2</c> or <c>4</c> gives each active segment
    /// its own file, segment id, and writer. Writers append only to the segment they own.
    /// </para>
    /// <para>
    /// This does not add disks or segment roots. Every file stays under <see cref="CacheDir"/>.
    /// Values outside 1, 2, and 4 are rejected.
    /// </para>
    /// </remarks>
    [Range(1, 4)]
    public int ActiveSegmentCount { get; set; } = DefaultActiveSegmentCount;

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
    /// Ordinary Closed-segment eligibility thresholds. Compaction is always part of maintenance.
    /// </remarks>
    public ArticleCompactionPolicyOptions Compaction { get; set; } = new();

    /// <summary>
    /// Gets or sets process-local SATA capacity admission under
    /// <c>StorageServer:Storage:Capacity</c>.
    /// </summary>
    /// <remarks>
    /// Capacity admission is always on. Percentages are integers from 0 to 100.
    /// Reservations are process-local only. These ceilings are not the bulk retention
    /// watermarks in <see cref="BulkPressure"/>.
    /// </remarks>
    public ArticleCapacityOptions Capacity { get; set; } = new();

    /// <summary>
    /// Gets or sets cache-volume retention watermarks under
    /// <c>StorageServer:Storage:BulkPressure</c>.
    /// </summary>
    /// <remarks>
    /// Classifies physical used space on <see cref="CacheDir"/>. It does not classify
    /// journal <c>OutstandingRecoverableBytes</c> and it does not change
    /// <see cref="Capacity"/>.
    /// </remarks>
    public BulkStoragePressureOptions BulkPressure { get; set; } = new();
}
