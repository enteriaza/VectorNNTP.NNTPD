using System.Diagnostics;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

public sealed partial class FileArticleStorageEngine
{
    /// <summary>
    /// Persists the sequences already taken from the pending queue.
    /// Accept frames are already durable. Segment, PhysicalWritten, Present, and
    /// IndexCommitted each flush once for the sequences that reach that stage.
    /// </summary>
    private async Task PersistBatchAsync(List<ulong> sequences, CancellationToken cancellationToken)
    {
        var incompleteBySequence = new Dictionary<ulong, JournalIncompleteSequence>();
        foreach (var incomplete in _journal.EnumerateIncomplete())
        {
            incompleteBySequence[incomplete.Accept.Sequence] = incomplete;
        }

        var work = new List<PersistBatchItem>(sequences.Count);
        foreach (var sequence in sequences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!incompleteBySequence.TryGetValue(sequence, out var incomplete))
            {
                ClearPersistRetryAttempts(sequence);
                continue;
            }

            var attemptsBefore = ReadPersistRetryAttempts(sequence);

            work.Add(new PersistBatchItem(incomplete, attemptsBefore));
        }

        if (work.Count == 0)
        {
            return;
        }

        var bytes = 0L;
        foreach (var item in work)
        {
            bytes += item.Accept.ArtSize;
        }

        Volatile.Write(ref _lastPersistBatchArticleCount, work.Count);
        Volatile.Write(ref _lastPersistBatchByteCount, bytes);
        _ = Interlocked.Increment(ref _persistBatchCount);

        var segmentPhase = PhysicalProofProbe.MarkEnabled();
        await PersistBatchSegmentsAsync(work, cancellationToken).ConfigureAwait(false);
        PhysicalProofProbe.AddPhase(PhysicalProofProbe.StageSegment, segmentPhase, work.Count);
        PhysicalProofProbe.NoteStage(PhysicalProofProbe.StageSegment);
        cancellationToken.ThrowIfCancellationRequested();
        var physicalWrittenPhase = PhysicalProofProbe.MarkEnabled();
        PersistBatchPhysicalWritten(work);
        PhysicalProofProbe.AddPhase(PhysicalProofProbe.StagePhysicalWritten, physicalWrittenPhase, work.Count);
        PhysicalProofProbe.NoteStage(PhysicalProofProbe.StagePhysicalWritten);
        cancellationToken.ThrowIfCancellationRequested();
        var presentPhase = PhysicalProofProbe.MarkEnabled();
        PersistBatchPresent(work);
        PhysicalProofProbe.AddPhase(PhysicalProofProbe.StagePresentBegin, presentPhase, work.Count);
        cancellationToken.ThrowIfCancellationRequested();
        var indexCommittedPhase = PhysicalProofProbe.MarkEnabled();
        PersistBatchIndexCommitted(work);
        PhysicalProofProbe.AddPhase(PhysicalProofProbe.StageIndexCommitted, indexCommittedPhase, work.Count);
        PhysicalProofProbe.NoteStage(PhysicalProofProbe.StageIndexCommitted);
    }

    private async Task PersistBatchSegmentsAsync(List<PersistBatchItem> work, CancellationToken cancellationToken)
    {
        var appending = new List<PersistBatchItem>();
        foreach (var item in work)
        {
            if (item.Failed)
            {
                continue;
            }

            if (item.Incomplete.PhysicalWritten is { } existing)
            {
                item.Location = existing.Location;
                item.PhysicalWrittenDurable = true;
                item.SegmentReady = true;
                continue;
            }

            try
            {
                ThrowIfTestFault(PersistFaultPoint.BeforeSataAppend, item.Sequence);
                var accept = item.Accept;
                IReadOnlyList<StoredArticleLocation> proven;
                if (CanSkipProvenLocationScan(accept))
                {
                    PhysicalProofProbe.NoteScanSkipped();
                    proven = [];
                }
                else
                {
                    PhysicalProofProbe.NoteScanPerformed();
                    proven = _segments.FindProvenLocations(
                        accept.ArtId,
                        accept.ArtHash,
                        accept.ArtSize,
                        accept.ArtData.Span);
                }
                item.Proven = proven;
                if (proven.Count > 0
                    && TryChooseProvenLocation(accept, proven, out var chosen)
                    && TryRegisterPrePhysicalWritten(item.Sequence, chosen))
                {
                    item.Location = chosen;
                    item.SegmentReady = true;
                    NoteSegmentCopyWrittenIfUnwritten(item.Sequence, chosen.SegmentId);
                    continue;
                }

                ClearPrePhysicalWritten(item.Sequence);
                _ = ReserveSegmentCopyBeforeAppend(accept);
                appending.Add(item);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ClearPrePhysicalWritten(item.Sequence);
                FailPersistBatchItem(item, ex);
            }
        }

        if (appending.Count > 0)
        {
            foreach (var item in appending)
            {
                RemoveAcceptWithoutPhysicalBytes(item.Sequence);
            }

            FlushedSegmentAppend[] flushed;
            try
            {
                var payloads = new ReadOnlyMemory<byte>[appending.Count];
                for (var i = 0; i < appending.Count; i++)
                {
                    payloads[i] = appending[i].Accept.ArtData;
                }

                flushed = _segments.AppendActiveBatch(payloads);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var item in appending)
                {
                    ClearPrePhysicalWritten(item.Sequence);
                    FailPersistBatchItem(item, ex);
                }

                FlushUnflushedSegmentOrFail(work);
                return;
            }

            for (var i = 0; i < appending.Count; i++)
            {
                var item = appending[i];
                try
                {
                    var receipt = flushed[i];
                    var location = receipt.Location;
                    NoteSegmentCopyWritten(item.Sequence, location.SegmentId);
                    _ = Interlocked.Increment(ref _physicalAppendCount);
                    ThrowIfTestFault(PersistFaultPoint.AfterSataAppend, item.Sequence);
                    location = ApplyPhysicalLocationTestHooks(item.Sequence, location);
                    item.WritePathVerified = location == receipt.Location
                        && receipt.ArtId == item.Accept.ArtId
                        && receipt.ArtHash == item.Accept.ArtHash
                        && receipt.ArtSize == item.Accept.ArtSize;
                    if (!TryRegisterPrePhysicalWritten(item.Sequence, location))
                    {
                        ClearPrePhysicalWritten(item.Sequence);
                        item.WritePathVerified = false;
                        location = await AppendAcceptLocationForPhysicalWrittenAsync(item.Accept, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var afterSataHook = TestHookAfterSataBeforePhysicalWritten;
                    TestHookAfterSataBeforePhysicalWritten = null;
                    afterSataHook?.Invoke(item.Sequence, location);
                    item.Location = location;
                    item.SegmentReady = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ClearPrePhysicalWritten(item.Sequence);
                    FailPersistBatchItem(item, ex);
                }
            }
        }

        FlushUnflushedSegmentOrFail(work);
    }

    private void FlushUnflushedSegmentOrFail(List<PersistBatchItem> work)
    {
        if (!_segments.HasUnflushedCommittedAppend)
        {
            return;
        }

        try
        {
            _segments.FlushActiveDurable();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var item in work)
            {
                if (!item.Failed && !item.PhysicalWrittenDurable)
                {
                    FailPersistBatchItem(item, ex);
                }
            }
        }
    }

    private StoredArticleLocation ApplyPhysicalLocationTestHooks(ulong sequence, StoredArticleLocation location)
    {
        _ = sequence;
        var rewrite = TestRewritePhysicalLocationAfterAppend;
        TestRewritePhysicalLocationAfterAppend = null;
        if (rewrite is not null)
        {
            location = rewrite(location);
        }

        var sealRewrite = TestRewriteSealedPhysicalLocation;
        if (sealRewrite is not null)
        {
            var rewritten = sealRewrite(location);
            if (rewritten is { } next)
            {
                location = next;
            }
            else
            {
                TestRewriteSealedPhysicalLocation = null;
            }
        }

        return location;
    }

    private void PersistBatchPhysicalWritten(List<PersistBatchItem> work)
    {
        var ready = new List<PersistBatchItem>();
        foreach (var item in work)
        {
            if (item.Failed || item.PhysicalWrittenDurable || !item.SegmentReady)
            {
                continue;
            }

            try
            {
                ThrowIfTestFault(PersistFaultPoint.BeforePhysicalWritten, item.Sequence);
                ready.Add(item);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FailPersistBatchItem(item, ex);
            }
        }

        if (ready.Count == 0)
        {
            return;
        }

        var records = new JournalPhysicalWrittenRecord[ready.Count];
        for (var i = 0; i < ready.Count; i++)
        {
            records[i] = new JournalPhysicalWrittenRecord(1, ready[i].Sequence, ready[i].Location);
        }

        JournalAppendOutcome[] outcomes;
        try
        {
            outcomes = _journal.AppendPhysicalWrittenBatch(records);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var item in ready)
            {
                FailPersistBatchItem(item, ex);
            }

            return;
        }

        for (var i = 0; i < ready.Count; i++)
        {
            var item = ready[i];
            try
            {
                switch (outcomes[i])
                {
                    case JournalAppendOutcome.Applied:
                    case JournalAppendOutcome.IdempotentNoOp:
                        ClearPrePhysicalWritten(item.Sequence);
                        item.PhysicalWrittenDurable = true;
                        ThrowIfTestFault(PersistFaultPoint.AfterPhysicalWritten, item.Sequence);
                        MarkUnpublishedProvenCopiesDead(item.Accept.ArtId, item.Proven);
                        break;

                    case JournalAppendOutcome.Conflict:
                        ClearPrePhysicalWritten(item.Sequence);
                        item.WritePathVerified = false;
                        if (!TryGetDurablePhysicalWritten(item.Sequence, out var existing))
                        {
                            throw new InvalidOperationException(
                                $"PhysicalWritten conflict for sequence {item.Sequence} " +
                                "but no durable PhysicalWritten was found.");
                        }

                        item.Location = existing.Location;
                        item.PhysicalWrittenDurable = true;
                        MarkUnpublishedProvenCopiesDead(item.Accept.ArtId, item.Proven);
                        break;

                    case JournalAppendOutcome.Rejected:
                        throw new InvalidOperationException(
                            $"PhysicalWritten rejected for sequence {item.Sequence} " +
                            "(prerequisite missing or location length below ArtSize).");

                    default:
                        throw new InvalidOperationException(
                            $"Unexpected PhysicalWritten outcome {outcomes[i]} for sequence {item.Sequence}.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FailPersistBatchItem(item, ex);
            }
        }
    }

    private void PersistBatchPresent(List<PersistBatchItem> work)
    {
        PhysicalProofProbe.NoteStage(PhysicalProofProbe.StagePresentBegin);
        var publishing = new List<PersistBatchItem>();
        var metadata = new List<StoredArticleMetadata>();
        foreach (var item in work)
        {
            if (item.Failed || !item.PhysicalWrittenDurable)
            {
                continue;
            }

            try
            {
                PreparePresent(item, publishing, metadata);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FailPersistBatchItem(item, ex);
            }
        }

        try
        {
            if (publishing.Count > 0)
            {
                var frames = metadata.ToArray();
                var results = new DurableIndexAppend[frames.Length];
                var offsets = new long[frames.Length];
                _index.CommitPresentBatch(frames, results, offsets);
                for (var i = 0; i < publishing.Count; i++)
                {
                    var item = publishing[i];
                    try
                    {
                        switch (results[i])
                        {
                            case DurableIndexAppend.Appended:
                                BindIndexFrameFromSequence(item.Sequence, item.Accept.ArtId, offsets[i]);
                                item.PresentDurable = true;
                                break;
                            case DurableIndexAppend.Unchanged:
                                ReleaseUnboundIndexReservation(item.Sequence);
                                item.PresentDurable = true;
                                break;
                            default:
                                if (!ShouldFinishPhysicalWrittenWithoutPublishing(item.Accept, item.Location))
                                {
                                    throw new InvalidOperationException(
                                        $"Index commit failed for sequence {item.Sequence} during recovery.");
                                }

                                item.PresentDurable = true;
                                break;
                        }

                        ThrowIfTestFault(PersistFaultPoint.AfterIndexCommit, item.Sequence);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        FailPersistBatchItem(item, ex);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var item in publishing)
            {
                if (!item.PresentDurable)
                {
                    FailPersistBatchItem(item, ex);
                }
            }
        }
        finally
        {
            foreach (var item in work)
            {
                if (item.PublicationSegment is { } segmentId)
                {
                    ExitIndexPublication(segmentId);
                    item.PublicationSegment = null;
                }
            }

            PhysicalProofProbe.NoteStage(PhysicalProofProbe.StagePresentCommit);
        }
    }

    private void PreparePresent(
        PersistBatchItem item,
        List<PersistBatchItem> publishing,
        List<StoredArticleMetadata> metadata)
    {
        var locationProved = item.WritePathVerified
            ? _segments.TryConfirmFlushedAppend(
                item.Location,
                item.Accept.ArtId,
                item.Accept.ArtHash,
                item.Accept.ArtSize)
            : TryProvePhysicalLocation(item.Accept, item.Location, out _);
        if (!locationProved)
        {
            FileArticleStorageEngineLogMessages.PhysicalWrittenUnusable(
                _logger,
                item.Sequence,
                item.Location.SegmentId.Value,
                item.Location.Offset);
            if (_index.TryGet(item.Accept.ArtId, out var existing)
                && existing.State == ArticleStorageState.Present
                && LocationsEqual(existing.Location, item.Location))
            {
                _ = TryInvalidate(item.Accept.ArtId);
            }

            throw new InvalidOperationException(
                $"Physical range for sequence {item.Sequence} failed integrity proof during recovery.");
        }

        ThrowIfTestFault(PersistFaultPoint.BeforeIndexCommit, item.Sequence);
        if (ShouldFinishPhysicalWrittenWithoutPublishing(item.Accept, item.Location))
        {
            item.PresentDurable = true;
            return;
        }

        var segmentId = item.Location.SegmentId.Value;
        if (!TryEnterIndexPublication(segmentId))
        {
            throw new IOException(
                $"Index publication for sequence {item.Sequence} lost the retirement fence.");
        }

        item.PublicationSegment = segmentId;
        var publicationHook = TestHookAfterPublicationEnteredBeforeIndexCommit;
        TestHookAfterPublicationEnteredBeforeIndexCommit = null;
        publicationHook?.Invoke(item.Location.SegmentId);
        if (ShouldFinishPhysicalWrittenWithoutPublishing(item.Accept, item.Location))
        {
            item.PresentDurable = true;
            return;
        }

        var frameBytes = (long)ArticleIndexRecordCodec.RecordLength;
        if (_capacityAdmissionEnabled && !EnsureUnboundIndexReservation(item.Sequence, frameBytes))
        {
            throw new PersistCompletionDeferredException(
                $"Cannot reserve the {frameBytes}-byte index Present frame for sequence {item.Sequence}.");
        }

        metadata.Add(new StoredArticleMetadata(
            item.Accept.ArtId,
            item.Accept.ArtHash,
            item.Accept.ArtSize,
            item.Location,
            ArticleStorageState.Present,
            _timeProvider.GetUtcNow(),
            item.Sequence));
        publishing.Add(item);
    }

    private void PersistBatchIndexCommitted(List<PersistBatchItem> work)
    {
        var readyStart = IndexCommittedProbe.MarkEnabled();
        var ready = new List<PersistBatchItem>();
        foreach (var item in work)
        {
            if (item.Failed || !item.PresentDurable)
            {
                continue;
            }

            try
            {
                ThrowIfTestFault(PersistFaultPoint.BeforeIndexCommitted, item.Sequence);
                ready.Add(item);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FailPersistBatchItem(item, ex);
            }
        }

        IndexCommittedProbe.AddReady(readyStart);
        if (ready.Count == 0)
        {
            return;
        }

        var recordArrayStart = IndexCommittedProbe.MarkEnabled();
        var records = new JournalIndexCommittedRecord[ready.Count];
        for (var i = 0; i < ready.Count; i++)
        {
            records[i] = new JournalIndexCommittedRecord(1, ready[i].Sequence);
        }

        IndexCommittedProbe.AddRecordArray(recordArrayStart);
        JournalAppendOutcome[] outcomes;
        var batchSlot = IndexCommittedProbe.BeginBatch();
        var appendAllocBefore = batchSlot >= 0 ? GC.GetAllocatedBytesForCurrentThread() : 0;
        var appendStart = batchSlot >= 0 ? Stopwatch.GetTimestamp() : 0;
        try
        {
            outcomes = _journal.AppendIndexCommittedBatch(records);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var item in ready)
            {
                FailPersistBatchItem(item, ex);
            }

            return;
        }
        finally
        {
            IndexCommittedProbe.EndBatch(batchSlot, appendStart, appendAllocBefore);
        }

        for (var i = 0; i < ready.Count; i++)
        {
            var item = ready[i];
            IndexCommittedProbe.BeginArticle();
            var articleStart = IndexCommittedProbe.MarkArticle();
            try
            {
                if (outcomes[i] is JournalAppendOutcome.Rejected or JournalAppendOutcome.Conflict)
                {
                    throw new InvalidOperationException(
                        $"IndexCommitted rejected for sequence {item.Sequence}.");
                }

                TryPopulateCacheAfterDurableCommit(item.Accept);
                ThrowIfTestFault(PersistFaultPoint.AfterIndexCommitted, item.Sequence);
                var afterStart = IndexCommittedProbe.MarkArticle();
                var toStringStart = IndexCommittedProbe.MarkArticle();
                var artIdText = item.Accept.ArtId.ToLowerHexString();
                IndexCommittedProbe.AddToString(toStringStart);
                var logStart = IndexCommittedProbe.MarkArticle();
                FileArticleStorageEngineLogMessages.RecoveredIndexCommitted(
                    _logger,
                    item.Sequence,
                    artIdText);
                IndexCommittedProbe.AddLog(logStart);
                var clearStart = IndexCommittedProbe.MarkArticle();
                ClearPersistRetryAttempts(item.Sequence);
                IndexCommittedProbe.AddClearWall(clearStart);
                var retryStart = IndexCommittedProbe.MarkArticle();
                if (item.AttemptsBefore > 0)
                {
                    FileArticleStorageEngineLogMessages.PersistRetrySucceeded(
                        _logger,
                        item.Sequence,
                        item.AttemptsBefore);
                }

                IndexCommittedProbe.AddRetryLog(retryStart);
                IndexCommittedProbe.AddAfter(afterStart);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FailPersistBatchItem(item, ex);
            }
            finally
            {
                IndexCommittedProbe.EndArticle(articleStart);
            }
        }
    }

    private void FailPersistBatchItem(PersistBatchItem item, Exception ex)
    {
        if (item.Failed)
        {
            return;
        }

        item.Failed = true;
        HandlePersistFailure(item.Sequence, ex);
    }

    private sealed class PersistBatchItem
    {
        public PersistBatchItem(JournalIncompleteSequence incomplete, int attemptsBefore)
        {
            Incomplete = incomplete;
            AttemptsBefore = attemptsBefore;
        }

        public JournalIncompleteSequence Incomplete { get; }

        public int AttemptsBefore { get; }

        public JournalAcceptRecord Accept => Incomplete.Accept;

        public ulong Sequence => Accept.Sequence;

        public bool Failed { get; set; }

        public bool SegmentReady { get; set; }

        public bool PhysicalWrittenDurable { get; set; }

        public bool PresentDurable { get; set; }

        public StoredArticleLocation Location { get; set; }

        public IReadOnlyList<StoredArticleLocation> Proven { get; set; } = [];

        public ulong? PublicationSegment { get; set; }

        /// <summary>
        /// True when <see cref="Location"/> is the receipt from a flushed append of this Accept
        /// in this batch and no later step replaced that location.
        /// Present then confirms the header instead of rereading the payload.
        /// </summary>
        public bool WritePathVerified { get; set; }
    }
}
