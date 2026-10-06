namespace VectorNNTP.StorageServer.Storage.Engine.Maintenance;

/// <summary>Outcome of one <see cref="StorageMaintenanceCoordinator.RunOnceAsync"/> invocation.</summary>
public enum StorageMaintenanceOutcome : byte
{
    /// <summary>No Retired reclaim candidate and no compaction work to continue or start.</summary>
    NoWork = 1,

    /// <summary>Physically reclaimed one already-Retired segment (no new compaction).</summary>
    Reclaimed = 2,

    /// <summary>CompactionCommitted is durable; retirement did not complete in this invocation.</summary>
    Compacted = 3,

    /// <summary>Source is durably Retired; physical reclaim did not complete in this invocation.</summary>
    Retired = 4,

    /// <summary>CompactionCommitted → Retired → physical reclaim completed in this invocation.</summary>
    CompactedAndReclaimed = 5,

    /// <summary>Compaction ran but Present@source remain; CompactionCommitted was not appended.</summary>
    Incomplete = 6,

    /// <summary>Stale policy/hint or lifecycle race; no durable maintenance progress expected.</summary>
    Skipped = 7,

    /// <summary>Storage/journal/protocol failure from an execution primitive.</summary>
    Failed = 8,
}

/// <summary>Result of one storage-maintenance coordinator invocation.</summary>
/// <param name="Outcome">High-level maintenance outcome.</param>
/// <param name="SegmentId">Primary segment involved (default when none).</param>
/// <param name="CompactionId">Compaction identity when known (0 otherwise).</param>
/// <param name="CompactionAttempted">True when <c>CompactClosedSegmentAsync</c> was invoked.</param>
/// <param name="CompactionCommitted">True when CompactionCommitted is durable after this call.</param>
/// <param name="RetirementAttempted">True when <c>RetireCompactedSegmentAsync</c> was invoked.</param>
/// <param name="Retired">True when the source is Retired after this call.</param>
/// <param name="ReclamationAttempted">True when <c>ReclaimRetiredSegmentAsync</c> was invoked.</param>
/// <param name="Reclaimed">True when physical reclamation succeeded (or was already done).</param>
/// <param name="SkipReason">Optional reason for <see cref="StorageMaintenanceOutcome.Skipped"/> / diagnostics.</param>
/// <param name="RelocatedArticleCount">
/// Present articles relocated during compaction in this invocation (from compaction primitive).
/// </param>
/// <param name="SourceSizeBytes">Source segment size when known without an extra catalogue scan.</param>
/// <param name="SourceLiveBytes">Source live bytes when known without an extra catalogue scan.</param>
/// <param name="SourceDeadBytes">Source dead bytes when known without an extra catalogue scan.</param>
/// <param name="SourceDeadRatio">Source dead ratio when accounting is present.</param>
/// <param name="ReclaimedSegmentSizeBytes">
/// Catalogue size of a reclaimed segment captured before physical delete when already available.
/// </param>
/// <param name="AdmissionPressure">
/// True when article admission was under MaximumUtilization pressure at observation time.
/// </param>
/// <param name="AdmissionRecoveryTargetBytes">
/// Physical UsedBytes that must disappear before minimum-size admission can succeed.
/// </param>
/// <param name="CapacityUsedBytes">Observed DriveInfo UsedBytes when capacity admission is enabled.</param>
/// <param name="CapacityTotalBytes">Observed DriveInfo TotalBytes when capacity admission is enabled.</param>
/// <param name="CapacityArticleReservedBytes">Observed process-local article reservations.</param>
/// <param name="CapacityCompactionReservedBytes">Observed process-local compaction reservations.</param>
/// <param name="DeferredOpenCompactionId">
/// When Phase 5F.3 fall-through deferred an open compaction after capacity zero-progress,
/// the deferred compaction id (null when none).
/// </param>
/// <param name="DeferredOpenSourceSegmentId">Source segment of the deferred open compaction.</param>
/// <param name="DeferredOpenSkipReason">
/// Skip reason from the deferred open compaction (typically
/// <see cref="StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress"/>).
/// </param>
/// <param name="DeferredOpenCompactionCount">
/// Number of open compactions that capacity-yielded this run before the primary outcome
/// (0 when none; may be &gt; 1 after Phase 5F.4 same-run rotation).
/// </param>
/// <param name="DestinationSegmentId">
/// Segment that received a relocated Present article when this run rewrote a closed source.
/// Null when this run did not publish a relocation.
/// </param>
/// <param name="RewriteDensitySkipCount">
/// Closed segments left in place because their dead ratio was below
/// <c>MinimumDeadRatio</c>. Zero when no such segment was observed.
/// </param>
/// <param name="BulkPressureState">
/// Cache-volume watermark observed for this cycle. Null when this result was built before
/// observation. This is not <c>StorageWritePressure</c>.
/// </param>
/// <param name="BulkUsedPercent">Truncated cache-volume used percent. Null when unobserved.</param>
/// <param name="BulkFreeBytes">Cache-volume free bytes. Null when unobserved.</param>
/// <param name="BulkAvailableReserveBytes">
/// Free bytes above the three configured reserves, floored at zero. Null when unobserved.
/// </param>
/// <param name="BulkMaintenanceMode">Stable maintenance posture name. Null when unobserved.</param>
/// <param name="BulkRewriteSuppressed">
/// True when a new low-density rewrite was withheld because of bulk cache-volume pressure.
/// </param>
/// <param name="BulkEmergencyAdmissionProtectionRequired">
/// True when the cache volume is in Emergency. Accept does not read this flag.
/// </param>
public readonly record struct StorageMaintenanceResult(
    StorageMaintenanceOutcome Outcome,
    SegmentId SegmentId,
    ulong CompactionId,
    bool CompactionAttempted,
    bool CompactionCommitted,
    bool RetirementAttempted,
    bool Retired,
    bool ReclamationAttempted,
    bool Reclaimed,
    string? SkipReason = null,
    int RelocatedArticleCount = 0,
    long? SourceSizeBytes = null,
    long? SourceLiveBytes = null,
    long? SourceDeadBytes = null,
    double? SourceDeadRatio = null,
    long? ReclaimedSegmentSizeBytes = null,
    bool? AdmissionPressure = null,
    long? AdmissionRecoveryTargetBytes = null,
    long? CapacityUsedBytes = null,
    long? CapacityTotalBytes = null,
    long? CapacityArticleReservedBytes = null,
    long? CapacityCompactionReservedBytes = null,
    ulong? DeferredOpenCompactionId = null,
    SegmentId? DeferredOpenSourceSegmentId = null,
    string? DeferredOpenSkipReason = null,
    int DeferredOpenCompactionCount = 0,
    ulong? DestinationSegmentId = null,
    int RewriteDensitySkipCount = 0,
    string? BulkPressureState = null,
    int? BulkUsedPercent = null,
    long? BulkFreeBytes = null,
    long? BulkAvailableReserveBytes = null,
    string? BulkMaintenanceMode = null,
    bool BulkRewriteSuppressed = false,
    bool BulkEmergencyAdmissionProtectionRequired = false);
