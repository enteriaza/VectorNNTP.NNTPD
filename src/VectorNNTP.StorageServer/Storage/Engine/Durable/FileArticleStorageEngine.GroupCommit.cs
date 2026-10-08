using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

public sealed partial class FileArticleStorageEngine
{
    /// <summary>
    /// Accept path used when <see cref="FileArticleJournal.AcceptGroupLimit"/> is greater than one.
    /// Arrival is counted before the engine gate so a concurrent caller is visible. The gate is not
    /// held while this call waits for the shared journal flush.
    /// </summary>
    private async Task<ArticleAcceptResult> AcceptGroupedAsync(
        ArticleRecord record,
        ReadOnlyMemory<byte> artData,
        CancellationToken cancellationToken,
        bool recoveryAttempted,
        bool recoveryReclaimedSpace)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reservedSegment = false;
        var reservedJournal = false;
        var reservedIndex = false;
        var segmentBytes = 0L;
        var journalBytes = 0L;
        var indexBytes = 0L;
        Task<GroupedJournalAccept>? durability = null;
        _journal.EnterGroupedAccept();
        try
        {
            _journal.TestBeforeGroupedLock?.Invoke();
            lock (_gate)
            {
                if (_index.TryGet(record.ArtId, out var existing)
                    && existing.State == ArticleStorageState.Present)
                {
                    if (existing.ArtHash == record.ArtHash && existing.ArtSize == record.ArtSize)
                    {
                        _ = _articleCache.Put(in record);
                        return ArticleAcceptResult.Duplicate(record.ArtId);
                    }

                    return ArticleAcceptResult.Conflict(record.ArtId);
                }

                if (_capacityAdmissionEnabled)
                {
                    segmentBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
                    journalBytes = ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize);
                    indexBytes = ArticleIndexRecordCodec.RecordLength;
                    if (!TryReserveAcceptPair(record, segmentBytes, journalBytes, indexBytes))
                    {
                        if (TryResolveOutstandingAccept(record, out var outstandingGrouped))
                        {
                            return outstandingGrouped;
                        }

                        return RejectCapacityAdmission(record, recoveryAttempted, recoveryReclaimedSpace);
                    }

                    reservedSegment = true;
                    reservedJournal = true;
                    reservedIndex = true;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    RollbackUnboundAccept(
                        reservedSegment,
                        reservedJournal,
                        reservedIndex,
                        segmentBytes,
                        journalBytes,
                        indexBytes);
                    throw new OperationCanceledException(cancellationToken);
                }

                try
                {
                    durability = _journal.StageGroupedAccept(
                        record.ArtId,
                        record.ArtHash,
                        record.ArtSize,
                        _timeProvider.GetUtcNow(),
                        artData);
                }
                catch
                {
                    RollbackUnboundAccept(
                        reservedSegment,
                        reservedJournal,
                        reservedIndex,
                        segmentBytes,
                        journalBytes,
                        indexBytes);
                    throw;
                }
            }
        }
        finally
        {
            _journal.ExitGroupedAccept();
        }

        GroupFlushObservation observation;
        try
        {
            observation = await WaitForGroupFlushAsync(durability!, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                RollbackUnboundAccept(
                    reservedSegment,
                    reservedJournal,
                    reservedIndex,
                    segmentBytes,
                    journalBytes,
                    indexBytes);
            }

            throw;
        }

        var grouped = observation.Result;

        if (!grouped.Appended || grouped.Record is null)
        {
            lock (_gate)
            {
                RollbackUnboundAccept(
                    reservedSegment,
                    reservedJournal,
                    reservedIndex,
                    segmentBytes,
                    journalBytes,
                    indexBytes);
            }

            return grouped.RejectOutcome switch
            {
                ArticleAcceptOutcome.Duplicate => ArticleAcceptResult.Duplicate(record.ArtId),
                ArticleAcceptOutcome.Conflict => ArticleAcceptResult.Conflict(record.ArtId),
                ArticleAcceptOutcome.RejectedInvalid =>
                    ArticleAcceptResult.RejectedInvalid(record.ArtId, "integrity"),
                _ => ArticleAcceptResult.RejectedPressure(record.ArtId),
            };
        }

        var journalRecord = grouped.Record;
        var enqueue = false;
        lock (_gate)
        {
            if (reservedSegment)
            {
                RequireSegmentVolume().WithLedger(ledger =>
                {
                    ledger.BindSequence(journalRecord.Sequence, segmentBytes);
                    return 0;
                });
            }

            if (reservedJournal)
            {
                RequireControlVolume().WithLedger(ledger =>
                {
                    ledger.BindJournalSequence(journalRecord.Sequence, journalBytes);
                    return 0;
                });
            }

            if (reservedIndex)
            {
                RequireControlVolume().WithLedger(ledger =>
                {
                    ledger.BindIndexUnbound(journalRecord.Sequence, indexBytes);
                    return 0;
                });
            }

            if (!_index.TryGet(record.ArtId, out var indexed)
                || indexed.State is not (ArticleStorageState.Evicted or ArticleStorageState.Invalid))
            {
                AddAcceptWithoutPhysicalBytes(journalRecord.Sequence);
            }

            _pendingAcceptAdmission = null;
            if (!SuspendBackgroundPersist)
            {
                EnqueuePersistWorkUnlocked(journalRecord.Sequence, journalRecord.ArtSize);
                enqueue = true;
            }
        }

        if (enqueue)
        {
            SignalPersistWorker();
        }

        FileArticleStorageEngineLogMessages.Accepted(
            _logger,
            record.ArtId.ToString() ?? string.Empty,
            journalRecord.Sequence,
            record.ArtSize);
        if (observation.Canceled)
        {
            // The shared flush already committed this member. The caller observed cancellation,
            // so Accept does not return success. The journal record stays for recovery and retry.
            throw new OperationCanceledException(cancellationToken);
        }

        return ArticleAcceptResult.Accepted(record.ArtId, journalRecord.Sequence);
    }

    /// <summary>
    /// Waits for the shared journal flush. A cancelled caller still observes the flush outcome.
    /// A flush that already succeeded is not rolled back.
    /// </summary>
    private static async Task<GroupFlushObservation> WaitForGroupFlushAsync(
        Task<GroupedJournalAccept> durability,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await durability.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new GroupFlushObservation(result, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var result = await durability.ConfigureAwait(false);
            return new GroupFlushObservation(result, true);
        }
    }

    /// <summary>Shared flush result plus whether the caller cancelled while waiting.</summary>
    /// <param name="Result">Journal result after the flush attempt.</param>
    /// <param name="Canceled">True when the caller cancelled and the flush still finished.</param>
    private readonly record struct GroupFlushObservation(GroupedJournalAccept Result, bool Canceled);

    /// <summary>
    /// Reserves journal order under the engine gate, encodes outside that gate, then the journal
    /// lock appends frames in sequence order and completes the group on one durability flush.
    /// </summary>
    private async Task<ArticleAcceptResult> AcceptPreparedAsync(
        ArticleRecord record,
        ReadOnlyMemory<byte> artData,
        CancellationToken cancellationToken,
        bool recoveryAttempted,
        bool recoveryReclaimedSpace)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reservedSegment = false;
        var reservedJournal = false;
        var reservedIndex = false;
        var segmentBytes = 0L;
        var journalBytes = 0L;
        var indexBytes = 0L;
        PreparedAcceptReservation ticket;
        lock (_gate)
        {
            if (_index.TryGet(record.ArtId, out var existing)
                && existing.State == ArticleStorageState.Present)
            {
                if (existing.ArtHash == record.ArtHash && existing.ArtSize == record.ArtSize)
                {
                    _ = _articleCache.Put(in record);
                    return ArticleAcceptResult.Duplicate(record.ArtId);
                }

                return ArticleAcceptResult.Conflict(record.ArtId);
            }

            if (_capacityAdmissionEnabled)
            {
                segmentBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
                journalBytes = ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize);
                indexBytes = ArticleIndexRecordCodec.RecordLength;
                if (!TryReserveAcceptPair(record, segmentBytes, journalBytes, indexBytes))
                {
                    if (TryResolveOutstandingAccept(record, out var outstandingPrepared))
                    {
                        return outstandingPrepared;
                    }

                    return RejectCapacityAdmission(record, recoveryAttempted, recoveryReclaimedSpace);
                }

                reservedSegment = true;
                reservedJournal = true;
                reservedIndex = true;
            }

            try
            {
                ticket = _journal.TryReservePreparedAccept(
                    record.ArtId,
                    record.ArtHash,
                    record.ArtSize,
                    _timeProvider.GetUtcNow(),
                    artData);
            }
            catch
            {
                RollbackUnboundAccept(
                    reservedSegment,
                    reservedJournal,
                    reservedIndex,
                    segmentBytes,
                    journalBytes,
                    indexBytes);
                throw;
            }

            if (!ticket.Reserved && !ticket.AlreadyDurable)
            {
                RollbackUnboundAccept(
                    reservedSegment,
                    reservedJournal,
                    reservedIndex,
                    segmentBytes,
                    journalBytes,
                    indexBytes);
                return RejectPrepared(record, ticket.RejectOutcome);
            }
        }

        if (ticket.AlreadyDurable && ticket.Record is not null)
        {
            return CompletePreparedAccept(
                record,
                ticket.Record,
                reservedSegment,
                reservedJournal,
                reservedIndex,
                segmentBytes,
                journalBytes,
                indexBytes);
        }

        try
        {
            await _journal.InvokePreparedEncodeGateAsync(ticket.Sequence, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = new JournalAcceptRecord(
                ArticleJournalFrameCodec.SchemaVersion,
                ticket.Sequence,
                record.ArtId,
                record.ArtHash,
                record.ArtSize,
                ticket.AcceptedUtc,
                artData);
            var encoded = ArticleJournalFrameCodec.EncodeAccept(prepared);
            _journal.PublishPreparedAccept(ticket.Sequence, prepared, encoded);
        }
        catch
        {
            _journal.AbandonPreparedAccept(ticket.Sequence);
            lock (_gate)
            {
                RollbackUnboundAccept(
                    reservedSegment,
                    reservedJournal,
                    reservedIndex,
                    segmentBytes,
                    journalBytes,
                    indexBytes);
            }

            throw;
        }

        GroupFlushObservation observation;
        try
        {
            observation = await WaitForGroupFlushAsync(ticket.Durability!, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                RollbackUnboundAccept(
                    reservedSegment,
                    reservedJournal,
                    reservedIndex,
                    segmentBytes,
                    journalBytes,
                    indexBytes);
            }

            throw;
        }

        var grouped = observation.Result;
        if (observation.Canceled && grouped.Appended && grouped.Record is not null)
        {
            _ = CompletePreparedAccept(
                record,
                grouped.Record,
                reservedSegment,
                reservedJournal,
                reservedIndex,
                segmentBytes,
                journalBytes,
                indexBytes);
            throw new OperationCanceledException(cancellationToken);
        }

        if (!grouped.Appended || grouped.Record is null)
        {
            lock (_gate)
            {
                RollbackUnboundAccept(
                    reservedSegment,
                    reservedJournal,
                    reservedIndex,
                    segmentBytes,
                    journalBytes,
                    indexBytes);
            }

            return RejectPrepared(record, grouped.RejectOutcome);
        }

        return CompletePreparedAccept(
            record,
            grouped.Record,
            reservedSegment,
            reservedJournal,
            reservedIndex,
            segmentBytes,
            journalBytes,
            indexBytes);
    }

    private ArticleAcceptResult CompletePreparedAccept(
        ArticleRecord record,
        JournalAcceptRecord journalRecord,
        bool reservedSegment,
        bool reservedJournal,
        bool reservedIndex,
        long segmentBytes,
        long journalBytes,
        long indexBytes)
    {
        var enqueue = false;
        lock (_gate)
        {
            enqueue = BindPreparedAcceptUnlocked(
                record,
                journalRecord,
                reservedSegment,
                reservedJournal,
                reservedIndex,
                segmentBytes,
                journalBytes,
                indexBytes);
        }

        if (enqueue)
        {
            SignalPersistWorker();
        }

        FileArticleStorageEngineLogMessages.Accepted(
            _logger,
            record.ArtId.ToString() ?? string.Empty,
            journalRecord.Sequence,
            record.ArtSize);
        return ArticleAcceptResult.Accepted(record.ArtId, journalRecord.Sequence);
    }

    private bool BindPreparedAcceptUnlocked(
        ArticleRecord record,
        JournalAcceptRecord journalRecord,
        bool reservedSegment,
        bool reservedJournal,
        bool reservedIndex,
        long segmentBytes,
        long journalBytes,
        long indexBytes)
    {
        if (reservedSegment)
        {
            RequireSegmentVolume().WithLedger(ledger =>
            {
                ledger.BindSequence(journalRecord.Sequence, segmentBytes);
                return 0;
            });
        }

        if (reservedJournal)
        {
            RequireControlVolume().WithLedger(ledger =>
            {
                ledger.BindJournalSequence(journalRecord.Sequence, journalBytes);
                return 0;
            });
        }

        if (reservedIndex)
        {
            RequireControlVolume().WithLedger(ledger =>
            {
                ledger.BindIndexUnbound(journalRecord.Sequence, indexBytes);
                return 0;
            });
        }

        if (!_index.TryGet(record.ArtId, out var indexed)
            || indexed.State is not (ArticleStorageState.Evicted or ArticleStorageState.Invalid))
        {
            AddAcceptWithoutPhysicalBytes(journalRecord.Sequence);
        }

        _pendingAcceptAdmission = null;
        if (SuspendBackgroundPersist)
        {
            return false;
        }

        EnqueuePersistWorkUnlocked(journalRecord.Sequence, journalRecord.ArtSize);
        return true;
    }

    private static ArticleAcceptResult RejectPrepared(ArticleRecord record, ArticleAcceptOutcome outcome) =>
        outcome switch
        {
            ArticleAcceptOutcome.Duplicate => ArticleAcceptResult.Duplicate(record.ArtId),
            ArticleAcceptOutcome.Conflict => ArticleAcceptResult.Conflict(record.ArtId),
            ArticleAcceptOutcome.RejectedInvalid => ArticleAcceptResult.RejectedInvalid(record.ArtId, "integrity"),
            _ => ArticleAcceptResult.RejectedPressure(record.ArtId),
        };
}
