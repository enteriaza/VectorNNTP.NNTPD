using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>Phase 4B.3: explicit single-article live relocation primitive.</summary>
public sealed partial class FileArticleStorageEngine
{
    /// <summary>Test fault injection points during live relocation (engine-owned only).</summary>
    internal enum RelocationFaultPoint
    {
        None = 0,
        AfterIntentBeforeAppend = 1,
        AfterAppendBeforeWritten = 2,
        AfterWrittenBeforeIndex = 3,
    }

    /// <summary>Optional one-shot relocation fault (cleared when consumed). Tests only.</summary>
    internal RelocationFaultPoint TestRelocationFaultPoint { get; set; }

    /// <summary>
    /// Invoked after a successful compaction capacity reservation and before destination append.
    /// Tests only. Remains installed until cleared by the test.
    /// </summary>
    internal Action? TestHookAfterCompactionCapacityReserved { get; set; }

    /// <summary>
    /// Invoked after durable RelocationWritten and before <see cref="IArticleIndex.TryRelocate"/>.
    /// Tests only (concurrent eviction / prior relocate). Cleared after invoke.
    /// </summary>
    internal Action? TestHookBeforeIndexRelocate { get; set; }

    /// <summary>
    /// Relocates one Present article from a Closed source segment to a fresh Active append,
    /// using the Phase 4B.1/4B.2 compaction journal contract.
    /// </summary>
    /// <remarks>
    /// Durable order: RelocationIntent → destination append+flush → RelocationWritten →
    /// index TryRelocate. Does not Put/Remove/Clear the article cache. Does not retire or
    /// delete the source. Does not append CompactionCommitted.
    /// </remarks>
    /// <param name="compactionId">Open compaction that already has CompactionBegin.</param>
    /// <param name="relocationId">Unique within <paramref name="compactionId"/>.</param>
    /// <param name="sourceSegmentId">Closed source segment.</param>
    /// <param name="sourceGeneration">Catalogue generation fence (must match Begin).</param>
    /// <param name="artId">Article to relocate.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<ArticleRelocationResult> RelocateArticleAsync(
        ulong compactionId,
        ulong relocationId,
        SegmentId sourceSegmentId,
        ulong sourceGeneration,
        ArticleId artId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (compactionId == 0 || relocationId == 0)
        {
            return new ArticleRelocationResult(
                ArticleRelocationOutcome.RejectedCompaction,
                artId,
                Reason: "compaction-or-relocation-id-zero");
        }

        if (!TryValidateCompactionFence(
                compactionId,
                sourceSegmentId,
                sourceGeneration,
                artId,
                out var rejected))
        {
            return rejected;
        }

        if (!TryValidateSourceSegment(sourceSegmentId, sourceGeneration, artId, out rejected))
        {
            return rejected;
        }

        // Resume from durable journal state when this RelocationId already exists.
        if (_journal.TryGetCompaction(compactionId, out var snap))
        {
            var existing = snap.Relocations.FirstOrDefault(r => r.Intent.RelocationId == relocationId);
            if (existing.Intent.RelocationId == relocationId)
            {
                if (existing.Intent.ArtId != artId)
                {
                    return new ArticleRelocationResult(
                        ArticleRelocationOutcome.Conflict,
                        artId,
                        Reason: "relocation-id-artid-mismatch");
                }

                if (existing.Written is { } written)
                {
                    return await CompleteFromWrittenAsync(
                            existing.Intent,
                            written,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                // Intent-only: continue only while index still Present @ ExpectedSource.
                return await ContinueFromIntentAsync(existing.Intent, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (!_index.TryGet(artId, out var meta)
            || meta.State != ArticleStorageState.Present)
        {
            return new ArticleRelocationResult(
                ArticleRelocationOutcome.RejectedNotPresent,
                artId,
                Reason: "not-present");
        }

        if (meta.Location.SegmentId.Value != sourceSegmentId.Value)
        {
            return new ArticleRelocationResult(
                ArticleRelocationOutcome.RejectedNotPresent,
                artId,
                Reason: "index-not-on-source-segment");
        }

        var expectedSource = meta.Location;
        if (!_segments.TryReadProven(
                expectedSource,
                artId,
                meta.ArtHash,
                meta.ArtSize,
                out var artData))
        {
            return new ArticleRelocationResult(
                ArticleRelocationOutcome.RejectedSourceCorrupt,
                artId,
                Reason: "source-proof-failed");
        }

        var intent = new JournalRelocationIntentRecord(
            1,
            compactionId,
            relocationId,
            artId,
            meta.ArtHash,
            meta.ArtSize,
            expectedSource);

        var intentAppend = await AppendReservedCompactionJournalFrameAsync(
                compactionId,
                CompactionJournalFrameKind.Intent,
                relocationId,
                ArticleJournalFrameCodec.RelocationIntentFrameLength,
                ct => _journal.AppendRelocationIntentAsync(intent, ct),
                cancellationToken)
            .ConfigureAwait(false);
        if (intentAppend.CapacityDenied)
        {
            return new ArticleRelocationResult(
                ArticleRelocationOutcome.RejectedCapacity,
                artId,
                Reason: "compaction-journal-intent-capacity");
        }

        var intentOutcome = intentAppend.Outcome;
        if (intentOutcome == JournalAppendOutcome.Conflict)
        {
            return new ArticleRelocationResult(
                ArticleRelocationOutcome.Conflict,
                artId,
                Reason: "relocation-intent-conflict");
        }

        if (intentOutcome == JournalAppendOutcome.Rejected)
        {
            return new ArticleRelocationResult(
                ArticleRelocationOutcome.RejectedCompaction,
                artId,
                Reason: "relocation-intent-rejected");
        }

        return await AppendDestinationAndFinishAsync(intent, artData, cancellationToken)
            .ConfigureAwait(false);
    }

    private bool TryValidateCompactionFence(
        ulong compactionId,
        SegmentId sourceSegmentId,
        ulong sourceGeneration,
        ArticleId artId,
        out ArticleRelocationResult rejected)
    {
        if (!_journal.TryGetCompaction(compactionId, out var snap) || snap.Retired is not null)
        {
            rejected = new ArticleRelocationResult(
                ArticleRelocationOutcome.RejectedCompaction,
                artId,
                Reason: "compaction-missing-or-retired");
            return false;
        }

        if (snap.Begin.SourceSegmentId.Value != sourceSegmentId.Value
            || snap.Begin.SourceGeneration != sourceGeneration)
        {
            rejected = new ArticleRelocationResult(
                ArticleRelocationOutcome.RejectedGenerationMismatch,
                artId,
                Reason: "compaction-begin-fence-mismatch");
            return false;
        }

        rejected = default;
        return true;
    }

    private bool TryValidateSourceSegment(
        SegmentId sourceSegmentId,
        ulong sourceGeneration,
        ArticleId artId,
        out ArticleRelocationResult rejected)
    {
        if (!Catalogue.TryGet(sourceSegmentId, out var info))
        {
            rejected = new ArticleRelocationResult(
                ArticleRelocationOutcome.RejectedSourceMissing,
                artId,
                Reason: "source-missing");
            return false;
        }

        if (info.State == SegmentState.Retired)
        {
            rejected = new ArticleRelocationResult(
                ArticleRelocationOutcome.RejectedSourceNotClosed,
                artId,
                Reason: "source-retired");
            return false;
        }

        if (info.State != SegmentState.Closed)
        {
            rejected = new ArticleRelocationResult(
                ArticleRelocationOutcome.RejectedSourceNotClosed,
                artId,
                Reason: "source-not-closed");
            return false;
        }

        // Catalogue Generation is process-local (Phase 2B): discovery reallocates generations on
        // every Open. Durable fencing for compaction is CompactionBegin.SourceGeneration plus
        // Closed state — compared in TryValidateCompactionFence. Same-process callers typically
        // pass Catalogue.Generation which still matches Begin; after restart they must pass
        // Begin.SourceGeneration.
        _ = sourceGeneration;

        rejected = default;
        return true;
    }

    private async Task<ArticleRelocationResult> ContinueFromIntentAsync(
        JournalRelocationIntentRecord intent,
        CancellationToken cancellationToken)
    {
        if (!_index.TryGet(intent.ArtId, out var meta)
            || meta.State != ArticleStorageState.Present
            || !LocationsEqual(meta.Location, intent.ExpectedSourceLocation))
        {
            // Intent-only with no destination knowledge: abandon without inventing bytes.
            return new ArticleRelocationResult(
                ArticleRelocationOutcome.Abandoned,
                intent.ArtId,
                Reason: "intent-only-index-left-expected-source");
        }

        if (!_segments.TryReadProven(
                intent.ExpectedSourceLocation,
                intent.ArtId,
                intent.ArtHash,
                intent.ArtSize,
                out var artData))
        {
            return new ArticleRelocationResult(
                ArticleRelocationOutcome.RejectedSourceCorrupt,
                intent.ArtId,
                Reason: "source-proof-failed");
        }

        return await AppendDestinationAndFinishAsync(intent, artData, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ArticleRelocationResult> AppendDestinationAndFinishAsync(
        JournalRelocationIntentRecord intent,
        ReadOnlyMemory<byte> artData,
        CancellationToken cancellationToken)
    {
        var reservedCompaction = false;
        var reservedIndex = false;
        if (_capacityAdmissionEnabled)
        {
            var volume = RequireSegmentVolume();
            var requiredBytes = SegmentRecordCodec.RecordLengthForArtSize(intent.ArtSize);
            lock (_gate)
            {
                var ceiling = _capacityMaximumUtilization + _capacityCompactionHeadroom;
                var decision = Admit(
                    volume,
                    requiredBytes,
                    ceiling,
                    static _ => false,
                    ledger => ledger.ReserveCompaction(
                        intent.CompactionId,
                        intent.RelocationId,
                        requiredBytes));
                if (!decision.Admitted)
                {
                    FileArticleStorageEngineLogMessages.RejectedCompactionCapacity(
                        _logger,
                        intent.ArtId.ToString() ?? string.Empty,
                        intent.ExpectedSourceLocation.SegmentId.Value,
                        intent.CompactionId,
                        intent.RelocationId,
                        requiredBytes,
                        decision.Snapshot.UsedBytes,
                        decision.ArticleReservedBytes,
                        decision.CompactionReservedBytes,
                        decision.CheckpointReservedBytes,
                        decision.Snapshot.TotalBytes,
                        decision.Snapshot.AvailableBytes,
                        _capacityMaximumUtilization,
                        _capacityCompactionHeadroom);
                    return new ArticleRelocationResult(
                        ArticleRelocationOutcome.RejectedCapacity,
                        intent.ArtId,
                        Reason: "storage-capacity");
                }

                reservedCompaction = true;
                if (!TryReserveDirectIndexFrame(
                        intent.ArtId,
                        ArticleIndexRecordCodec.RecordLength,
                        _capacityMaximumUtilization + _capacityCompactionHeadroom))
                {
                    ReleaseCompactionReservation(intent.CompactionId, intent.RelocationId);
                    reservedCompaction = false;
                    return new ArticleRelocationResult(
                        ArticleRelocationOutcome.RejectedCapacity,
                        intent.ArtId,
                        Reason: "index-capacity");
                }

                reservedIndex = true;
            }

            var reservedHook = TestHookAfterCompactionCapacityReserved;
            reservedHook?.Invoke();
        }

        try
        {
            // After capacity reservation: intentional pre-append fault still rolls back reservation.
            ThrowIfRelocationFault(RelocationFaultPoint.AfterIntentBeforeAppend);

            var appender = await _segments.GetActiveAppenderAsync(cancellationToken).ConfigureAwait(false);
            if (appender.SegmentId.Value == intent.ExpectedSourceLocation.SegmentId.Value)
            {
                // Source is Closed, so active must never be the source; fail closed if invariants break.
                throw new InvalidOperationException(
                    "Active destination segment must not be the Closed compaction source.");
            }

            var destination = await appender.AppendAsync(artData, cancellationToken).ConfigureAwait(false);
            _ = Interlocked.Increment(ref _physicalAppendCount);

            // Destination Flush(true) completed; process-local compaction reservation may release.
            if (reservedCompaction)
            {
                ReleaseCompactionReservation(intent.CompactionId, intent.RelocationId);
                reservedCompaction = false;
            }

            ThrowIfRelocationFault(RelocationFaultPoint.AfterAppendBeforeWritten);

            var written = new JournalRelocationWrittenRecord(
                1,
                intent.CompactionId,
                intent.RelocationId,
                destination);
            var writtenAppend = await AppendReservedCompactionJournalFrameAsync(
                    intent.CompactionId,
                    CompactionJournalFrameKind.Written,
                    intent.RelocationId,
                    ArticleJournalFrameCodec.RelocationWrittenFrameLength,
                    ct => _journal.AppendRelocationWrittenAsync(written, ct),
                    cancellationToken)
                .ConfigureAwait(false);
            if (writtenAppend.CapacityDenied)
            {
                if (reservedIndex)
                {
                    RollbackDirectIndexFrame(ArticleIndexRecordCodec.RecordLength);
                    reservedIndex = false;
                }

                return new ArticleRelocationResult(
                    ArticleRelocationOutcome.RejectedCapacity,
                    intent.ArtId,
                    Reason: "compaction-journal-written-capacity");
            }

            var writtenOutcome = writtenAppend.Outcome;
            if (writtenOutcome == JournalAppendOutcome.Conflict)
            {
                throw new InvalidOperationException(
                    $"RelocationWritten conflict for compaction {intent.CompactionId} " +
                    $"relocation {intent.RelocationId}.");
            }

            if (writtenOutcome == JournalAppendOutcome.Rejected)
            {
                throw new InvalidOperationException(
                    $"RelocationWritten rejected for compaction {intent.CompactionId} " +
                    $"relocation {intent.RelocationId}.");
            }

            return FinishIndexRelocate(intent, destination, ref reservedIndex);
        }
        catch
        {
            if (reservedCompaction)
            {
                ReleaseCompactionReservation(intent.CompactionId, intent.RelocationId);
            }

            if (reservedIndex)
            {
                RollbackDirectIndexFrame(ArticleIndexRecordCodec.RecordLength);
            }

            throw;
        }
    }

    private void ReleaseCompactionReservation(ulong compactionId, ulong relocationId)
    {
        lock (_gate)
        {
            if (!_capacityAdmissionEnabled)
            {
                return;
            }

            _ = RequireSegmentVolume().WithLedger(
                ledger => ledger.ReleaseCompaction(compactionId, relocationId));
        }
    }

    private async Task<ArticleRelocationResult> CompleteFromWrittenAsync(
        JournalRelocationIntentRecord intent,
        JournalRelocationWrittenRecord written,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_segments.TryReadProven(
                written.DestinationLocation,
                intent.ArtId,
                intent.ArtHash,
                intent.ArtSize,
                out _))
        {
            throw new InvalidOperationException(
                $"Compaction RelocationWritten destination failed integrity proof " +
                $"(compaction={intent.CompactionId}, relocation={intent.RelocationId}).");
        }

        // Do not append another destination when Written already exists.
        var reservedIndex = false;
        return FinishIndexRelocate(intent, written.DestinationLocation, ref reservedIndex);
    }

    private ArticleRelocationResult FinishIndexRelocate(
        JournalRelocationIntentRecord intent,
        StoredArticleLocation destination,
        ref bool indexFrameReserved)
    {
        ThrowIfRelocationFault(RelocationFaultPoint.AfterWrittenBeforeIndex);

        var hook = TestHookBeforeIndexRelocate;
        TestHookBeforeIndexRelocate = null;
        hook?.Invoke();

        var frameBytes = (long)ArticleIndexRecordCodec.RecordLength;
        if (!indexFrameReserved
            && _capacityAdmissionEnabled
            && !(_index.TryGet(intent.ArtId, out var current)
                 && current.State == ArticleStorageState.Present
                 && LocationsEqual(current.Location, destination)))
        {
            if (!TryReserveDirectIndexFrame(
                    intent.ArtId,
                    frameBytes,
                    _capacityMaximumUtilization + _capacityCompactionHeadroom))
            {
                return new ArticleRelocationResult(
                    ArticleRelocationOutcome.RejectedCapacity,
                    intent.ArtId,
                    Reason: "index-capacity");
            }

            indexFrameReserved = true;
        }

        long frameOffset = -1;
        var lengthBefore = indexFrameReserved ? _index.DurableLength : 0L;
        ArticleRelocationResult result = default;
        try
        {
            Catalogue.ExecuteLocked(() =>
                result = FinishIndexRelocateUnlocked(intent, destination, ref frameOffset));
            if (indexFrameReserved)
            {
                if (result.Outcome == ArticleRelocationOutcome.Relocated)
                {
                    BindDirectIndexFrame(intent.ArtId, frameOffset, frameBytes);
                }
                else
                {
                    RollbackDirectIndexFrame(frameBytes);
                }

                indexFrameReserved = false;
            }
        }
        catch
        {
            if (indexFrameReserved)
            {
                FinishDirectIndexFrameAfterThrow(intent.ArtId, lengthBefore, frameBytes);
                indexFrameReserved = false;
            }

            throw;
        }

        return result;
    }

    private ArticleRelocationResult FinishIndexRelocateUnlocked(
        JournalRelocationIntentRecord intent,
        StoredArticleLocation destination,
        ref long frameOffset)
    {
        if (_index.TryGet(intent.ArtId, out var current)
            && current.State == ArticleStorageState.Present
            && LocationsEqual(current.Location, destination))
        {
            return new ArticleRelocationResult(
                ArticleRelocationOutcome.IdempotentNoOp,
                intent.ArtId,
                destination);
        }

        var outcome = _index.TryRelocateReporting(
            intent.ArtId,
            intent.ExpectedSourceLocation,
            destination,
            intent.ArtHash,
            intent.ArtSize,
            out frameOffset);

        switch (outcome)
        {
            case ArticleRelocateOutcome.Relocated:
                ApplySourceBecameDead(intent.ExpectedSourceLocation);
                return new ArticleRelocationResult(
                    ArticleRelocationOutcome.Relocated,
                    intent.ArtId,
                    destination);

            case ArticleRelocateOutcome.IdempotentNoOp:
                return new ArticleRelocationResult(
                    ArticleRelocationOutcome.IdempotentNoOp,
                    intent.ArtId,
                    destination);

            case ArticleRelocateOutcome.ExpectedLocationMismatch:
            case ArticleRelocateOutcome.NotPresent:
            case ArticleRelocateOutcome.IdentityMismatch:
                ApplyDestinationBecameDead(destination);
                return new ArticleRelocationResult(
                    ArticleRelocationOutcome.Abandoned,
                    intent.ArtId,
                    destination,
                    Reason: outcome.ToString());

            default:
                ApplyDestinationBecameDead(destination);
                return new ArticleRelocationResult(
                    ArticleRelocationOutcome.Abandoned,
                    intent.ArtId,
                    destination,
                    Reason: outcome.ToString());
        }
    }

    private void ApplySourceBecameDead(in StoredArticleLocation source)
    {
        try
        {
            Catalogue.ApplyLiveDeadDelta(
                source.SegmentId,
                liveDelta: -source.Length,
                deadDelta: source.Length);
        }
        catch (InvalidOperationException)
        {
            // Catalogue may lack the segment in edge tests; index remains authoritative.
        }
    }

    private void ApplyDestinationBecameDead(in StoredArticleLocation destination)
    {
        try
        {
            // Append already counted LiveBytes; move provisional live → dead orphan.
            Catalogue.ApplyLiveDeadDelta(
                destination.SegmentId,
                liveDelta: -destination.Length,
                deadDelta: destination.Length);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ThrowIfRelocationFault(RelocationFaultPoint point)
    {
        if (TestRelocationFaultPoint != point)
        {
            return;
        }

        TestRelocationFaultPoint = RelocationFaultPoint.None;
        throw new IOException($"Injected relocation fault at {point}.");
    }
}
