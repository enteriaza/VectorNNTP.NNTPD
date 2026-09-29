using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;

namespace VectorNNTP.StorageServer.Storage.Engine.Memory;

/// <summary>In-memory Model A staged journal (Accept → PhysicalWritten → IndexCommitted).</summary>
public sealed class MemoryArticleJournal : IArticleJournal
{
    private readonly long _softLimitBytes;
    private readonly long _hardLimitBytes;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, SequenceState> _bySequence = new();
    private readonly Dictionary<ArticleId, ulong> _outstandingArtIdToSequence = new();
    private readonly List<object> _physicalLog = [];
    private ulong _nextSequence = 1;
    private long _outstandingRecoverableBytes;
    private long _journalPhysicalBytes;

    /// <summary>Initializes journal limits from runtime options.</summary>
    public MemoryArticleJournal(ArticleStorageRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _softLimitBytes = options.JournalSoftLimitBytes;
        _hardLimitBytes = options.JournalHardLimitBytes;
    }

    /// <inheritdoc />
    public long OutstandingRecoverableBytes
    {
        get
        {
            lock (_gate)
            {
                return _outstandingRecoverableBytes;
            }
        }
    }

    /// <inheritdoc />
    public long JournalPhysicalBytes
    {
        get
        {
            lock (_gate)
            {
                return _journalPhysicalBytes;
            }
        }
    }

    /// <inheritdoc />
    public StorageWritePressure Pressure
    {
        get
        {
            lock (_gate)
            {
                return ComputePressureUnlocked();
            }
        }
    }

    /// <summary>
    /// Atomically rejects under pressure / duplicate / conflict, otherwise appends Accept with ArtData.
    /// </summary>
    public bool TryAppendNewAccept(
        ArticleId artId,
        ulong artHash,
        int artSize,
        DateTimeOffset utcNow,
        ReadOnlyMemory<byte> artData,
        out JournalAcceptRecord record,
        out ArticleAcceptOutcome rejectOutcome)
    {
        if (artSize != artData.Length)
        {
            record = null!;
            rejectOutcome = ArticleAcceptOutcome.RejectedInvalid;
            return false;
        }

        lock (_gate)
        {
            if (_outstandingArtIdToSequence.TryGetValue(artId, out var existingSeq)
                && _bySequence.TryGetValue(existingSeq, out var existing)
                && !existing.IndexCommitted)
            {
                record = existing.Accept;
                rejectOutcome = existing.Accept.ArtHash == artHash && existing.Accept.ArtSize == artSize
                    ? ArticleAcceptOutcome.Duplicate
                    : ArticleAcceptOutcome.Conflict;
                return false;
            }

            if (ComputePressureUnlocked() == StorageWritePressure.Critical)
            {
                record = null!;
                rejectOutcome = ArticleAcceptOutcome.RejectedPressure;
                return false;
            }

            var sequence = _nextSequence++;
            record = new JournalAcceptRecord(
                version: 1,
                sequence,
                artId,
                artHash,
                artSize,
                utcNow,
                artData);
            _bySequence[sequence] = new SequenceState(record);
            _outstandingArtIdToSequence[artId] = sequence;
            _outstandingRecoverableBytes += artSize;
            _journalPhysicalBytes += EstimateAcceptPhysicalBytes(record);
            _physicalLog.Add(record);
            rejectOutcome = default;
            return true;
        }
    }

    /// <inheritdoc />
    public ValueTask AppendAcceptAsync(JournalAcceptRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_bySequence.ContainsKey(record.Sequence))
            {
                throw new InvalidOperationException($"Journal sequence {record.Sequence} already exists.");
            }

            _bySequence[record.Sequence] = new SequenceState(record);
            _outstandingArtIdToSequence[record.ArtId] = record.Sequence;
            _outstandingRecoverableBytes += record.ArtSize;
            _journalPhysicalBytes += EstimateAcceptPhysicalBytes(record);
            _physicalLog.Add(record);
            if (record.Sequence >= _nextSequence)
            {
                _nextSequence = record.Sequence + 1;
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<JournalAppendOutcome> AppendPhysicalWrittenAsync(
        JournalPhysicalWrittenRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_bySequence.TryGetValue(record.Sequence, out var state))
            {
                return ValueTask.FromResult(JournalAppendOutcome.Rejected);
            }

            if (state.PhysicalWritten is { } existing)
            {
                if (LocationsEqual(existing.Location, record.Location))
                {
                    return ValueTask.FromResult(JournalAppendOutcome.IdempotentNoOp);
                }

                return ValueTask.FromResult(JournalAppendOutcome.Conflict);
            }

            if (record.Location.Length < state.Accept.ArtSize)
            {
                return ValueTask.FromResult(JournalAppendOutcome.Rejected);
            }

            state.PhysicalWritten = record;
            _journalPhysicalBytes += EstimatePhysicalWrittenBytes(record);
            _physicalLog.Add(record);
            return ValueTask.FromResult(JournalAppendOutcome.Applied);
        }
    }

    /// <inheritdoc />
    public ValueTask<JournalAppendOutcome> AppendIndexCommittedAsync(
        JournalIndexCommittedRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_bySequence.TryGetValue(record.Sequence, out var state))
            {
                return ValueTask.FromResult(JournalAppendOutcome.Rejected);
            }

            if (state.IndexCommitted)
            {
                return ValueTask.FromResult(JournalAppendOutcome.IdempotentNoOp);
            }

            if (state.PhysicalWritten is null)
            {
                return ValueTask.FromResult(JournalAppendOutcome.Rejected);
            }

            state.IndexCommitted = true;
            state.IndexCommittedRecord = record;
            _ = _outstandingArtIdToSequence.Remove(state.Accept.ArtId);
            _outstandingRecoverableBytes = Math.Max(0L, _outstandingRecoverableBytes - state.Accept.ArtSize);
            _journalPhysicalBytes += EstimateIndexCommittedBytes(record);
            _physicalLog.Add(record);
            return ValueTask.FromResult(JournalAppendOutcome.Applied);
        }
    }

    /// <inheritdoc />
    public bool TryGetOutstanding(ArticleId artId, out JournalAcceptRecord record)
    {
        lock (_gate)
        {
            if (_outstandingArtIdToSequence.TryGetValue(artId, out var sequence)
                && _bySequence.TryGetValue(sequence, out var state)
                && !state.IndexCommitted)
            {
                record = state.Accept;
                return true;
            }

            record = null!;
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<JournalIncompleteSequence> EnumerateIncomplete()
    {
        lock (_gate)
        {
            return _bySequence.Values
                .Where(static s => !s.IndexCommitted)
                .OrderBy(static s => s.Accept.Sequence)
                .Select(static s => new JournalIncompleteSequence(s.Accept, s.PhysicalWritten))
                .ToArray();
        }
    }

    /// <summary>
    /// Exports a durable-journal snapshot for cold-start recovery tests (memory only).
    /// </summary>
    public MemoryArticleJournalSnapshot ExportSnapshot()
    {
        lock (_gate)
        {
            var sequences = _bySequence.Values
                .OrderBy(static s => s.Accept.Sequence)
                .Select(static s => new MemoryJournalSequenceSnapshot(
                    s.Accept,
                    s.PhysicalWritten,
                    s.IndexCommittedRecord,
                    s.IndexCommitted,
                    s.Checkpointed))
                .ToArray();
            return new MemoryArticleJournalSnapshot(
                SoftLimitBytes: _softLimitBytes,
                HardLimitBytes: _hardLimitBytes,
                NextSequence: _nextSequence,
                OutstandingRecoverableBytes: _outstandingRecoverableBytes,
                JournalPhysicalBytes: _journalPhysicalBytes,
                Sequences: sequences);
        }
    }

    /// <summary>
    /// Creates a new journal rehydrated from <paramref name="snapshot"/> (cold-start tests).
    /// </summary>
    public static MemoryArticleJournal ImportSnapshot(MemoryArticleJournalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var options = new ArticleStorageRuntimeOptions(
            ControlDir: "control",
            SegmentDir: "cache",
            JournalSoftLimitBytes: snapshot.SoftLimitBytes,
            JournalHardLimitBytes: snapshot.HardLimitBytes,
            SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        var journal = new MemoryArticleJournal(options);
        journal.LoadSnapshot(snapshot);
        return journal;
    }

    private void LoadSnapshot(MemoryArticleJournalSnapshot snapshot)
    {
        lock (_gate)
        {
            _bySequence.Clear();
            _outstandingArtIdToSequence.Clear();
            _physicalLog.Clear();
            _nextSequence = snapshot.NextSequence;
            _outstandingRecoverableBytes = 0;
            _journalPhysicalBytes = 0;

            foreach (var entry in snapshot.Sequences.OrderBy(static s => s.Accept.Sequence))
            {
                var state = new SequenceState(entry.Accept)
                {
                    PhysicalWritten = entry.PhysicalWritten,
                    IndexCommitted = entry.IndexCommitted,
                    IndexCommittedRecord = entry.IndexCommittedRecord,
                    Checkpointed = entry.Checkpointed,
                };
                _bySequence[entry.Accept.Sequence] = state;
                _physicalLog.Add(entry.Accept);
                _journalPhysicalBytes += EstimateAcceptPhysicalBytes(entry.Accept);
                if (entry.PhysicalWritten is { } pw)
                {
                    _physicalLog.Add(pw);
                    _journalPhysicalBytes += EstimatePhysicalWrittenBytes(pw);
                }

                if (entry.IndexCommitted)
                {
                    if (entry.IndexCommittedRecord is { } ic)
                    {
                        state.IndexCommittedRecord = ic;
                        _physicalLog.Add(ic);
                        _journalPhysicalBytes += EstimateIndexCommittedBytes(ic);
                    }
                }
                else
                {
                    _outstandingArtIdToSequence[entry.Accept.ArtId] = entry.Accept.Sequence;
                    _outstandingRecoverableBytes += entry.Accept.ArtSize;
                }
            }

            // Prefer exported counters when present (checkpoint may have reduced physical bytes).
            _outstandingRecoverableBytes = snapshot.OutstandingRecoverableBytes;
            _journalPhysicalBytes = snapshot.JournalPhysicalBytes;
        }
    }

    /// <summary>
    /// Simulates checkpoint truncation releasing physical bytes of IndexCommitted sequences (tests).
    /// Does not change OutstandingRecoverableBytes.
    /// </summary>
    public long CheckpointTruncateCommitted()
    {
        lock (_gate)
        {
            long released = 0;
            foreach (var state in _bySequence.Values.Where(static s => s.IndexCommitted))
            {
                if (state.Checkpointed)
                {
                    continue;
                }

                released += EstimateAcceptPhysicalBytes(state.Accept);
                if (state.PhysicalWritten is { } pw)
                {
                    released += EstimatePhysicalWrittenBytes(pw);
                }

                if (state.IndexCommittedRecord is { } ic)
                {
                    released += EstimateIndexCommittedBytes(ic);
                }

                state.Checkpointed = true;
            }

            _journalPhysicalBytes = Math.Max(0L, _journalPhysicalBytes - released);
            return released;
        }
    }

    private StorageWritePressure ComputePressureUnlocked()
    {
        if (_outstandingRecoverableBytes >= _hardLimitBytes)
        {
            return StorageWritePressure.Critical;
        }

        if (_outstandingRecoverableBytes >= _softLimitBytes)
        {
            return StorageWritePressure.Elevated;
        }

        return StorageWritePressure.Normal;
    }

    private static bool LocationsEqual(in StoredArticleLocation left, in StoredArticleLocation right) =>
        left.SegmentId.Value == right.SegmentId.Value
        && left.Offset == right.Offset
        && left.Length == right.Length;

    private static long EstimateAcceptPhysicalBytes(JournalAcceptRecord record) =>
        64 + record.ArtSize;

    private static long EstimatePhysicalWrittenBytes(JournalPhysicalWrittenRecord record) =>
        32;

    private static long EstimateIndexCommittedBytes(JournalIndexCommittedRecord record) =>
        16;

    private sealed class SequenceState(JournalAcceptRecord accept)
    {
        public JournalAcceptRecord Accept { get; } = accept;

        public JournalPhysicalWrittenRecord? PhysicalWritten { get; set; }

        public bool IndexCommitted { get; set; }

        public JournalIndexCommittedRecord? IndexCommittedRecord { get; set; }

        public bool Checkpointed { get; set; }
    }
}
