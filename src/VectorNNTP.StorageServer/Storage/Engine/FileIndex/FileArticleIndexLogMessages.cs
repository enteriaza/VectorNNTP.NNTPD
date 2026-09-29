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
}
