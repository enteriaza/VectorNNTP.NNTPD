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

    private static double ComputeDeadRatio(long sizeBytes, long deadBytes) =>
        sizeBytes > 0 ? (double)deadBytes / sizeBytes : 0d;
}
