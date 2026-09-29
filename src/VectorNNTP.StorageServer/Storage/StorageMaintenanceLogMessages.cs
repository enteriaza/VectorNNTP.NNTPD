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
        Message = "Storage maintenance skipped (MaintenanceRunId={MaintenanceRunId}, DurationMs={DurationMs}, SegmentId={SegmentId}, CompactionId={CompactionId}, SkipReason={SkipReason})")]
    public static partial void RunSkipped(
        ILogger logger,
        ulong MaintenanceRunId,
        double DurationMs,
        ulong SegmentId,
        ulong CompactionId,
        string? SkipReason);

    [LoggerMessage(
        EventId = 3019,
        Level = LogLevel.Information,
        Message = "Storage maintenance summary (Outcome={Outcome}, MaintenanceRunId={MaintenanceRunId}, DurationMs={DurationMs}, SegmentId={SegmentId}, CompactionId={CompactionId}, RelocatedArticles={RelocatedArticles}, CompactionAttempted={CompactionAttempted}, CompactionCommitted={CompactionCommitted}, RetirementAttempted={RetirementAttempted}, Retired={Retired}, ReclamationAttempted={ReclamationAttempted}, Reclaimed={Reclaimed}, SourceSizeBytes={SourceSizeBytes}, SourceLiveBytes={SourceLiveBytes}, SourceDeadBytes={SourceDeadBytes}, SourceDeadRatio={SourceDeadRatio}, ReclaimedSegmentSizeBytes={ReclaimedSegmentSizeBytes}, Detail={Detail})")]
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
}
