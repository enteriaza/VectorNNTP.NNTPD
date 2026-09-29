namespace VectorNNTP.StorageServer.Storage;

/// <summary>Source-generated storage-engine hosting lifecycle log messages.</summary>
internal static partial class StorageEngineLogMessages
{
    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Information,
        Message = "Opening FileArticleStorageEngine (ControlDir={ControlDir}, SegmentDir={SegmentDir})")]
    public static partial void Opening(ILogger logger, string ControlDir, string SegmentDir);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Storage engine recovery starting (ControlDir={ControlDir})")]
    public static partial void RecoveryStarting(ILogger logger, string ControlDir);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Information,
        Message = "Storage engine ready (ControlDir={ControlDir}, SegmentDir={SegmentDir})")]
    public static partial void Ready(ILogger logger, string ControlDir, string SegmentDir);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Error,
        Message = "Storage engine startup failed")]
    public static partial void StartupFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Information,
        Message = "Storage engine stopping")]
    public static partial void Stopping(ILogger logger);

    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Information,
        Message = "Storage engine stopped")]
    public static partial void Stopped(ILogger logger);
}
