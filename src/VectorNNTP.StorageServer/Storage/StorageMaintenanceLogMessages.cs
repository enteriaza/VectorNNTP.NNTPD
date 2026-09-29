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
        Message = "Storage maintenance run starting")]
    public static partial void RunStarting(ILogger logger);

    [LoggerMessage(
        EventId = 3013,
        Level = LogLevel.Debug,
        Message = "Storage maintenance run completed with NoWork (DurationMs={DurationMs})")]
    public static partial void RunNoWork(ILogger logger, double DurationMs);

    [LoggerMessage(
        EventId = 3014,
        Level = LogLevel.Information,
        Message = "Storage maintenance run completed (Outcome={Outcome}, SegmentId={SegmentId}, CompactionId={CompactionId}, DurationMs={DurationMs})")]
    public static partial void RunCompleted(
        ILogger logger,
        string Outcome,
        ulong SegmentId,
        ulong CompactionId,
        double DurationMs);

    [LoggerMessage(
        EventId = 3015,
        Level = LogLevel.Error,
        Message = "Storage maintenance run failed (DurationMs={DurationMs}); worker will continue after Interval")]
    public static partial void RunFailed(ILogger logger, double DurationMs, Exception exception);

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
