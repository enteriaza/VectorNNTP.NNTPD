namespace VectorNNTP.StorageServer.Storage.Engine.Maintenance;

/// <summary>Non-mutating enrichment of <see cref="StorageMaintenanceResult"/> for operational reporting.</summary>
internal static class StorageMaintenanceResultAccounting
{
    public static StorageMaintenanceResult WithSourceAccounting(
        this StorageMaintenanceResult result,
        in SegmentInfo info)
    {
        if (info.SegmentId != result.SegmentId)
        {
            return result;
        }

        return result with
        {
            SourceSizeBytes = info.SizeBytes,
            SourceLiveBytes = info.LiveBytes,
            SourceDeadBytes = info.DeadBytes,
            SourceDeadRatio = ComputeDeadRatio(info.SizeBytes, info.DeadBytes),
        };
    }

    public static StorageMaintenanceResult WithCompactionExecution(
        this StorageMaintenanceResult result,
        in ArticleCompactionResult compact)
    {
        return result with
        {
            SegmentId = compact.SourceSegmentId,
            CompactionId = compact.CompactionId != 0 ? compact.CompactionId : result.CompactionId,
            RelocatedArticleCount = compact.RelocatedCount,
        };
    }

    public static StorageMaintenanceResult WithReclaimedSize(
        this StorageMaintenanceResult result,
        long reclaimedSegmentSizeBytes) =>
        result with { ReclaimedSegmentSizeBytes = reclaimedSegmentSizeBytes };

    public static StorageMaintenanceResult WithCapacityPressure(
        this StorageMaintenanceResult result,
        in CapacityAdmissionPressureSnapshot pressure)
    {
        if (!pressure.CapacityAdmissionEnabled)
        {
            return result;
        }

        return result with
        {
            AdmissionPressure = pressure.IsUnderAdmissionPressure,
            AdmissionRecoveryTargetBytes = pressure.AdmissionRecoveryTargetBytes,
            CapacityUsedBytes = pressure.UsedBytes,
            CapacityTotalBytes = pressure.TotalBytes,
            CapacityArticleReservedBytes = pressure.ArticleReservedBytes,
            CapacityCompactionReservedBytes = pressure.CompactionReservedBytes,
        };
    }

    /// <summary>
    /// Records that one or more open compactions were deferred this run after capacity
    /// zero-progress (Phase 5F.3 / 5F.4), without claiming deferred work as the primary outcome.
    /// Identity fields describe the first deferred open; <paramref name="deferredCount"/> is the
    /// total number deferred this run.
    /// </summary>
    public static StorageMaintenanceResult WithDeferredOpenCompaction(
        this StorageMaintenanceResult result,
        in StorageMaintenanceResult deferredOpenSkip,
        int deferredCount = 1)
    {
        return result with
        {
            DeferredOpenCompactionId = deferredOpenSkip.CompactionId,
            DeferredOpenSourceSegmentId = deferredOpenSkip.SegmentId,
            DeferredOpenSkipReason = deferredOpenSkip.SkipReason,
            DeferredOpenCompactionCount = deferredCount > 0 ? deferredCount : 1,
        };
    }

    private static double ComputeDeadRatio(long sizeBytes, long deadBytes) =>
        sizeBytes > 0 ? (double)deadBytes / sizeBytes : 0d;
}
