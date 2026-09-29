using Microsoft.Extensions.Logging;

namespace VectorNNTP.StorageServer.Storage.Engine.FileJournal;

/// <summary>Source-generated durable article journal diagnostics.</summary>
internal static partial class FileArticleJournalLogMessages
{
    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Information,
        Message = "Article journal opened (path={Path}, bytes={Bytes}, nextSequence={NextSequence}, outstandingBytes={OutstandingBytes}, incomplete={IncompleteCount})")]
    public static partial void Opened(
        ILogger logger,
        string Path,
        long Bytes,
        ulong NextSequence,
        long OutstandingBytes,
        int IncompleteCount);

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Warning,
        Message = "Article journal truncating torn or corrupt tail (path={Path}, validEnd={ValidEnd}, fileLength={FileLength}, reason={Reason})")]
    public static partial void TruncatingTornTail(
        ILogger logger,
        string Path,
        long ValidEnd,
        long FileLength,
        string Reason);

    [LoggerMessage(
        EventId = 3102,
        Level = LogLevel.Error,
        Message = "Article journal corrupt before final boundary (path={Path}, offset={Offset}, reason={Reason})")]
    public static partial void MidFileCorrupt(
        ILogger logger,
        string Path,
        long Offset,
        string Reason);

    [LoggerMessage(
        EventId = 3103,
        Level = LogLevel.Information,
        Message = "Article journal sequence recovered (path={Path}, nextSequence={NextSequence})")]
    public static partial void SequenceRecovered(ILogger logger, string Path, ulong NextSequence);

    [LoggerMessage(
        EventId = 3104,
        Level = LogLevel.Information,
        Message = "Article journal write pressure {Pressure} (outstandingBytes={OutstandingBytes}, soft={SoftLimit}, hard={HardLimit})")]
    public static partial void Pressure(
        ILogger logger,
        StorageWritePressure Pressure,
        long OutstandingBytes,
        long SoftLimit,
        long HardLimit);

    [LoggerMessage(
        EventId = 3105,
        Level = LogLevel.Information,
        Message = "Article journal checkpoint truncated committed records (path={Path}, releasedBytes={ReleasedBytes}, physicalBytes={PhysicalBytes}, nextSequence={NextSequence})")]
    public static partial void Checkpointed(
        ILogger logger,
        string Path,
        long ReleasedBytes,
        long PhysicalBytes,
        ulong NextSequence);

    [LoggerMessage(
        EventId = 3106,
        Level = LogLevel.Information,
        Message = "Article journal closed (path={Path})")]
    public static partial void Closed(ILogger logger, string Path);
}
