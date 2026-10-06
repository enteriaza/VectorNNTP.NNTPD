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
        Message = "Storage maintenance summary (Outcome={Outcome}, MaintenanceRunId={MaintenanceRunId}, DurationMs={DurationMs}, SegmentId={SegmentId}, CompactionId={CompactionId}, RelocatedArticles={RelocatedArticles}, CompactionAttempted={CompactionAttempted}, CompactionCommitted={CompactionCommitted}, RetirementAttempted={RetirementAttempted}, Retired={Retired}, ReclamationAttempted={ReclamationAttempted}, Reclaimed={Reclaimed}, SourceSizeBytes={SourceSizeBytes}, SourceLiveBytes={SourceLiveBytes}, SourceDeadBytes={SourceDeadBytes}, SourceDeadRatio={SourceDeadRatio}, ReclaimedSegmentSizeBytes={ReclaimedSegmentSizeBytes}, DestinationSegmentId={DestinationSegmentId}, AdmissionPressure={AdmissionPressure}, AdmissionRecoveryTargetBytes={AdmissionRecoveryTargetBytes}, CapacityUsedBytes={CapacityUsedBytes}, CapacityTotalBytes={CapacityTotalBytes}, CapacityArticleReservedBytes={CapacityArticleReservedBytes}, CapacityCompactionReservedBytes={CapacityCompactionReservedBytes}, Detail={Detail})")]
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
        ulong DestinationSegmentId,
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

    /// <summary>
    /// Closed segments were examined for rewrite and none met the configured dead-byte floors.
    /// </summary>
    [LoggerMessage(
        EventId = 3030,
        Level = LogLevel.Debug,
        Message = "Low-density rewrite not selected (ClosedSegments={ClosedSegments}, SkippedForDensity={SkippedForDensity}, SkippedForDeadBytes={SkippedForDeadBytes}, MinimumDeadRatio={MinimumDeadRatio}, MinimumDeadBytes={MinimumDeadBytes})")]
    public static partial void RewriteNotSelected(
        ILogger logger,
        int ClosedSegments,
        int SkippedForDensity,
        int SkippedForDeadBytes,
        int MinimumDeadRatio,
        long MinimumDeadBytes);

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

    [LoggerMessage(
        EventId = 3029,
        Level = LogLevel.Warning,
        Message = "Journal checkpoint deferred; compaction state changed during image construction (MaintenanceRunId={MaintenanceRunId})")]
    public static partial void JournalCheckpointDeferredCompactionChanged(
        ILogger logger,
        ulong MaintenanceRunId,
        Exception exception);

    [LoggerMessage(
        EventId = 3025,
        Level = LogLevel.Information,
        Message = "Index checkpoint attempted (MaintenanceRunId={MaintenanceRunId}, IndexPhysicalBytes={IndexPhysicalBytes}, ThresholdBytes={ThresholdBytes})")]
    public static partial void IndexCheckpointAttempted(
        ILogger logger,
        ulong MaintenanceRunId,
        long IndexPhysicalBytes,
        long ThresholdBytes);

    [LoggerMessage(
        EventId = 3026,
        Level = LogLevel.Information,
        Message = "Index checkpoint retired physical index history (MaintenanceRunId={MaintenanceRunId}, IndexPhysicalBytes={IndexPhysicalBytes}, ThresholdBytes={ThresholdBytes}, RetiredBytes={RetiredBytes}, DurationMs={DurationMs})")]
    public static partial void IndexCheckpointRetired(
        ILogger logger,
        ulong MaintenanceRunId,
        long IndexPhysicalBytes,
        long ThresholdBytes,
        long RetiredBytes,
        double DurationMs);

    [LoggerMessage(
        EventId = 3027,
        Level = LogLevel.Debug,
        Message = "Index checkpoint had nothing to retire (MaintenanceRunId={MaintenanceRunId}, IndexPhysicalBytes={IndexPhysicalBytes}, ThresholdBytes={ThresholdBytes}, DurationMs={DurationMs})")]
    public static partial void IndexCheckpointNothingToRetire(
        ILogger logger,
        ulong MaintenanceRunId,
        long IndexPhysicalBytes,
        long ThresholdBytes,
        double DurationMs);

    [LoggerMessage(
        EventId = 3028,
        Level = LogLevel.Warning,
        Message = "Index checkpoint failed (MaintenanceRunId={MaintenanceRunId}, IndexPhysicalBytes={IndexPhysicalBytes}, ThresholdBytes={ThresholdBytes})")]
    public static partial void IndexCheckpointFailed(
        ILogger logger,
        ulong MaintenanceRunId,
        long IndexPhysicalBytes,
        long ThresholdBytes,
        Exception exception);

    /// <summary>Debug measurement logged only when the bulk watermark class changes.</summary>
    [LoggerMessage(
        EventId = 3031,
        Level = LogLevel.Debug,
        Message = "Bulk retention pressure measured (State={State}, TotalBytes={TotalBytes}, FreeBytes={FreeBytes}, UsedBytes={UsedBytes}, UsedPercent={UsedPercent}, AvailableReserveBytes={AvailableReserveBytes}, MaintenanceMode={MaintenanceMode})")]
    public static partial void BulkPressureMeasured(
        ILogger logger,
        string State,
        long TotalBytes,
        long FreeBytes,
        long UsedBytes,
        int UsedPercent,
        long AvailableReserveBytes,
        string MaintenanceMode);

    /// <summary>Information log for a bulk watermark transition, including the first observation.</summary>
    [LoggerMessage(
        EventId = 3032,
        Level = LogLevel.Information,
        Message = "Bulk retention pressure state changed (PreviousState={PreviousState}, State={State}, UsedPercent={UsedPercent}, MaintenanceMode={MaintenanceMode})")]
    public static partial void BulkPressureStateChanged(
        ILogger logger,
        string PreviousState,
        string State,
        int UsedPercent,
        string MaintenanceMode);

    /// <summary>Actionable log when the cache volume is Warning, Pressure, or High.</summary>
    [LoggerMessage(
        EventId = 3033,
        Level = LogLevel.Warning,
        Message = "Bulk retention pressure requires attention (State={State}, UsedPercent={UsedPercent}, FreeBytes={FreeBytes}, AvailableReserveBytes={AvailableReserveBytes}, MaintenanceMode={MaintenanceMode})")]
    public static partial void BulkPressureAttention(
        ILogger logger,
        string State,
        int UsedPercent,
        long FreeBytes,
        long AvailableReserveBytes,
        string MaintenanceMode);

    /// <summary>
    /// Actionable log when the cache volume is Critical or Emergency.
    /// Classification does not delete an acknowledged article.
    /// </summary>
    [LoggerMessage(
        EventId = 3034,
        Level = LogLevel.Error,
        Message = "Bulk retention pressure is critical (State={State}, UsedPercent={UsedPercent}, FreeBytes={FreeBytes}, AvailableReserveBytes={AvailableReserveBytes}, EmergencyAdmissionProtectionRequired={EmergencyAdmissionProtectionRequired}, MaintenanceMode={MaintenanceMode})")]
    public static partial void BulkPressureCritical(
        ILogger logger,
        string State,
        int UsedPercent,
        long FreeBytes,
        long AvailableReserveBytes,
        bool EmergencyAdmissionProtectionRequired,
        string MaintenanceMode);

    /// <summary>Debug log when a new rewrite is withheld. Idle cycles that select nothing do not emit this.</summary>
    [LoggerMessage(
        EventId = 3035,
        Level = LogLevel.Debug,
        Message = "Bulk retention pressure withheld a new rewrite (State={State}, SegmentId={SegmentId}, LiveBytes={LiveBytes}, DeadBytes={DeadBytes}, AvailableReserveBytes={AvailableReserveBytes})")]
    public static partial void BulkRewriteWithheld(
        ILogger logger,
        string State,
        ulong SegmentId,
        long LiveBytes,
        long DeadBytes,
        long AvailableReserveBytes);
}
