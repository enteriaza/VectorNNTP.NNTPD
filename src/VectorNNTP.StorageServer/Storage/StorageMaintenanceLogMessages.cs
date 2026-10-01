namespace VectorNNTP.StorageServer.Storage;

/// <summary>Source-generated storage-maintenance worker log messages.</summary>
internal static partial class StorageMaintenanceLogMessages
{
    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Information,
        Message = "Storage maintenance worker started (Interval={Interval})")]
    public static partial void Started(ILogger logger, TimeSpan Interval);

    [LoggerMessage(
        EventId = 3011,
        Level = LogLevel.Information,
        Message = "Storage maintenance automation is disabled (MaintenanceEnabled=false)")]
    public static partial void Disabled(ILogger logger);

    [LoggerMessage(
        EventId = 3012,
        Level = LogLevel.Debug,
        Message = "Storage maintenance run starting (MaintenanceRunId={MaintenanceRunId})")]
    public static partial void RunStarting(ILogger logger, ulong MaintenanceRunId);

    [LoggerMessage(
        EventId = 3013,
        Level = LogLevel.Debug,
        Message = "Storage maintenance idle (Outcome=NoWork, MaintenanceRunId={MaintenanceRunId}, DurationMs={DurationMs})")]
    public static partial void RunNoWork(ILogger logger, ulong MaintenanceRunId, double DurationMs);

    [LoggerMessage(
        EventId = 3018,
        Level = LogLevel.Debug,
        Message = "Storage maintenance skipped (MaintenanceRunId={MaintenanceRunId}, DurationMs={DurationMs}, SegmentId={SegmentId}, CompactionId={CompactionId}, SkipReason={SkipReason}, AdmissionPressure={AdmissionPressure}, AdmissionRecoveryTargetBytes={AdmissionRecoveryTargetBytes}, CapacityUsedBytes={CapacityUsedBytes}, CapacityArticleReservedBytes={CapacityArticleReservedBytes}, CapacityCompactionReservedBytes={CapacityCompactionReservedBytes})")]
    public static partial void RunSkipped(
        ILogger logger,
        ulong MaintenanceRunId,
        double DurationMs,
        ulong SegmentId,
        ulong CompactionId,
        string? SkipReason,
        bool? AdmissionPressure,
        long? AdmissionRecoveryTargetBytes,
        long? CapacityUsedBytes,
        long? CapacityArticleReservedBytes,
        long? CapacityCompactionReservedBytes);

    [LoggerMessage(
        EventId = 3019,
        Level = LogLevel.Information,
        Message = "Storage maintenance summary (Outcome={Outcome}, MaintenanceRunId={MaintenanceRunId}, DurationMs={DurationMs}, SegmentId={SegmentId}, CompactionId={CompactionId}, RelocatedArticles={RelocatedArticles}, CompactionAttempted={CompactionAttempted}, CompactionCommitted={CompactionCommitted}, RetirementAttempted={RetirementAttempted}, Retired={Retired}, ReclamationAttempted={ReclamationAttempted}, Reclaimed={Reclaimed}, SourceSizeBytes={SourceSizeBytes}, SourceLiveBytes={SourceLiveBytes}, SourceDeadBytes={SourceDeadBytes}, SourceDeadRatio={SourceDeadRatio}, ReclaimedSegmentSizeBytes={ReclaimedSegmentSizeBytes}, AdmissionPressure={AdmissionPressure}, AdmissionRecoveryTargetBytes={AdmissionRecoveryTargetBytes}, CapacityUsedBytes={CapacityUsedBytes}, CapacityTotalBytes={CapacityTotalBytes}, CapacityArticleReservedBytes={CapacityArticleReservedBytes}, CapacityCompactionReservedBytes={CapacityCompactionReservedBytes}, Detail={Detail})")]
    public static partial void RunOperationalSummary(
        ILogger logger,
        ulong MaintenanceRunId,
        string Outcome,
        double DurationMs,
        ulong SegmentId,
        ulong CompactionId,
        int RelocatedArticles,
        bool CompactionAttempted,
        bool CompactionCommitted,
        bool RetirementAttempted,
        bool Retired,
        bool ReclamationAttempted,
        bool Reclaimed,
        long? SourceSizeBytes,
        long? SourceLiveBytes,
        long? SourceDeadBytes,
        double? SourceDeadRatio,
        long? ReclaimedSegmentSizeBytes,
        bool? AdmissionPressure,
        long? AdmissionRecoveryTargetBytes,
        long? CapacityUsedBytes,
        long? CapacityTotalBytes,
        long? CapacityArticleReservedBytes,
        long? CapacityCompactionReservedBytes,
        string? Detail);

    [LoggerMessage(
        EventId = 3020,
        Level = LogLevel.Warning,
        Message = "Storage maintenance failed (Outcome={Outcome}, MaintenanceRunId={MaintenanceRunId}, DurationMs={DurationMs}, SegmentId={SegmentId}, CompactionId={CompactionId}, RelocatedArticles={RelocatedArticles}, SourceSizeBytes={SourceSizeBytes}, SourceDeadBytes={SourceDeadBytes}, SourceDeadRatio={SourceDeadRatio}, ReclaimedSegmentSizeBytes={ReclaimedSegmentSizeBytes}, Detail={Detail})")]
    public static partial void RunMaintenanceFailed(
        ILogger logger,
        ulong MaintenanceRunId,
        string Outcome,
        double DurationMs,
        ulong SegmentId,
        ulong CompactionId,
        int RelocatedArticles,
        long? SourceSizeBytes,
        long? SourceDeadBytes,
        double? SourceDeadRatio,
        long? ReclaimedSegmentSizeBytes,
        string? Detail);

    [LoggerMessage(
        EventId = 3015,
        Level = LogLevel.Error,
        Message = "Storage maintenance worker fault (MaintenanceRunId={MaintenanceRunId}, DurationMs={DurationMs}); worker will continue after Interval")]
    public static partial void RunFailed(
        ILogger logger,
        ulong MaintenanceRunId,
        double DurationMs,
        Exception exception);

    [LoggerMessage(
        EventId = 3016,
        Level = LogLevel.Information,
        Message = "Storage maintenance worker stopping")]
    public static partial void Stopping(ILogger logger);

    [LoggerMessage(
        EventId = 3017,
        Level = LogLevel.Information,
        Message = "Storage maintenance worker stopped")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(
        EventId = 3021,
        Level = LogLevel.Information,
        Message = "Journal checkpoint attempted (MaintenanceRunId={MaintenanceRunId}, JournalPhysicalBytes={JournalPhysicalBytes}, ThresholdBytes={ThresholdBytes})")]
    public static partial void JournalCheckpointAttempted(
        ILogger logger,
        ulong MaintenanceRunId,
        long JournalPhysicalBytes,
        long ThresholdBytes);

    [LoggerMessage(
        EventId = 3022,
        Level = LogLevel.Information,
        Message = "Journal checkpoint omitted committed journal bytes (MaintenanceRunId={MaintenanceRunId}, ReleasedBytes={ReleasedBytes}, DurationMs={DurationMs})")]
    public static partial void JournalCheckpointOmitted(
        ILogger logger,
        ulong MaintenanceRunId,
        long ReleasedBytes,
        double DurationMs);

    [LoggerMessage(
        EventId = 3023,
        Level = LogLevel.Debug,
        Message = "Journal checkpoint had nothing to omit (MaintenanceRunId={MaintenanceRunId}, DurationMs={DurationMs})")]
    public static partial void JournalCheckpointNothingToOmit(
        ILogger logger,
        ulong MaintenanceRunId,
        double DurationMs);

    [LoggerMessage(
        EventId = 3024,
        Level = LogLevel.Warning,
        Message = "Journal checkpoint deferred; unresolved durable tail (MaintenanceRunId={MaintenanceRunId})")]
    public static partial void JournalCheckpointDeferred(
        ILogger logger,
        ulong MaintenanceRunId,
        Exception exception);
}
