using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Storage.Engine.FileJournal;

/// <summary>Compaction journal append/replay/checkpoint (Phase 4B.2).</summary>
public sealed partial class FileArticleJournal
{
    /// <summary>Gets the next CompactionId that will be allocated.</summary>
    public ulong NextCompactionId
    {
        get
        {
            lock (_gate)
            {
                return _nextCompactionId;
            }
        }
    }

    /// <summary>Allocates a new globally monotonic <see cref="JournalCompactionBeginRecord.CompactionId"/>.</summary>
    public ulong AllocateCompactionId()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _nextCompactionId++;
        }
    }

    /// <summary>
    /// Returns a CompactionBegin that is physically present but not yet durable.
    /// Process-local only; restart does not restore it.
    /// </summary>
    internal bool TryGetPendingCompactionBegin(out JournalCompactionBeginRecord record)
    {
        lock (_gate)
        {
            if (_pending is PendingJournalFrame.CompactionOperation { Begin: { } begin } pending
                && pending.FrameType == ArticleJournalFrameType.CompactionBegin)
            {
                record = begin;
                return true;
            }
        }

        record = default;
        return false;
    }

    /// <summary>
    /// Returns a RelocationWritten destination that is physically present but not yet durable.
    /// </summary>
    internal bool TryGetPendingRelocationWritten(
        ulong compactionId,
        ulong relocationId,
        out StoredArticleLocation destination)
    {
        lock (_gate)
        {
            if (_pending is PendingJournalFrame.CompactionOperation
                {
                    FrameType: ArticleJournalFrameType.RelocationWritten,
                    WrittenDestination: { } pendingDestination,
                } pending
                && pending.CompactionId == compactionId
                && pending.RelocationId == relocationId)
            {
                destination = pendingDestination;
                return true;
            }
        }

        destination = default;
        return false;
    }

    /// <summary>Looks up in-memory compaction state by id.</summary>
    public bool TryGetCompaction(ulong compactionId, out CompactionJournalSnapshot snapshot)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_compactions.TryGetValue(compactionId, out var state))
            {
                snapshot = default;
                return false;
            }

            snapshot = state.ToSnapshot();
            return true;
        }
    }

    /// <summary>Enumerates non-retired open compactions (Begin present, Retired absent).</summary>
    public IReadOnlyList<CompactionJournalSnapshot> EnumerateOpenCompactions()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _compactions.Values
                .Where(static s => s.Retired is null)
                .OrderBy(static s => s.Begin.CompactionId)
                .Select(static s => s.ToSnapshot())
                .ToArray();
        }
    }

    /// <summary>
    /// Enumerates every known compaction including retired (recovery / diagnostics).
    /// </summary>
    public IReadOnlyList<CompactionJournalSnapshot> EnumerateCompactions()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _compactions.Values
                .OrderBy(static s => s.Begin.CompactionId)
                .Select(static s => s.ToSnapshot())
                .ToArray();
        }
    }

    /// <summary>Appends CompactionBegin. Does not affect Accept recoverable bytes.</summary>
    public ValueTask<JournalAppendOutcome> AppendCompactionBeginAsync(
        JournalCompactionBeginRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record.CompactionId == 0)
        {
            return ValueTask.FromResult(JournalAppendOutcome.Rejected);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_compactions.TryGetValue(record.CompactionId, out var existing))
            {
                if (existing.Begin.SourceSegmentId.Value == record.SourceSegmentId.Value
                    && existing.Begin.SourceGeneration == record.SourceGeneration)
                {
                    return ValueTask.FromResult(JournalAppendOutcome.IdempotentNoOp);
                }

                return ValueTask.FromResult(JournalAppendOutcome.Conflict);
            }

            var encodedBegin = ArticleJournalFrameCodec.EncodeCompactionBegin(record);
            AppendFrameUnlocked(
                encodedBegin,
                offset => new PendingJournalFrame.CompactionOperation(
                    offset,
                    ArticleJournalFrameType.CompactionBegin,
                    record.CompactionId,
                    relocationId: 0,
                    encodedBegin,
                    record));

            _compactions[record.CompactionId] = new CompactionState(record);
            if (record.CompactionId >= _nextCompactionId)
            {
                _nextCompactionId = record.CompactionId + 1;
            }

            return ValueTask.FromResult(JournalAppendOutcome.Applied);
        }
    }

    /// <summary>Appends RelocationIntent for an open compaction.</summary>
    public ValueTask<JournalAppendOutcome> AppendRelocationIntentAsync(
        JournalRelocationIntentRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record.CompactionId == 0 || record.RelocationId == 0)
        {
            return ValueTask.FromResult(JournalAppendOutcome.Rejected);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_compactions.TryGetValue(record.CompactionId, out var compaction)
                || compaction.Retired is not null)
            {
                return ValueTask.FromResult(JournalAppendOutcome.Rejected);
            }

            if (compaction.Relocations.TryGetValue(record.RelocationId, out var relocation))
            {
                return IntentsEqual(relocation.Intent, record)
                    ? ValueTask.FromResult(JournalAppendOutcome.IdempotentNoOp)
                    : ValueTask.FromResult(JournalAppendOutcome.Conflict);
            }

            var encodedIntent = ArticleJournalFrameCodec.EncodeRelocationIntent(record);
            AppendFrameUnlocked(
                encodedIntent,
                offset => new PendingJournalFrame.CompactionOperation(
                    offset,
                    ArticleJournalFrameType.RelocationIntent,
                    record.CompactionId,
                    record.RelocationId,
                    encodedIntent));
            compaction.Relocations[record.RelocationId] = new RelocationState(record);
            return ValueTask.FromResult(JournalAppendOutcome.Applied);
        }
    }

    /// <summary>Appends RelocationWritten after a durable destination append.</summary>
    public ValueTask<JournalAppendOutcome> AppendRelocationWrittenAsync(
        JournalRelocationWrittenRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record.CompactionId == 0 || record.RelocationId == 0)
        {
            return ValueTask.FromResult(JournalAppendOutcome.Rejected);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_compactions.TryGetValue(record.CompactionId, out var compaction)
                || compaction.Retired is not null
                || !compaction.Relocations.TryGetValue(record.RelocationId, out var relocation))
            {
                return ValueTask.FromResult(JournalAppendOutcome.Rejected);
            }

            if (relocation.Written is { } existing)
            {
                return LocationsEqual(existing.DestinationLocation, record.DestinationLocation)
                    ? ValueTask.FromResult(JournalAppendOutcome.IdempotentNoOp)
                    : ValueTask.FromResult(JournalAppendOutcome.Conflict);
            }

            var encodedWritten = ArticleJournalFrameCodec.EncodeRelocationWritten(record);
            AppendFrameUnlocked(
                encodedWritten,
                offset => new PendingJournalFrame.CompactionOperation(
                    offset,
                    ArticleJournalFrameType.RelocationWritten,
                    record.CompactionId,
                    record.RelocationId,
                    encodedWritten,
                    writtenDestination: record.DestinationLocation));
            relocation.Written = record;
            return ValueTask.FromResult(JournalAppendOutcome.Applied);
        }
    }

    /// <summary>Appends CompactionCommitted (logical source exhaustion).</summary>
    public ValueTask<JournalAppendOutcome> AppendCompactionCommittedAsync(
        JournalCompactionCommittedRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record.CompactionId == 0)
        {
            return ValueTask.FromResult(JournalAppendOutcome.Rejected);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_compactions.TryGetValue(record.CompactionId, out var compaction)
                || compaction.Retired is not null)
            {
                return ValueTask.FromResult(JournalAppendOutcome.Rejected);
            }

            if (compaction.Committed)
            {
                return ValueTask.FromResult(JournalAppendOutcome.IdempotentNoOp);
            }

            var encodedCommitted = ArticleJournalFrameCodec.EncodeCompactionCommitted(record);
            AppendFrameUnlocked(
                encodedCommitted,
                offset => new PendingJournalFrame.CompactionOperation(
                    offset,
                    ArticleJournalFrameType.CompactionCommitted,
                    record.CompactionId,
                    relocationId: 0,
                    encodedCommitted));
            compaction.Committed = true;
            compaction.CommittedRecord = record;
            return ValueTask.FromResult(JournalAppendOutcome.Applied);
        }
    }

    /// <summary>
    /// Appends CompactionRetired (journal contract only; physical retirement is a later phase).
    /// </summary>
    public ValueTask<JournalAppendOutcome> AppendCompactionRetiredAsync(
        JournalCompactionRetiredRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record.CompactionId == 0)
        {
            return ValueTask.FromResult(JournalAppendOutcome.Rejected);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_compactions.TryGetValue(record.CompactionId, out var compaction))
            {
                return ValueTask.FromResult(JournalAppendOutcome.Rejected);
            }

            if (compaction.Retired is { } existing)
            {
                if (existing.SourceSegmentId.Value == record.SourceSegmentId.Value
                    && existing.ExpectedGeneration == record.ExpectedGeneration)
                {
                    return ValueTask.FromResult(JournalAppendOutcome.IdempotentNoOp);
                }

                return ValueTask.FromResult(JournalAppendOutcome.Conflict);
            }

            if (!compaction.Committed)
            {
                return ValueTask.FromResult(JournalAppendOutcome.Rejected);
            }

            if (compaction.Begin.SourceSegmentId.Value != record.SourceSegmentId.Value
                || compaction.Begin.SourceGeneration != record.ExpectedGeneration)
            {
                return ValueTask.FromResult(JournalAppendOutcome.Conflict);
            }

            var encodedRetired = ArticleJournalFrameCodec.EncodeCompactionRetired(record);
            AppendFrameUnlocked(
                encodedRetired,
                offset => new PendingJournalFrame.CompactionOperation(
                    offset,
                    ArticleJournalFrameType.CompactionRetired,
                    record.CompactionId,
                    relocationId: 0,
                    encodedRetired));
            compaction.Retired = record;
            return ValueTask.FromResult(JournalAppendOutcome.Applied);
        }
    }

    private void ApplyCompactionFrameUnlocked(in ArticleJournalDecodedFrame decoded)
    {
        switch (decoded.Type)
        {
            case ArticleJournalFrameType.CompactionBegin:
                ApplyCompactionBeginReplayUnlocked(
                    decoded.CompactionBegin
                    ?? throw new ArticleJournalCorruptException("CompactionBegin frame missing body."));
                break;
            case ArticleJournalFrameType.RelocationIntent:
                ApplyRelocationIntentReplayUnlocked(
                    decoded.RelocationIntent
                    ?? throw new ArticleJournalCorruptException("RelocationIntent frame missing body."));
                break;
            case ArticleJournalFrameType.RelocationWritten:
                ApplyRelocationWrittenReplayUnlocked(
                    decoded.RelocationWritten
                    ?? throw new ArticleJournalCorruptException("RelocationWritten frame missing body."));
                break;
            case ArticleJournalFrameType.CompactionCommitted:
                ApplyCompactionCommittedReplayUnlocked(
                    decoded.CompactionCommitted
                    ?? throw new ArticleJournalCorruptException("CompactionCommitted frame missing body."));
                break;
            case ArticleJournalFrameType.CompactionRetired:
                ApplyCompactionRetiredReplayUnlocked(
                    decoded.CompactionRetired
                    ?? throw new ArticleJournalCorruptException("CompactionRetired frame missing body."));
                break;
            default:
                throw new ArticleJournalCorruptException($"Unknown journal frame type {(byte)decoded.Type}.");
        }
    }

    private void ApplyCompactionBeginReplayUnlocked(JournalCompactionBeginRecord record)
    {
        if (_compactions.TryGetValue(record.CompactionId, out var existing))
        {
            if (existing.Begin.SourceSegmentId.Value != record.SourceSegmentId.Value
                || existing.Begin.SourceGeneration != record.SourceGeneration)
            {
                throw new ArticleJournalCorruptException(
                    $"Conflicting CompactionBegin for CompactionId {record.CompactionId}.");
            }

            return;
        }

        _compactions[record.CompactionId] = new CompactionState(record);
        if (record.CompactionId >= _nextCompactionId)
        {
            _nextCompactionId = record.CompactionId + 1;
        }
    }

    private void ApplyRelocationIntentReplayUnlocked(JournalRelocationIntentRecord record)
    {
        if (!_compactions.TryGetValue(record.CompactionId, out var compaction))
        {
            throw new ArticleJournalCorruptException(
                $"RelocationIntent for unknown CompactionId {record.CompactionId}.");
        }

        if (compaction.Relocations.TryGetValue(record.RelocationId, out var existing))
        {
            if (!IntentsEqual(existing.Intent, record))
            {
                throw new ArticleJournalCorruptException(
                    $"Conflicting RelocationIntent CompactionId={record.CompactionId} RelocationId={record.RelocationId}.");
            }

            return;
        }

        compaction.Relocations[record.RelocationId] = new RelocationState(record);
    }

    private void ApplyRelocationWrittenReplayUnlocked(JournalRelocationWrittenRecord record)
    {
        if (!_compactions.TryGetValue(record.CompactionId, out var compaction)
            || !compaction.Relocations.TryGetValue(record.RelocationId, out var relocation))
        {
            throw new ArticleJournalCorruptException(
                $"RelocationWritten without Intent CompactionId={record.CompactionId} RelocationId={record.RelocationId}.");
        }

        if (relocation.Written is { } existing)
        {
            if (!LocationsEqual(existing.DestinationLocation, record.DestinationLocation))
            {
                throw new ArticleJournalCorruptException(
                    $"Conflicting RelocationWritten CompactionId={record.CompactionId} RelocationId={record.RelocationId}.");
            }

            return;
        }

        relocation.Written = record;
    }

    private void ApplyCompactionCommittedReplayUnlocked(JournalCompactionCommittedRecord record)
    {
        if (!_compactions.TryGetValue(record.CompactionId, out var compaction))
        {
            throw new ArticleJournalCorruptException(
                $"CompactionCommitted for unknown CompactionId {record.CompactionId}.");
        }

        compaction.Committed = true;
        compaction.CommittedRecord = record;
    }

    private void ApplyCompactionRetiredReplayUnlocked(JournalCompactionRetiredRecord record)
    {
        if (!_compactions.TryGetValue(record.CompactionId, out var compaction))
        {
            throw new ArticleJournalCorruptException(
                $"CompactionRetired for unknown CompactionId {record.CompactionId}.");
        }

        if (compaction.Retired is { } existing)
        {
            if (existing.SourceSegmentId.Value != record.SourceSegmentId.Value
                || existing.ExpectedGeneration != record.ExpectedGeneration)
            {
                throw new ArticleJournalCorruptException(
                    $"Conflicting CompactionRetired for CompactionId {record.CompactionId}.");
            }

            return;
        }

        if (!compaction.Committed)
        {
            throw new ArticleJournalCorruptException(
                $"CompactionRetired without CompactionCommitted for CompactionId {record.CompactionId}.");
        }

        if (compaction.Begin.SourceSegmentId.Value != record.SourceSegmentId.Value
            || compaction.Begin.SourceGeneration != record.ExpectedGeneration)
        {
            throw new ArticleJournalCorruptException(
                $"CompactionRetired source/generation mismatch for CompactionId {record.CompactionId}.");
        }

        compaction.Retired = record;
    }

    private void WriteOpenCompactionFramesUnlocked(Stream temp, ulong[] omittedCompactionIds)
    {
        var omitted = new HashSet<ulong>(omittedCompactionIds);
        foreach (var compaction in _compactions.Values
                     .Where(compaction => !omitted.Contains(compaction.Begin.CompactionId))
                     .OrderBy(static s => s.Begin.CompactionId))
        {
            var begin = ArticleJournalFrameCodec.EncodeCompactionBegin(compaction.Begin);
            temp.Write(begin, 0, begin.Length);
            foreach (var relocation in compaction.Relocations.Values.OrderBy(static r => r.Intent.RelocationId))
            {
                var intent = ArticleJournalFrameCodec.EncodeRelocationIntent(relocation.Intent);
                temp.Write(intent, 0, intent.Length);
                if (relocation.Written is { } written)
                {
                    var writtenFrame = ArticleJournalFrameCodec.EncodeRelocationWritten(written);
                    temp.Write(writtenFrame, 0, writtenFrame.Length);
                }
            }

            if (compaction.Committed && compaction.CommittedRecord is { } committed)
            {
                var committedFrame = ArticleJournalFrameCodec.EncodeCompactionCommitted(committed);
                temp.Write(committedFrame, 0, committedFrame.Length);
            }

            if (compaction.Retired is { } retired)
            {
                var retiredFrame = ArticleJournalFrameCodec.EncodeCompactionRetired(retired);
                temp.Write(retiredFrame, 0, retiredFrame.Length);
            }
        }
    }

    private static bool IntentsEqual(in JournalRelocationIntentRecord a, in JournalRelocationIntentRecord b) =>
        a.CompactionId == b.CompactionId
        && a.RelocationId == b.RelocationId
        && a.ArtId == b.ArtId
        && a.ArtHash == b.ArtHash
        && a.ArtSize == b.ArtSize
        && LocationsEqual(a.ExpectedSourceLocation, b.ExpectedSourceLocation);

    private sealed class CompactionState(JournalCompactionBeginRecord begin)
    {
        public JournalCompactionBeginRecord Begin { get; } = begin;

        public Dictionary<ulong, RelocationState> Relocations { get; } = new();

        public bool Committed { get; set; }

        public JournalCompactionCommittedRecord? CommittedRecord { get; set; }

        public JournalCompactionRetiredRecord? Retired { get; set; }

        public CompactionJournalSnapshot ToSnapshot() =>
            new(
                Begin,
                Relocations.Values
                    .OrderBy(static r => r.Intent.RelocationId)
                    .Select(static r => new CompactionRelocationSnapshot(r.Intent, r.Written))
                    .ToArray(),
                Committed,
                CommittedRecord,
                Retired);
    }

    private sealed class RelocationState(JournalRelocationIntentRecord intent)
    {
        public JournalRelocationIntentRecord Intent { get; } = intent;

        public JournalRelocationWrittenRecord? Written { get; set; }
    }
}

/// <summary>Immutable snapshot of one compaction's journal state.</summary>
/// <param name="Begin">CompactionBegin record.</param>
/// <param name="Relocations">Relocation intents/written pairs.</param>
/// <param name="Committed">Whether CompactionCommitted was recorded.</param>
/// <param name="CommittedRecord">Committed record when present.</param>
/// <param name="Retired">Retired record when present.</param>
public readonly record struct CompactionJournalSnapshot(
    JournalCompactionBeginRecord Begin,
    IReadOnlyList<CompactionRelocationSnapshot> Relocations,
    bool Committed,
    JournalCompactionCommittedRecord? CommittedRecord,
    JournalCompactionRetiredRecord? Retired);

/// <summary>One relocation within a compaction snapshot.</summary>
/// <param name="Intent">RelocationIntent.</param>
/// <param name="Written">RelocationWritten when present.</param>
public readonly record struct CompactionRelocationSnapshot(
    JournalRelocationIntentRecord Intent,
    JournalRelocationWrittenRecord? Written);
