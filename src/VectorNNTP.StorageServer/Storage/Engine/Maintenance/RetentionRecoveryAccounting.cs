using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Storage.Engine.Maintenance;

/// <summary>
/// One maintenance cycle's retention recovery. Logical expiration and physical release are
/// separate totals. <see cref="LogicalExpiredBytes"/> is not filesystem free space and must not
/// be treated as <see cref="FreeBytesAfter"/>.
/// </summary>
/// <param name="PressureStateBefore">Bulk class at cycle start. Not journal pressure.</param>
/// <param name="PressureStateAfter">Bulk class at cycle end.</param>
/// <param name="UsedPercentBefore">Truncated used percent at cycle start. Contextual.</param>
/// <param name="UsedPercentAfter">Truncated used percent at cycle end. Contextual.</param>
/// <param name="FreeBytesBefore">Filesystem free bytes at cycle start. Zero when unmeasured.</param>
/// <param name="FreeBytesAfter">Filesystem free bytes at cycle end. Zero when unmeasured.</param>
/// <param name="Measured">True when both snapshots had a positive volume size.</param>
/// <param name="AgeArticlesEvaluated">Present rows the age predicate inspected.</param>
/// <param name="AgeArticlesExpired">Rows the age pass transitioned to Evicted.</param>
/// <param name="AgeExpiredBytes">Record lengths logically expired by the age pass.</param>
/// <param name="PressureArticlesEvaluated">Present rows the pressure window inspected.</param>
/// <param name="PressureArticlesExpired">Rows the pressure pass transitioned to Evicted.</param>
/// <param name="PressureExpiredBytes">Record lengths logically expired by the pressure pass.</param>
/// <param name="LogicalExpiredBytes">
/// <see cref="AgeExpiredBytes"/> plus <see cref="PressureExpiredBytes"/>. Not bytes deleted.
/// </param>
/// <param name="FullyDeadSegmentsReclaimed">Closed fully-dead segments whose file this cycle deleted.</param>
/// <param name="FullyDeadBytesReclaimed">Catalogue size of those deleted files.</param>
/// <param name="RetiredSegmentsReclaimed">Already-retired segments whose file this cycle deleted.</param>
/// <param name="RetiredBytesReclaimed">Catalogue size of those deleted retired files.</param>
/// <param name="CompactionSourcesSelected">Primary compaction attempts in this result. At most one.</param>
/// <param name="CompactionSourceLiveBytes">Source live bytes when a compaction was attempted.</param>
/// <param name="CompactionSourceDeadBytes">Source dead bytes when a compaction was attempted.</param>
/// <param name="CompactionBytesRelocated">
/// Source live bytes copied when compaction committed and relocated at least one article.
/// Not added again into <see cref="NetPhysicalRecoveryBytes"/>.
/// </param>
/// <param name="CompactionDestinationBytesWritten">
/// Same copied live bytes. They occupy destination space until the source file is deleted.
/// </param>
/// <param name="CompactionSourceBytesReclaimed">
/// Source catalogue size, and only when this cycle deleted that source file.
/// </param>
/// <param name="CompactionInterrupted">True when compaction was attempted and did not commit.</param>
/// <param name="NetPhysicalRecoveryBytes">
/// Files deleted this cycle minus destination bytes written. Negative when a committed rewrite
/// has not yet deleted its source. Never derived from <see cref="LogicalExpiredBytes"/>.
/// </param>
/// <param name="RewriteSuppressedByReserve">True when bulk pressure withheld a new rewrite.</param>
/// <param name="PressureImproved">
/// True when a measured end snapshot has more free bytes, or a less severe bulk class, than the start.
/// </param>
/// <param name="RecoveryTargetReached">
/// True when the end used percent is below the pressure-expiration recovery target.
/// </param>
/// <param name="ConsecutiveUnimprovedCycles">
/// Process-local count of cycles that stayed at Warning or above without freeing snapshot bytes
/// or lowering the class. Reset when the volume recovers or is unmeasured. Saturated.
/// </param>
/// <param name="MaintenanceDurationMilliseconds">Elapsed time of the coordinator invocation.</param>
public readonly record struct RetentionRecoveryAccounting(
    string PressureStateBefore,
    string PressureStateAfter,
    int UsedPercentBefore,
    int UsedPercentAfter,
    long FreeBytesBefore,
    long FreeBytesAfter,
    bool Measured,
    int AgeArticlesEvaluated,
    int AgeArticlesExpired,
    long AgeExpiredBytes,
    int PressureArticlesEvaluated,
    int PressureArticlesExpired,
    long PressureExpiredBytes,
    long LogicalExpiredBytes,
    int FullyDeadSegmentsReclaimed,
    long FullyDeadBytesReclaimed,
    int RetiredSegmentsReclaimed,
    long RetiredBytesReclaimed,
    int CompactionSourcesSelected,
    long CompactionSourceLiveBytes,
    long CompactionSourceDeadBytes,
    long CompactionBytesRelocated,
    long CompactionDestinationBytesWritten,
    long CompactionSourceBytesReclaimed,
    bool CompactionInterrupted,
    long NetPhysicalRecoveryBytes,
    bool RewriteSuppressedByReserve,
    bool PressureImproved,
    bool RecoveryTargetReached,
    int ConsecutiveUnimprovedCycles,
    double MaintenanceDurationMilliseconds)
{
    /// <summary>Saturation point for <see cref="ConsecutiveUnimprovedCycles"/>. Not a history log.</summary>
    public const int MaxConsecutiveUnimprovedCycles = 1_000_000;

    /// <summary>
    /// Builds the cycle account from the two capacity classifications and the work this result
    /// already recorded. Does not read the filesystem or article payloads.
    /// </summary>
    /// <param name="before">Classification taken before expiration.</param>
    /// <param name="after">Classification taken after the cycle's physical work.</param>
    /// <param name="age">Age-expiration batch. Disabled batches contribute zeros.</param>
    /// <param name="pressure">Pressure-expiration window. An idle window contributes zeros.</param>
    /// <param name="cycle">Maintenance result before this account is attached.</param>
    /// <param name="recoveryTargetPercent">Used percent below which a pressure pass has reached its target.</param>
    /// <param name="consecutiveUnimprovedCycles">Count entering this cycle.</param>
    /// <param name="durationMilliseconds">Elapsed coordinator time.</param>
    /// <returns>The account, including the updated consecutive-cycle count.</returns>
    public static RetentionRecoveryAccounting Compose(
        in BulkStoragePressureEvaluation before,
        in BulkStoragePressureEvaluation after,
        in RetentionExpirationResult age,
        in PressureExpirationResult pressure,
        in StorageMaintenanceResult cycle,
        int recoveryTargetPercent,
        int consecutiveUnimprovedCycles,
        double durationMilliseconds)
    {
        var ageBytes = age.BytesExpired < 0 ? 0 : age.BytesExpired;
        var pressureBytes = pressure.BytesLogicallyExpired < 0 ? 0 : pressure.BytesLogicallyExpired;
        var logical = ageBytes + pressureBytes;

        long fullyDeadBytes = 0;
        var fullyDeadSegments = 0;
        long retiredBytes = 0;
        var retiredSegments = 0;
        long sourceReclaimed = 0;
        var released = cycle.ReclaimedSegmentSizeBytes ?? 0L;
        if (released < 0)
        {
            released = 0;
        }
        if (cycle.PhysicalFileDeleted)
        {
            if (cycle.FullyDeadFileReclaim)
            {
                fullyDeadSegments = 1;
                fullyDeadBytes = released;
            }
            else if (cycle.Outcome == StorageMaintenanceOutcome.CompactedAndReclaimed)
            {
                sourceReclaimed = released;
            }
            else if (cycle.Outcome == StorageMaintenanceOutcome.Reclaimed && !cycle.CompactionAttempted)
            {
                retiredSegments = 1;
                retiredBytes = released;
            }
        }

        var compactionSelected = cycle.CompactionAttempted ? 1 : 0;
        var sourceLive = compactionSelected == 1 ? NonNegative(cycle.SourceLiveBytes) : 0L;
        var sourceDead = compactionSelected == 1 ? NonNegative(cycle.SourceDeadBytes) : 0L;
        var copied = cycle.CompactionCommitted
            && cycle.RelocatedArticleCount > 0
            && cycle.SourceLiveBytes is not null
            ? sourceLive
            : 0L;
        var interrupted = cycle.CompactionAttempted && !cycle.CompactionCommitted;
        var net = fullyDeadBytes + retiredBytes + sourceReclaimed - copied;
        var measured = before.Measured && after.Measured;
        var improved = measured
            && (after.FreeBytes > before.FreeBytes || (int)after.State < (int)before.State);
        var targetReached = measured && after.UsedPercent < recoveryTargetPercent;
        var unimproved = NextUnimprovedCount(in before, in after, consecutiveUnimprovedCycles);

        return new RetentionRecoveryAccounting(
            before.State.ToString(),
            after.State.ToString(),
            before.Measured ? before.UsedPercent : 0,
            after.Measured ? after.UsedPercent : 0,
            before.Measured ? before.FreeBytes : 0,
            after.Measured ? after.FreeBytes : 0,
            measured,
            age.PresentEvaluated,
            age.Expired,
            ageBytes,
            pressure.PresentEvaluated,
            pressure.Expired,
            pressureBytes,
            logical,
            fullyDeadSegments,
            fullyDeadBytes,
            retiredSegments,
            retiredBytes,
            compactionSelected,
            sourceLive,
            sourceDead,
            copied,
            copied,
            sourceReclaimed,
            interrupted,
            net,
            cycle.BulkRewriteSuppressed,
            improved,
            targetReached,
            unimproved,
            durationMilliseconds);
    }

    /// <summary>
    /// Increments while the end class is still Warning or worse and neither free bytes nor the
    /// class improved. Resets otherwise.
    /// </summary>
    private static int NextUnimprovedCount(
        in BulkStoragePressureEvaluation before,
        in BulkStoragePressureEvaluation after,
        int consecutiveUnimprovedCycles)
    {
        if (!after.Measured || after.State < BulkStoragePressureState.Warning)
        {
            return 0;
        }

        if (before.Measured
            && ((int)after.State < (int)before.State || after.FreeBytes > before.FreeBytes))
        {
            return 0;
        }

        var next = consecutiveUnimprovedCycles < 0 ? 1 : consecutiveUnimprovedCycles + 1;
        return next > MaxConsecutiveUnimprovedCycles ? MaxConsecutiveUnimprovedCycles : next;
    }

    private static long NonNegative(long? value) => value is null || value.Value < 0 ? 0L : value.Value;
}
