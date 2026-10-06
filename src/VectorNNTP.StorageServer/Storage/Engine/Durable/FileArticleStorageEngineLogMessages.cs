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
        int MaximumUtilization,
        int CompactionHeadroom);

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
        int MaximumUtilization,
        int CompactionHeadroom);

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
        int MaximumUtilization);

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

    [LoggerMessage(
        EventId = 3428,
        Level = LogLevel.Debug,
        Message = "Retention batch idle (Visited={Visited}, Evaluated={Evaluated}, NotEligible={NotEligible}, StateChanged={StateChanged}, BatchSize={BatchSize}, DurationMs={DurationMs}, Wrapped={Wrapped})")]
    public static partial void RetentionBatchIdle(
        ILogger logger,
        int Visited,
        int Evaluated,
        int NotEligible,
        int StateChanged,
        int BatchSize,
        double DurationMs,
        bool Wrapped);

    [LoggerMessage(
        EventId = 3426,
        Level = LogLevel.Information,
        Message = "Retention batch completed (Visited={Visited}, Evaluated={Evaluated}, Expired={Expired}, NotEligible={NotEligible}, StateChanged={StateChanged}, BatchSize={BatchSize}, DurationMs={DurationMs}, Wrapped={Wrapped})")]
    public static partial void RetentionBatchCompleted(
        ILogger logger,
        int Visited,
        int Evaluated,
        int Expired,
        int NotEligible,
        int StateChanged,
        int BatchSize,
        double DurationMs,
        bool Wrapped);

    [LoggerMessage(
        EventId = 3427,
        Level = LogLevel.Error,
        Message = "Retention expiration failed (artId={ArtId})")]
    public static partial void RetentionExpirationFailed(ILogger logger, Exception ex, string ArtId);

    [LoggerMessage(
        EventId = 3429,
        Level = LogLevel.Information,
        Message = "Fully-dead segment reclaimed (segmentId={SegmentId}, bytes={Bytes}, reason={Reason})")]
    public static partial void FullyDeadSegmentReclaimed(
        ILogger logger,
        ulong SegmentId,
        long Bytes,
        string Reason);

    [LoggerMessage(
        EventId = 3430,
        Level = LogLevel.Error,
        Message = "Fully-dead segment deletion failed (segmentId={SegmentId}, reason={Reason})")]
    public static partial void FullyDeadSegmentReclamationFailed(
        ILogger logger,
        ulong SegmentId,
        string Reason);

    /// <summary>Debug log when Accept observes a routine cache-volume class change.</summary>
    [LoggerMessage(
        EventId = 3431,
        Level = LogLevel.Debug,
        Message = "Bulk admission pressure state changed (PreviousState={PreviousState}, State={State}, UsedPercent={UsedPercent}, FreeBytes={FreeBytes}, RecoveryReserveBytes={RecoveryReserveBytes})")]
    public static partial void BulkAdmissionStateChanged(
        ILogger logger,
        string PreviousState,
        string State,
        int UsedPercent,
        long FreeBytes,
        long RecoveryReserveBytes);

    /// <summary>Warning when Accept observes High or Critical cache-volume pressure.</summary>
    [LoggerMessage(
        EventId = 3436,
        Level = LogLevel.Warning,
        Message = "Bulk admission pressure elevated (PreviousState={PreviousState}, State={State}, UsedPercent={UsedPercent}, FreeBytes={FreeBytes}, RecoveryReserveBytes={RecoveryReserveBytes})")]
    public static partial void BulkAdmissionElevated(
        ILogger logger,
        string PreviousState,
        string State,
        int UsedPercent,
        long FreeBytes,
        long RecoveryReserveBytes);

    /// <summary>Error when Accept observes Emergency cache-volume pressure.</summary>
    [LoggerMessage(
        EventId = 3437,
        Level = LogLevel.Error,
        Message = "Bulk admission pressure is emergency (PreviousState={PreviousState}, State={State}, UsedPercent={UsedPercent}, FreeBytes={FreeBytes}, RecoveryReserveBytes={RecoveryReserveBytes})")]
    public static partial void BulkAdmissionEmergency(
        ILogger logger,
        string PreviousState,
        string State,
        int UsedPercent,
        long FreeBytes,
        long RecoveryReserveBytes);

    /// <summary>Warning when High or Critical admission would consume the recovery reserve.</summary>
    [LoggerMessage(
        EventId = 3432,
        Level = LogLevel.Warning,
        Message = "Article Accept rejected by bulk recovery reserve (artId={ArtId}, ArtSize={ArtSize}, State={State}, UsedBytes={UsedBytes}, FreeBytes={FreeBytes}, RecoveryReserveBytes={RecoveryReserveBytes}, ProtectedHeadroomBytes={ProtectedHeadroomBytes}, RecoveryAttempted={RecoveryAttempted}, RecoveryReclaimedSpace={RecoveryReclaimedSpace}, Reason={Reason})")]
    public static partial void RejectedBulkHeadroom(
        ILogger logger,
        string ArtId,
        int ArtSize,
        string State,
        long UsedBytes,
        long FreeBytes,
        long RecoveryReserveBytes,
        long ProtectedHeadroomBytes,
        bool RecoveryAttempted,
        bool RecoveryReclaimedSpace,
        string Reason);

    /// <summary>Debug counts for one pressure-expiration window. Idle cycles that examine nothing do not emit this.</summary>
    [LoggerMessage(
        EventId = 3438,
        Level = LogLevel.Debug,
        Message = "Pressure expiration batch (State={State}, Visited={Visited}, Evaluated={Evaluated}, TooYoung={TooYoung}, MissingArrival={MissingArrival}, FutureArrival={FutureArrival}, Selected={Selected}, Expired={Expired}, BytesLogicallyExpired={BytesLogicallyExpired}, DurationMs={DurationMs}, Wrapped={Wrapped})")]
    public static partial void PressureExpirationBatch(
        ILogger logger,
        string State,
        int Visited,
        int Evaluated,
        int TooYoung,
        int MissingArrival,
        int FutureArrival,
        int Selected,
        int Expired,
        long BytesLogicallyExpired,
        double DurationMs,
        bool Wrapped);

    /// <summary>Error when Emergency or an unmeasured volume rejects a new Accept.</summary>
    [LoggerMessage(
        EventId = 3433,
        Level = LogLevel.Error,
        Message = "Article Accept rejected by bulk emergency pressure (artId={ArtId}, ArtSize={ArtSize}, State={State}, UsedBytes={UsedBytes}, FreeBytes={FreeBytes}, RecoveryReserveBytes={RecoveryReserveBytes}, ProtectedHeadroomBytes={ProtectedHeadroomBytes}, RecoveryAttempted={RecoveryAttempted}, RecoveryReclaimedSpace={RecoveryReclaimedSpace}, Reason={Reason})")]
    public static partial void RejectedBulkEmergency(
        ILogger logger,
        string ArtId,
        int ArtSize,
        string State,
        long UsedBytes,
        long FreeBytes,
        long RecoveryReserveBytes,
        long ProtectedHeadroomBytes,
        bool RecoveryAttempted,
        bool RecoveryReclaimedSpace,
        string Reason);

    /// <summary>Error when the cache volume cannot be measured. Admission fails closed.</summary>
    [LoggerMessage(
        EventId = 3434,
        Level = LogLevel.Error,
        Message = "Cache volume capacity could not be measured; new Accept is rejected")]
    public static partial void BulkCapacityUnmeasured(ILogger logger, Exception exception);

    /// <summary>Warning when admission recovery throws before the journal ACK.</summary>
    [LoggerMessage(
        EventId = 3435,
        Level = LogLevel.Warning,
        Message = "Bulk admission recovery failed; new Accept is rejected (artId={ArtId}, ArtSize={ArtSize})")]
    public static partial void BulkAdmissionRecoveryFailed(
        ILogger logger,
        string ArtId,
        int ArtSize,
        Exception exception);
}
