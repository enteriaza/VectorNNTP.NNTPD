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
/// <param name="ArticleReservedBytes">Process-local Accept reservations awaiting PhysicalWritten.</param>
/// <param name="CompactionReservedBytes">Process-local compaction destination reservations.</param>
/// <param name="MaximumUtilization">Article admission utilization ceiling fraction.</param>
/// <param name="CompactionHeadroom">Additional utilization allowed for compaction destinations.</param>
/// <param name="ArticleCeilingBytes">Floor(MaximumUtilization × TotalBytes) via ledger scaling.</param>
/// <param name="CompactionCeilingBytes">
/// Floor((MaximumUtilization + CompactionHeadroom) × TotalBytes) via ledger scaling.
/// </param>
/// <param name="AdmissionRecoveryTargetBytes">
/// Physical UsedBytes that must disappear before minimum-size article admission can succeed.
/// </param>
/// <param name="MinimumAdmissionRequiredBytes">
/// Required-bytes floor used for pressure / recovery-target (segment minimum record length).
/// </param>
public readonly record struct CapacityAdmissionPressureSnapshot(
    bool CapacityAdmissionEnabled,
    bool IsUnderAdmissionPressure,
    long UsedBytes,
    long TotalBytes,
    long AvailableBytes,
    long ArticleReservedBytes,
    long CompactionReservedBytes,
    double MaximumUtilization,
    double CompactionHeadroom,
    long ArticleCeilingBytes,
    long CompactionCeilingBytes,
    long AdmissionRecoveryTargetBytes,
    long MinimumAdmissionRequiredBytes)
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
        ArticleCeilingBytes: 0,
        CompactionCeilingBytes: 0,
        AdmissionRecoveryTargetBytes: 0,
        MinimumAdmissionRequiredBytes: SegmentRecordCodec.MinimumRecordLength);

    /// <summary>
    /// Builds a snapshot from a capacity read and reservation counters using ledger arithmetic.
    /// </summary>
    public static CapacityAdmissionPressureSnapshot FromCapacityState(
        in StorageCapacitySnapshot capacity,
        long articleReservedBytes,
        long compactionReservedBytes,
        double maximumUtilization,
        double compactionHeadroom,
        long minimumAdmissionRequiredBytes = SegmentRecordCodec.MinimumRecordLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumAdmissionRequiredBytes);

        var articleCeiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(
            capacity.TotalBytes,
            maximumUtilization);
        var compactionCeiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(
            capacity.TotalBytes,
            maximumUtilization + compactionHeadroom);
        var recoveryTarget = ProcessLocalCapacityLedger.ComputeAdmissionRecoveryTargetBytes(
            capacity.UsedBytes,
            articleReservedBytes,
            compactionReservedBytes,
            capacity.TotalBytes,
            maximumUtilization,
            minimumAdmissionRequiredBytes);

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
            ArticleCeilingBytes: articleCeiling,
            CompactionCeilingBytes: compactionCeiling,
            AdmissionRecoveryTargetBytes: recoveryTarget,
            MinimumAdmissionRequiredBytes: minimumAdmissionRequiredBytes);
    }
}

/// <summary>Distinguishable maintenance skip reasons for capacity-pressure paths (Phase 5F.2).</summary>
public static class StorageMaintenanceSkipReasons
{
    /// <summary>New compaction cannot begin useful progress under MaxUtil + CompactionHeadroom.</summary>
    public const string CapacityInsufficientHeadroom = "capacity-insufficient-headroom";

    /// <summary>Admission pressure exists but no Closed victim can make useful compaction progress.</summary>
    public const string CapacityPressureNoFeasibleCandidate = "capacity-pressure-no-feasible-candidate";

    /// <summary>Open compaction continued but relocated zero articles due to capacity denial.</summary>
    public const string CapacityOpenCompactionZeroProgress = "capacity-open-compaction-zero-progress";

    /// <summary>True when <paramref name="reason"/> is a capacity-pressure skip (not stale/race).</summary>
    public static bool IsCapacityPressureReason(string? reason) =>
        reason is CapacityInsufficientHeadroom
            or CapacityPressureNoFeasibleCandidate
            or CapacityOpenCompactionZeroProgress
            || (reason is not null
                && reason.StartsWith("capacity", StringComparison.Ordinal));
}
