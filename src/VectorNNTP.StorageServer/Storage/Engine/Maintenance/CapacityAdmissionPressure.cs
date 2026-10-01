using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Storage.Engine.Maintenance;

/// <summary>
/// Point-in-time capacity admission pressure observation for maintenance (Phase 5F.2).
/// Not persisted; recomputed each <see cref="StorageMaintenanceCoordinator.RunOnceAsync"/>.
/// </summary>
/// <param name="CapacityAdmissionEnabled">Whether process-local capacity admission is configured.</param>
/// <param name="IsUnderAdmissionPressure">
/// True when a minimum-size article admission would be rejected under MaximumUtilization.
/// </param>
/// <param name="UsedBytes">DriveInfo used bytes from the capacity snapshot.</param>
/// <param name="TotalBytes">DriveInfo total bytes from the capacity snapshot.</param>
/// <param name="AvailableBytes">DriveInfo available bytes from the capacity snapshot.</param>
/// <param name="ArticleReservedBytes">Process-local segment-copy reservations on this volume's ledger.</param>
/// <param name="CompactionReservedBytes">Process-local compaction destination reservations.</param>
/// <param name="MaximumUtilization">Article admission ceiling as an integer percent of TotalBytes.</param>
/// <param name="CompactionHeadroom">Additional percentage points allowed for compaction destinations.</param>
/// <param name="MaximumUsageCapacity">Physical usage percent that latches pressure recovery.</param>
/// <param name="FreeCapacity">Percentage points reclaimed below the usage trigger.</param>
/// <param name="IsUnderUsagePressure">
/// True after physical usage has reached <paramref name="MaximumUsageCapacity"/> until
/// <c>UsedBytes</c> is at or below the physical recovery target. Logical eviction does not clear it.
/// </param>
/// <param name="UsageRecoveryTargetBytes">
/// Floor((MaximumUsageCapacity - FreeCapacity) percent of TotalBytes). Pressure stays active
/// until filesystem UsedBytes is at or below this value.
/// </param>
/// <param name="ArticleCeilingBytes">Floor(MaximumUtilization percent of TotalBytes) via ledger scaling.</param>
/// <param name="CompactionCeilingBytes">
/// Floor((MaximumUtilization + CompactionHeadroom) percent of TotalBytes) via ledger scaling.
/// </param>
/// <param name="AdmissionRecoveryTargetBytes">
/// Physical UsedBytes that must disappear before minimum-size article admission can succeed.
/// </param>
/// <param name="MinimumAdmissionRequiredBytes">
/// Required-bytes floor used for pressure / recovery-target (segment minimum record length).
/// </param>
/// <param name="CheckpointReservedBytes">
/// Process-local bytes reserved for checkpoint temporary files. Included in the article
/// recovery target and in compaction feasibility. Zero when no checkpoint temp is reserved.
/// </param>
/// <param name="JournalReservedBytes">
/// Process-local journal-sequence reservations on this volume's ledger. Included in admission
/// and compaction feasibility. Zero when the control volume is a different ledger.
/// </param>
/// <param name="IndexReservedBytes">
/// Process-local Present-frame reservations on this volume's ledger. Included in admission
/// and compaction feasibility. Zero when the control volume is a different ledger.
/// </param>
/// <param name="CompactionJournalReservedBytes">
/// Process-local compaction-journal frame reservations on this volume's ledger. Included in
/// admission and compaction feasibility. Zero when the control volume is a different ledger.
/// </param>
public readonly record struct CapacityAdmissionPressureSnapshot(
    bool CapacityAdmissionEnabled,
    bool IsUnderAdmissionPressure,
    long UsedBytes,
    long TotalBytes,
    long AvailableBytes,
    long ArticleReservedBytes,
    long CompactionReservedBytes,
    int MaximumUtilization,
    int CompactionHeadroom,
    int MaximumUsageCapacity,
    int FreeCapacity,
    bool IsUnderUsagePressure,
    long UsageRecoveryTargetBytes,
    long ArticleCeilingBytes,
    long CompactionCeilingBytes,
    long AdmissionRecoveryTargetBytes,
    long MinimumAdmissionRequiredBytes,
    long CheckpointReservedBytes = 0,
    long JournalReservedBytes = 0,
    long IndexReservedBytes = 0,
    long CompactionJournalReservedBytes = 0)
{
    /// <summary>Empty snapshot when capacity admission is disabled.</summary>
    public static CapacityAdmissionPressureSnapshot Disabled { get; } = new(
        CapacityAdmissionEnabled: false,
        IsUnderAdmissionPressure: false,
        UsedBytes: 0,
        TotalBytes: 0,
        AvailableBytes: 0,
        ArticleReservedBytes: 0,
        CompactionReservedBytes: 0,
        MaximumUtilization: 0,
        CompactionHeadroom: 0,
        MaximumUsageCapacity: 0,
        FreeCapacity: 0,
        IsUnderUsagePressure: false,
        UsageRecoveryTargetBytes: 0,
        ArticleCeilingBytes: 0,
        CompactionCeilingBytes: 0,
        AdmissionRecoveryTargetBytes: 0,
        MinimumAdmissionRequiredBytes: SegmentRecordCodec.MinimumRecordLength,
        CheckpointReservedBytes: 0,
        JournalReservedBytes: 0,
        IndexReservedBytes: 0,
        CompactionJournalReservedBytes: 0);

    /// <summary>
    /// Builds a snapshot from a capacity read and reservation counters using ledger arithmetic.
    /// </summary>
    public static CapacityAdmissionPressureSnapshot FromCapacityState(
        in StorageCapacitySnapshot capacity,
        long articleReservedBytes,
        long compactionReservedBytes,
        int maximumUtilization,
        int compactionHeadroom,
        int maximumUsageCapacity = 80,
        int freeCapacity = 5,
        long minimumAdmissionRequiredBytes = SegmentRecordCodec.MinimumRecordLength,
        long checkpointReservedBytes = 0,
        long journalReservedBytes = 0,
        long indexReservedBytes = 0,
        long compactionJournalReservedBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumAdmissionRequiredBytes);

        var articleCeiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(
            capacity.TotalBytes,
            maximumUtilization);
        var compactionCeiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(
            capacity.TotalBytes,
            maximumUtilization + compactionHeadroom);
        var recoveryPercent = Math.Max(0, maximumUsageCapacity - freeCapacity);
        var usageRecoveryTarget = ProcessLocalCapacityLedger.ComputeCeilingBytes(
            capacity.TotalBytes,
            recoveryPercent);
        var underUsage = ProcessLocalCapacityLedger.IsUsageAtOrAbove(
            capacity.UsedBytes,
            capacity.TotalBytes,
            maximumUsageCapacity);
        var recoveryTarget = ProcessLocalCapacityLedger.ComputeAdmissionRecoveryTargetBytes(
            capacity.UsedBytes,
            articleReservedBytes,
            compactionReservedBytes,
            capacity.TotalBytes,
            maximumUtilization,
            minimumAdmissionRequiredBytes,
            checkpointReservedBytes,
            journalReservedBytes,
            indexReservedBytes,
            compactionJournalReservedBytes);

        return new CapacityAdmissionPressureSnapshot(
            CapacityAdmissionEnabled: true,
            IsUnderAdmissionPressure: recoveryTarget > 0,
            UsedBytes: capacity.UsedBytes,
            TotalBytes: capacity.TotalBytes,
            AvailableBytes: capacity.AvailableBytes,
            ArticleReservedBytes: articleReservedBytes,
            CompactionReservedBytes: compactionReservedBytes,
            MaximumUtilization: maximumUtilization,
            CompactionHeadroom: compactionHeadroom,
            MaximumUsageCapacity: maximumUsageCapacity,
            FreeCapacity: freeCapacity,
            IsUnderUsagePressure: underUsage,
            UsageRecoveryTargetBytes: usageRecoveryTarget,
            ArticleCeilingBytes: articleCeiling,
            CompactionCeilingBytes: compactionCeiling,
            AdmissionRecoveryTargetBytes: recoveryTarget,
            MinimumAdmissionRequiredBytes: minimumAdmissionRequiredBytes,
            CheckpointReservedBytes: checkpointReservedBytes,
            JournalReservedBytes: journalReservedBytes,
            IndexReservedBytes: indexReservedBytes,
            CompactionJournalReservedBytes: compactionJournalReservedBytes);
    }
}

/// <summary>Distinguishable maintenance skip reasons for capacity-pressure paths (Phase 5F.2).</summary>
public static class StorageMaintenanceSkipReasons
{
    /// <summary>New compaction cannot begin useful progress under MaxUtil + CompactionHeadroom.</summary>
    public const string CapacityInsufficientHeadroom = "capacity-insufficient-headroom";

    /// <summary>Admission pressure exists but no Closed victim can make useful compaction progress.</summary>
    public const string CapacityPressureNoFeasibleCandidate = "capacity-pressure-no-feasible-candidate";

    /// <summary>
    /// Usage pressure is latched and logical eviction plus compaction could not reduce
    /// filesystem UsedBytes to the physical recovery target.
    /// </summary>
    public const string CapacityPressureUnrecoverable = "capacity-pressure-unrecoverable";

    /// <summary>Open compaction continued but relocated zero articles due to capacity denial.</summary>
    public const string CapacityOpenCompactionZeroProgress = "capacity-open-compaction-zero-progress";

    /// <summary>
    /// Source cannot be committed or retired yet because a publishable PhysicalWritten
    /// or pre-PhysicalWritten append still targets it. Not a capacity decision.
    /// </summary>
    public const string PendingPhysicalWritten = "pending-physical-written";

    /// <summary>True when <paramref name="reason"/> is a capacity-pressure skip (not stale/race).</summary>
    public static bool IsCapacityPressureReason(string? reason) =>
        reason is CapacityInsufficientHeadroom
            or CapacityPressureNoFeasibleCandidate
            or CapacityOpenCompactionZeroProgress
            || (reason is not null
                && reason.StartsWith("capacity", StringComparison.Ordinal));
}
