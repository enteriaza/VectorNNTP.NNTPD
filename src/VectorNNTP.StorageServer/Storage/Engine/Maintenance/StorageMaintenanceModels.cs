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
    string? SkipReason = null);
