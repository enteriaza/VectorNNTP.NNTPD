using System.IO.Hashing;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Storage.Engine.FileJournal;

public sealed partial class FileArticleJournal
{
    /// <summary>
    /// Appends PhysicalWritten frames and durability-flushes the journal once.
    /// Sequence state changes only after that flush returns. A flush failure leaves the
    /// frames pending and does not mark them durable.
    /// </summary>
    internal JournalAppendOutcome[] AppendPhysicalWrittenBatch(IReadOnlyList<JournalPhysicalWrittenRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending is not null)
            {
                throw PendingOwnedByOther();
            }

            FinishPendingBatchUnlocked();
            var outcomes = new JournalAppendOutcome[records.Count];
            var frames = new List<byte[]>(records.Count);
            var applied = new List<JournalPhysicalWrittenRecord>(records.Count);
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (!_bySequence.TryGetValue(record.Sequence, out var state))
                {
                    outcomes[i] = JournalAppendOutcome.Rejected;
                    continue;
                }

                if (state.PhysicalWritten is { } existing)
                {
                    outcomes[i] = LocationsEqual(existing.Location, record.Location)
                        ? JournalAppendOutcome.IdempotentNoOp
                        : JournalAppendOutcome.Conflict;
                    continue;
                }

                if (record.Location.Length < state.ArtSize)
                {
                    outcomes[i] = JournalAppendOutcome.Rejected;
                    continue;
                }

                frames.Add(ArticleJournalFrameCodec.EncodePhysicalWritten(record));
                applied.Add(record);
                outcomes[i] = JournalAppendOutcome.Applied;
            }

            if (frames.Count == 0)
            {
                return outcomes;
            }

            WriteFramesOrPendUnlocked(
                frames,
                (offset, payload) => new PendingFrameBatch(offset, payload, applied.ToArray(), indexCommitted: null));
            foreach (var record in applied)
            {
                _bySequence[record.Sequence].PhysicalWritten = record;
            }

            return outcomes;
        }
    }

    /// <summary>
    /// Appends IndexCommitted frames and durability-flushes the journal once.
    /// IndexCommitted, outstanding bytes, and checkpoint eligibility change only after
    /// that flush returns.
    /// </summary>
    internal JournalAppendOutcome[] AppendIndexCommittedBatch(IReadOnlyList<JournalIndexCommittedRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending is not null)
            {
                throw PendingOwnedByOther();
            }

            FinishPendingBatchUnlocked();
            var outcomes = new JournalAppendOutcome[records.Count];
            var frames = new List<byte[]>(records.Count);
            var applied = new List<JournalIndexCommittedRecord>(records.Count);
            var encodeStart = IndexCommittedProbe.MarkBatch();
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (!_bySequence.TryGetValue(record.Sequence, out var state))
                {
                    outcomes[i] = JournalAppendOutcome.Rejected;
                    continue;
                }

                if (state.IndexCommitted)
                {
                    outcomes[i] = JournalAppendOutcome.IdempotentNoOp;
                    continue;
                }

                if (state.PhysicalWritten is null)
                {
                    outcomes[i] = JournalAppendOutcome.Rejected;
                    continue;
                }

                frames.Add(ArticleJournalFrameCodec.EncodeIndexCommitted(record));
                applied.Add(record);
                outcomes[i] = JournalAppendOutcome.Applied;
            }

            IndexCommittedProbe.AddEncode(encodeStart, frames.Count);

            if (frames.Count == 0)
            {
                return outcomes;
            }

            WriteFramesOrPendUnlocked(
                frames,
                (offset, payload) => new PendingFrameBatch(offset, payload, physicalWritten: null, applied.ToArray()));
            foreach (var record in applied)
            {
                ApplyIndexCommittedUnlocked(_bySequence[record.Sequence], record);
            }

            return outcomes;
        }
    }

    private void FinishPendingBatchUnlocked()
    {
        var pending = _pendingBatch;
        if (pending is null)
        {
            return;
        }

        if (!BatchBytesMatchUnlocked(pending.Offset, pending.Payload))
        {
            throw new UnreconciledDurableTailException(
                "Pending journal batch bytes changed before the durable flush.",
                new IOException("Pending durable payload no longer matches the file."));
        }

        try
        {
            var pendingFlush = IndexCommittedProbe.MarkBatch();
            DurableFlushUnlocked();
            IndexCommittedProbe.AddPendingFlush(pendingFlush);
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            throw new UnreconciledDurableTailException(
                "Article journal batch is present but not durable.",
                ex);
        }

        ApplyPendingBatchUnlocked(pending);
        _pendingBatch = null;
        _stream.Seek(0, SeekOrigin.End);
    }

    private void WriteFramesOrPendUnlocked(
        List<byte[]> frames,
        Func<long, byte[], PendingFrameBatch> pendingAt)
    {
        ReconcileBlockedTailUnlocked();
        _stream.Seek(0, SeekOrigin.End);
        var start = _stream.Position;
        var concatStart = IndexCommittedProbe.MarkBatch();
        var payload = ConcatFrames(frames);
        IndexCommittedProbe.AddConcat(concatStart);
        var written = 0;
        try
        {
            var writeStart = IndexCommittedProbe.MarkBatch();
            foreach (var frame in frames)
            {
                TestBeforeFrameAppend?.Invoke();
                _stream.Write(frame, 0, frame.Length);
                TestAfterWriteBeforeFlush?.Invoke(_stream, start + written, frame.Length);
                written += frame.Length;
            }

            IndexCommittedProbe.AddWrite(writeStart);
            var flushStart = IndexCommittedProbe.MarkBatch();
            DurableFlushUnlocked();
            IndexCommittedProbe.AddFlush(flushStart);
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            if (BatchBytesMatchUnlocked(start, payload))
            {
                _pendingBatch = pendingAt(start, payload);
                throw new UnreconciledDurableTailException(
                    "Article journal batch is present but not durable.",
                    ex,
                    createdByThisCall: true);
            }

            if (_stream.Length != start)
            {
                TruncateOrBlockUnlocked(start, createdByThisCall: true);
            }

            throw;
        }
    }

    private void ApplyPendingBatchUnlocked(PendingFrameBatch pending)
    {
        if (pending.PhysicalWritten is { } physicalWritten)
        {
            foreach (var record in physicalWritten)
            {
                if (!_bySequence.TryGetValue(record.Sequence, out var state))
                {
                    throw new ArticleJournalCorruptException(
                        $"PhysicalWritten batch for unknown sequence {record.Sequence}.");
                }

                if (state.PhysicalWritten is { } existing)
                {
                    if (!LocationsEqual(existing.Location, record.Location))
                    {
                        throw new ArticleJournalCorruptException(
                            $"Conflicting PhysicalWritten locations for sequence {record.Sequence}.");
                    }

                    continue;
                }

                state.PhysicalWritten = record;
            }
        }

        if (pending.IndexCommitted is { } indexCommitted)
        {
            foreach (var record in indexCommitted)
            {
                if (!_bySequence.TryGetValue(record.Sequence, out var state))
                {
                    throw new ArticleJournalCorruptException(
                        $"IndexCommitted batch for unknown sequence {record.Sequence}.");
                }

                if (state.PhysicalWritten is null)
                {
                    throw new ArticleJournalCorruptException(
                        $"IndexCommitted without PhysicalWritten for sequence {record.Sequence}.");
                }

                ApplyIndexCommittedUnlocked(state, record);
            }
        }
    }

    private void ApplyIndexCommittedUnlocked(SequenceState state, JournalIndexCommittedRecord record)
    {
        if (state.IndexCommitted)
        {
            return;
        }

        state.IndexCommitted = true;
        state.IndexCommittedRecord = record;
        _ = _outstandingArtIdToSequence.Remove(state.IncompleteAccept.ArtId);
        _outstandingRecoverableBytes = Math.Max(0L, _outstandingRecoverableBytes - state.ArtSize);
        state.IncompleteAccept.PermitPayloadDetach();
        state.ReleaseAcceptPayload();
        LogPressureIfChangedUnlocked();
    }

    private bool BatchBytesMatchUnlocked(long start, byte[] payload)
    {
        try
        {
            _stream.Flush(flushToDisk: false);
            if (_stream.Length < start + payload.Length)
            {
                return false;
            }

            var observed = ReadExactUnlocked(start, payload.Length);
            return observed.Length == payload.Length
                && XxHash3.HashToUInt64(observed) == XxHash3.HashToUInt64(payload);
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            _tailUnreconciled = true;
            _tailValidEnd = start;
            throw new UnreconciledDurableTailException(
                "Article journal batch could not be inspected after an ambiguous append.",
                ex,
                createdByThisCall: true);
        }
    }

    private static byte[] ConcatFrames(List<byte[]> frames)
    {
        var length = 0;
        foreach (var frame in frames)
        {
            length += frame.Length;
        }

        var payload = new byte[length];
        var offset = 0;
        foreach (var frame in frames)
        {
            frame.CopyTo(payload, offset);
            offset += frame.Length;
        }

        return payload;
    }

    /// <summary>
    /// Complete journal frames whose shared durability flush has not returned.
    /// Not restored after restart. Checkpoint refuses to replace the file while one exists.
    /// </summary>
    private sealed class PendingFrameBatch
    {
        public PendingFrameBatch(
            long offset,
            byte[] payload,
            JournalPhysicalWrittenRecord[]? physicalWritten,
            JournalIndexCommittedRecord[]? indexCommitted)
        {
            Offset = offset;
            Payload = payload;
            PhysicalWritten = physicalWritten;
            IndexCommitted = indexCommitted;
        }

        public long Offset { get; }

        public byte[] Payload { get; }

        public JournalPhysicalWrittenRecord[]? PhysicalWritten { get; }

        public JournalIndexCommittedRecord[]? IndexCommitted { get; }
    }
}
