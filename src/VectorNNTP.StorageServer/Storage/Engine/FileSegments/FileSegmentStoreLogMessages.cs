using Microsoft.Extensions.Logging;

namespace VectorNNTP.StorageServer.Storage.Engine.FileSegments;

/// <summary>Source-generated filesystem segment-store diagnostics.</summary>
internal static partial class FileSegmentStoreLogMessages
{
    [LoggerMessage(
        EventId = 3200,
        Level = LogLevel.Information,
        Message = "Segment store opened (root={Root}, segments={SegmentCount}, active={ActiveSegmentId}, next={NextSegmentId})")]
    public static partial void Opened(
        ILogger logger,
        string Root,
        int SegmentCount,
        ulong? ActiveSegmentId,
        ulong NextSegmentId);

    [LoggerMessage(
        EventId = 3201,
        Level = LogLevel.Information,
        Message = "Segment discovered (segmentId={SegmentId}, kind={Kind}, bytes={Bytes})")]
    public static partial void Discovered(ILogger logger, ulong SegmentId, string Kind, long Bytes);

    [LoggerMessage(
        EventId = 3202,
        Level = LogLevel.Information,
        Message = "Active segment selected (segmentId={SegmentId}, bytes={Bytes})")]
    public static partial void ActiveSelected(ILogger logger, ulong SegmentId, long Bytes);

    [LoggerMessage(
        EventId = 3203,
        Level = LogLevel.Information,
        Message = "Segment rotated (closed={ClosedSegmentId}, active={ActiveSegmentId})")]
    public static partial void Rotated(ILogger logger, ulong ClosedSegmentId, ulong ActiveSegmentId);

    [LoggerMessage(
        EventId = 3204,
        Level = LogLevel.Warning,
        Message = "Active segment truncating torn tail (segmentId={SegmentId}, validEnd={ValidEnd}, fileLength={FileLength}, reason={Reason})")]
    public static partial void TruncatingTornTail(
        ILogger logger,
        ulong SegmentId,
        long ValidEnd,
        long FileLength,
        string Reason);

    [LoggerMessage(
        EventId = 3205,
        Level = LogLevel.Error,
        Message = "Closed segment corrupt (path={Path}, offset={Offset}, reason={Reason})")]
    public static partial void ClosedCorrupt(ILogger logger, string Path, long Offset, string Reason);

    [LoggerMessage(
        EventId = 3206,
        Level = LogLevel.Information,
        Message = "Segment closed (segmentId={SegmentId}, bytes={Bytes})")]
    public static partial void Closed(ILogger logger, ulong SegmentId, long Bytes);

    [LoggerMessage(
        EventId = 3207,
        Level = LogLevel.Information,
        Message = "Segment retired (segmentId={SegmentId})")]
    public static partial void Retired(ILogger logger, ulong SegmentId);

    [LoggerMessage(
        EventId = 3209,
        Level = LogLevel.Information,
        Message = "Segment physically reclaimed (segmentId={SegmentId})")]
    public static partial void Reclaimed(ILogger logger, ulong SegmentId);

    [LoggerMessage(
        EventId = 3208,
        Level = LogLevel.Information,
        Message = "Segment store closed (root={Root})")]
    public static partial void StoreClosed(ILogger logger, string Root);
}
