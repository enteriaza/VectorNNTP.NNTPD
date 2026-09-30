using Microsoft.Extensions.Logging;

namespace VectorNNTP.StorageServer.Storage.Engine.FileIndex;

/// <summary>Source-generated durable article-index diagnostics.</summary>
internal static partial class FileArticleIndexLogMessages
{
    [LoggerMessage(
        EventId = 3300,
        Level = LogLevel.Information,
        Message = "Article index opened (path={Path}, bytes={Bytes}, entries={EntryCount})")]
    public static partial void Opened(ILogger logger, string Path, long Bytes, int EntryCount);

    [LoggerMessage(
        EventId = 3301,
        Level = LogLevel.Warning,
        Message = "Article index truncating torn or corrupt tail (path={Path}, validEnd={ValidEnd}, fileLength={FileLength}, reason={Reason})")]
    public static partial void TruncatingTornTail(
        ILogger logger,
        string Path,
        long ValidEnd,
        long FileLength,
        string Reason);

    [LoggerMessage(
        EventId = 3302,
        Level = LogLevel.Error,
        Message = "Article index corrupt before final boundary (path={Path}, offset={Offset}, reason={Reason})")]
    public static partial void MidFileCorrupt(ILogger logger, string Path, long Offset, string Reason);

    [LoggerMessage(
        EventId = 3303,
        Level = LogLevel.Information,
        Message = "Article index closed (path={Path})")]
    public static partial void Closed(ILogger logger, string Path);

    [LoggerMessage(
        EventId = 3304,
        Level = LogLevel.Information,
        Message = "Article index snapshot installed (path={Path}, generation={Generation}, records={RecordCount}, coveredIndexLength={CoveredIndexLength})")]
    public static partial void SnapshotInstalled(
        ILogger logger,
        string Path,
        ulong Generation,
        int RecordCount,
        long CoveredIndexLength);

    [LoggerMessage(
        EventId = 3305,
        Level = LogLevel.Error,
        Message = "Article index snapshot failed (path={Path})")]
    public static partial void SnapshotFailed(ILogger logger, Exception exception, string Path);

    [LoggerMessage(
        EventId = 3306,
        Level = LogLevel.Information,
        Message = "Article index startup using snapshot (path={Path}, generation={Generation}, coveredIndexLength={CoveredIndexLength}, indexLength={IndexLength})")]
    public static partial void SnapshotReplay(
        ILogger logger,
        string Path,
        ulong Generation,
        long CoveredIndexLength,
        long IndexLength);

    [LoggerMessage(
        EventId = 3307,
        Level = LogLevel.Information,
        Message = "Article index checkpoint installed (path={Path}, generation={Generation}, retiredPrefixLength={RetiredPrefixLength}, deltaBytes={DeltaBytes})")]
    public static partial void CheckpointInstalled(
        ILogger logger,
        string Path,
        ulong Generation,
        long RetiredPrefixLength,
        long DeltaBytes);

    [LoggerMessage(
        EventId = 3308,
        Level = LogLevel.Error,
        Message = "Article index checkpoint failed (path={Path})")]
    public static partial void CheckpointFailed(ILogger logger, Exception exception, string Path);

    [LoggerMessage(
        EventId = 3309,
        Level = LogLevel.Information,
        Message = "Article index startup replaying replacement delta (path={Path}, generation={Generation}, deltaStart={DeltaStart}, indexLength={IndexLength})")]
    public static partial void ReplacementReplay(
        ILogger logger,
        string Path,
        ulong Generation,
        long DeltaStart,
        long IndexLength);
}
