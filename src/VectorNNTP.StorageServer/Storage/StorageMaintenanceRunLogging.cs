using VectorNNTP.StorageServer.Storage.Engine.Maintenance;

namespace VectorNNTP.StorageServer.Storage;

/// <summary>Maps maintenance results to structured Serilog events (Phase 5D).</summary>
internal static class StorageMaintenanceRunLogging
{
    public static void LogRunOutcome(
        ILogger logger,
        ulong maintenanceRunId,
        in StorageMaintenanceResult result,
        double durationMs)
    {
        switch (result.Outcome)
        {
            case StorageMaintenanceOutcome.NoWork:
                StorageMaintenanceLogMessages.RunNoWork(logger, maintenanceRunId, durationMs);
                return;

            case StorageMaintenanceOutcome.Skipped:
                StorageMaintenanceLogMessages.RunSkipped(
                    logger,
                    maintenanceRunId,
                    durationMs,
                    result.SegmentId.Value,
                    result.CompactionId,
                    result.SkipReason);
                return;

            case StorageMaintenanceOutcome.Failed:
                StorageMaintenanceLogMessages.RunMaintenanceFailed(
                    logger,
                    maintenanceRunId,
                    result.Outcome.ToString(),
                    durationMs,
                    result.SegmentId.Value,
                    result.CompactionId,
                    result.RelocatedArticleCount,
                    result.SourceSizeBytes,
                    result.SourceDeadBytes,
                    result.SourceDeadRatio,
                    result.ReclaimedSegmentSizeBytes,
                    result.SkipReason);
                return;

            default:
                StorageMaintenanceLogMessages.RunOperationalSummary(
                    logger,
                    maintenanceRunId,
                    result.Outcome.ToString(),
                    durationMs,
                    result.SegmentId.Value,
                    result.CompactionId,
                    result.RelocatedArticleCount,
                    result.CompactionAttempted,
                    result.CompactionCommitted,
                    result.RetirementAttempted,
                    result.Retired,
                    result.ReclamationAttempted,
                    result.Reclaimed,
                    result.SourceSizeBytes,
                    result.SourceLiveBytes,
                    result.SourceDeadBytes,
                    result.SourceDeadRatio,
                    result.ReclaimedSegmentSizeBytes,
                    result.SkipReason);
                return;
        }
    }
}
