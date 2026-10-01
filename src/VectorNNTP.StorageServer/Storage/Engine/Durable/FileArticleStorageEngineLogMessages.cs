using Microsoft.Extensions.Logging;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>Source-generated durable article storage engine diagnostics.</summary>
internal static partial class FileArticleStorageEngineLogMessages
{
    [LoggerMessage(
        EventId = 3400,
        Level = LogLevel.Information,
        Message = "Article storage engine opened (controlDir={ControlDir}, segmentDir={SegmentDir})")]
    public static partial void Opened(ILogger logger, string ControlDir, string SegmentDir);

    [LoggerMessage(
        EventId = 3401,
        Level = LogLevel.Information,
        Message = "Article accepted (artId={ArtId}, sequence={Sequence}, artSize={ArtSize})")]
    public static partial void Accepted(ILogger logger, string ArtId, ulong Sequence, int ArtSize);

    [LoggerMessage(
        EventId = 3415,
        Level = LogLevel.Warning,
        Message = "Article Accept rejected by process-local capacity (artId={ArtId}, RequiredBytes={RequiredBytes}, UsedBytes={UsedBytes}, ArticleReservedBytes={ArticleReservedBytes}, CompactionReservedBytes={CompactionReservedBytes}, CheckpointReservedBytes={CheckpointReservedBytes}, TotalBytes={TotalBytes}, AvailableBytes={AvailableBytes}, MaximumUtilization={MaximumUtilization}, CompactionHeadroom={CompactionHeadroom})")]
    public static partial void RejectedCapacity(
        ILogger logger,
        string ArtId,
        long RequiredBytes,
        long UsedBytes,
        long ArticleReservedBytes,
        long CompactionReservedBytes,
        long CheckpointReservedBytes,
        long TotalBytes,
        long AvailableBytes,
        double MaximumUtilization,
        double CompactionHeadroom);

    [LoggerMessage(
        EventId = 3416,
        Level = LogLevel.Warning,
        Message = "Compaction relocation rejected by process-local capacity (artId={ArtId}, SegmentId={SegmentId}, CompactionId={CompactionId}, RelocationId={RelocationId}, RequiredBytes={RequiredBytes}, UsedBytes={UsedBytes}, ArticleReservedBytes={ArticleReservedBytes}, CompactionReservedBytes={CompactionReservedBytes}, CheckpointReservedBytes={CheckpointReservedBytes}, TotalBytes={TotalBytes}, AvailableBytes={AvailableBytes}, MaximumUtilization={MaximumUtilization}, CompactionHeadroom={CompactionHeadroom})")]
    public static partial void RejectedCompactionCapacity(
        ILogger logger,
        string ArtId,
        ulong SegmentId,
        ulong CompactionId,
        ulong RelocationId,
        long RequiredBytes,
        long UsedBytes,
        long ArticleReservedBytes,
        long CompactionReservedBytes,
        long CheckpointReservedBytes,
        long TotalBytes,
        long AvailableBytes,
        double MaximumUtilization,
        double CompactionHeadroom);

    [LoggerMessage(
        EventId = 3425,
        Level = LogLevel.Warning,
        Message = "Compaction journal frame rejected by process-local capacity (CompactionId={CompactionId}, FrameKind={FrameKind}, RelocationId={RelocationId}, RequiredBytes={RequiredBytes}, UsedBytes={UsedBytes}, ReservedBytes={ReservedBytes}, TotalBytes={TotalBytes}, CeilingUtilization={CeilingUtilization})")]
    public static partial void RejectedCompactionJournalCapacity(
        ILogger logger,
        ulong CompactionId,
        string FrameKind,
        ulong RelocationId,
        long RequiredBytes,
        long UsedBytes,
        long ReservedBytes,
        long TotalBytes,
        double CeilingUtilization);

    [LoggerMessage(
        EventId = 3424,
        Level = LogLevel.Warning,
        Message = "Checkpoint temporary allocation denied by process-local capacity (RequiredBytes={RequiredBytes}, UsedBytes={UsedBytes}, ArticleReservedBytes={ArticleReservedBytes}, CompactionReservedBytes={CompactionReservedBytes}, CheckpointReservedBytes={CheckpointReservedBytes}, TotalBytes={TotalBytes}, AvailableBytes={AvailableBytes}, MaximumUtilization={MaximumUtilization})")]
    public static partial void CheckpointCapacityDenied(
        ILogger logger,
        long RequiredBytes,
        long UsedBytes,
        long ArticleReservedBytes,
        long CompactionReservedBytes,
        long CheckpointReservedBytes,
        long TotalBytes,
        long AvailableBytes,
        double MaximumUtilization);

    [LoggerMessage(
        EventId = 3402,
        Level = LogLevel.Information,
        Message = "Article storage recovery started (incomplete={IncompleteCount})")]
    public static partial void RecoveryStarted(ILogger logger, int IncompleteCount);

    [LoggerMessage(
        EventId = 3403,
        Level = LogLevel.Information,
        Message = "Article storage recovery completed")]
    public static partial void RecoveryCompleted(ILogger logger);

    [LoggerMessage(
        EventId = 3404,
        Level = LogLevel.Information,
        Message = "Recovering Accept-only sequence by fresh SATA append (sequence={Sequence}, artId={ArtId}); prior orphan appends are not discovered")]
    public static partial void RecoverAcceptOnly(ILogger logger, ulong Sequence, string ArtId);

    [LoggerMessage(
        EventId = 3405,
        Level = LogLevel.Information,
        Message = "Recovering sequence from PhysicalWritten (sequence={Sequence}, segmentId={SegmentId}, offset={Offset}, length={Length})")]
    public static partial void RecoverPhysicalWritten(
        ILogger logger,
        ulong Sequence,
        ulong SegmentId,
        long Offset,
        int Length);

    [LoggerMessage(
        EventId = 3412,
        Level = LogLevel.Warning,
        Message = "Startup recovery changed a previously Present article to Invalid because its physical location could not be proved (artId={ArtId}, sequence={Sequence}, segmentId={SegmentId}, offset={Offset}, length={Length}, artHash={ArtHash}, artSize={ArtSize}); the index transition is durable and recovery is continuing")]
    public static partial void StartupPresentInvalidated(
        ILogger logger,
        string ArtId,
        ulong Sequence,
        ulong SegmentId,
        long Offset,
        int Length,
        ulong ArtHash,
        int ArtSize);

    [LoggerMessage(
        EventId = 3406,
        Level = LogLevel.Warning,
        Message = "PhysicalWritten location failed integrity proof (sequence={Sequence}, segmentId={SegmentId}, offset={Offset}); journal forbids supersede — leaving outstanding")]
    public static partial void PhysicalWrittenUnusable(
        ILogger logger,
        ulong Sequence,
        ulong SegmentId,
        long Offset);

    [LoggerMessage(
        EventId = 3407,
        Level = LogLevel.Information,
        Message = "Recovery completed IndexCommitted (sequence={Sequence}, artId={ArtId})")]
    public static partial void RecoveredIndexCommitted(ILogger logger, ulong Sequence, string ArtId);

    [LoggerMessage(
        EventId = 3408,
        Level = LogLevel.Error,
        Message = "Durable persist stage failed (sequence={Sequence}, stage={Stage})")]
    public static partial void PersistStageFailed(ILogger logger, ulong Sequence, string Stage, Exception ex);

    [LoggerMessage(
        EventId = 3417,
        Level = LogLevel.Warning,
        Message = "Durable persist retry scheduled (sequence={Sequence}, Attempt={Attempt}, DelayMs={DelayMs})")]
    public static partial void PersistRetryScheduled(
        ILogger logger,
        ulong Sequence,
        int Attempt,
        double DelayMs);

    [LoggerMessage(
        EventId = 3418,
        Level = LogLevel.Information,
        Message = "Durable persist retry succeeded (sequence={Sequence}, PriorFailures={PriorFailures})")]
    public static partial void PersistRetrySucceeded(ILogger logger, ulong Sequence, int PriorFailures);

    [LoggerMessage(
        EventId = 3419,
        Level = LogLevel.Error,
        Message = "Durable persist failure is not retryable (sequence={Sequence}, ExceptionType={ExceptionType}, Detail={Detail}); incomplete Accept retained; released unwritten segment-copy bytes={ReleasedUnwrittenSegmentBytes} and unbound index bytes={ReleasedUnboundIndexBytes}; written segment-copy bytes={RetainedWrittenSegmentBytes} and journal bytes={RetainedJournalBytes} remain held; blocked retry scheduled")]
    public static partial void PersistNonRetryableFailure(
        ILogger logger,
        ulong Sequence,
        string ExceptionType,
        string Detail,
        long ReleasedUnwrittenSegmentBytes,
        long ReleasedUnboundIndexBytes,
        long RetainedWrittenSegmentBytes,
        long RetainedJournalBytes);

    [LoggerMessage(
        EventId = 3420,
        Level = LogLevel.Warning,
        Message = "Blocked persist retry scheduled (sequence={Sequence}, Attempt={Attempt}, DelayMs={DelayMs})")]
    public static partial void PersistBlockedRetryScheduled(
        ILogger logger,
        ulong Sequence,
        int Attempt,
        double DelayMs);

    [LoggerMessage(
        EventId = 3421,
        Level = LogLevel.Information,
        Message = "Adopted proven physical record for incomplete Accept (sequence={Sequence}, SegmentId={SegmentId}, Offset={Offset}, Length={Length}, ProvenCopies={ProvenCopies})")]
    public static partial void AcceptOrphanAdopted(
        ILogger logger,
        ulong Sequence,
        ulong SegmentId,
        long Offset,
        int Length,
        int ProvenCopies);

    [LoggerMessage(
        EventId = 3423,
        Level = LogLevel.Warning,
        Message = "Closed segment extent accounting incomplete (SegmentId={SegmentId})")]
    public static partial void ClosedExtentAccountingIncomplete(ILogger logger, ulong SegmentId);

    [LoggerMessage(
        EventId = 3422,
        Level = LogLevel.Information,
        Message = "Unreferenced proven segment record marked dead (artId={ArtId}, SegmentId={SegmentId}, Offset={Offset}, Length={Length})")]
    public static partial void UnreferencedRecordMarkedDead(
        ILogger logger,
        string ArtId,
        ulong SegmentId,
        long Offset,
        int Length);

    [LoggerMessage(
        EventId = 3409,
        Level = LogLevel.Information,
        Message = "Journal checkpoint completed (releasedBytes={ReleasedBytes})")]
    public static partial void CheckpointCompleted(ILogger logger, long ReleasedBytes);

    [LoggerMessage(
        EventId = 3410,
        Level = LogLevel.Error,
        Message = "Journal checkpoint failed")]
    public static partial void CheckpointFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 3411,
        Level = LogLevel.Information,
        Message = "Article storage engine closed")]
    public static partial void Closed(ILogger logger);
}
