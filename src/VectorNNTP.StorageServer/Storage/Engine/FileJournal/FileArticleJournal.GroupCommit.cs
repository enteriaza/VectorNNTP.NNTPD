using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Storage.Engine.FileJournal;

public sealed partial class FileArticleJournal
{
    /// <summary>
    /// Maximum Accept records written before one shared <see cref="FileStream.Flush(bool)"/>.
    /// One preserves a durability flush on every Accept. Greater than one withholds every
    /// Accept result in the open group until that flush returns.
    /// The getter does not take <see cref="_gate"/>, so an Accept can observe the limit and
    /// reject capacity while a checkpoint holds the journal lock.
    /// </summary>
    internal int AcceptGroupLimit
    {
        get => Volatile.Read(ref _acceptGroupLimit);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            lock (_gate)
            {
                Volatile.Write(ref _acceptGroupLimit, value);
                if (value == 1)
                {
                    FlushOpenAcceptGroupIfAnyUnlocked();
                }
            }
        }
    }

    /// <summary>
    /// Maximum summed ArtSize in an open Accept group. A single article larger than the
    /// limit still flushes alone. <see cref="long.MaxValue"/> disables the byte bound.
    /// </summary>
    internal long AcceptGroupMaxBytes
    {
        get
        {
            lock (_gate)
            {
                return _acceptGroupMaxBytes;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            lock (_gate)
            {
                _acceptGroupMaxBytes = value;
            }
        }
    }

    /// <summary>
    /// Fallback bound used only after another Accept is already in the arrival section.
    /// A lone Accept flushes immediately and does not arm this delay.
    /// <see cref="TimeSpan.Zero"/> adds no timer. Concurrent callers already waiting on
    /// the journal lock still share one flush, up to <see cref="AcceptGroupLimit"/>.
    /// </summary>
    internal TimeSpan AcceptGroupMaxDelay
    {
        get
        {
            lock (_gate)
            {
                return _acceptGroupMaxDelay;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            lock (_gate)
            {
                _acceptGroupMaxDelay = value;
            }
        }
    }

    /// <summary>Accept records staged in the open group and not yet durability-flushed.</summary>
    internal int StagedAcceptCount
    {
        get
        {
            lock (_gate)
            {
                return _openAcceptGroup?.Slots.Count ?? 0;
            }
        }
    }

    /// <summary>
    /// Replaces <see cref="Task.Delay(TimeSpan, CancellationToken)"/> for the group-commit
    /// fallback bound. Tests only. The production path is null.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task>? TestAcceptGroupDelay { get; set; }

    /// <summary>
    /// Runs after <see cref="EnterGroupedAccept"/> and before the engine gate.
    /// <see cref="VectorNNTP.StorageServer.Storage.Engine.Durable.FileArticleStorageEngine"/> invokes it. Tests only. The production path is null.
    /// </summary>
    internal Action? TestBeforeGroupedLock { get; set; }

    private int _acceptGroupLimit = 1;
    private int _groupedAcceptArrivals;
    private long _acceptGroupMaxBytes = long.MaxValue;
    private TimeSpan _acceptGroupMaxDelay = TimeSpan.Zero;
    private long _stagedRecoverableBytes;
    private AcceptCommitGroup? _openAcceptGroup;
    private AcceptCommitGroup? _failedAcceptGroup;
    private readonly Dictionary<ArticleId, JournalAcceptRecord> _stagedArtIds = new();

    /// <summary>Counts an Accept that has reached the grouped path and has not yet left it.</summary>
    internal void EnterGroupedAccept() => Interlocked.Increment(ref _groupedAcceptArrivals);

    /// <summary>
    /// Drops one grouped arrival. The last departure flushes a group that was left open for a caller
    /// that did not append.
    /// </summary>
    internal void ExitGroupedAccept()
    {
        if (Interlocked.Decrement(ref _groupedAcceptArrivals) == 0)
        {
            FlushLingeringAcceptGroup();
        }
    }

    /// <summary>
    /// Stages one Accept. The returned task completes only after the group's durability
    /// flush has returned and the record is readable as outstanding. A rejection completes
    /// successfully with <see cref="GroupedJournalAccept.Appended"/> false and does not write.
    /// The caller has already called <see cref="EnterGroupedAccept"/>.
    /// </summary>
    internal Task<GroupedJournalAccept> StageGroupedAccept(
        ArticleId artId,
        ulong artHash,
        int artSize,
        DateTimeOffset utcNow,
        ReadOnlyMemory<byte> artData)
    {
        if (artSize != artData.Length || artSize is < 1 or > ArticleResourceLimits.MaxArticleBytes)
        {
            return Task.FromResult(GroupedJournalAccept.Rejected(ArticleAcceptOutcome.RejectedInvalid));
        }

        return StageGroupedAcceptUnderLock(artId, artHash, artSize, utcNow, artData);
    }

    /// <summary>
    /// Appends one Accept and flushes when the group is full, this caller is alone, or the
    /// fallback delay is not needed. The caller already holds an arrival count.
    /// </summary>
    private Task<GroupedJournalAccept> StageGroupedAcceptUnderLock(
        ArticleId artId,
        ulong artHash,
        int artSize,
        DateTimeOffset utcNow,
        ReadOnlyMemory<byte> artData)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending is not null || _pendingBatch is not null || _tailUnreconciled)
            {
                throw new UnreconciledDurableTailException(
                    "Article journal accept group cannot start while another append is pending.",
                    new IOException("pending-journal-frame"));
            }

            if (_failedAcceptGroup is not null)
            {
                if (!TryCompleteFailedAcceptGroupUnlocked(artId, artHash, artSize, out var recovered, out var failedReject))
                {
                    throw PendingOwnedByOther();
                }

                return Task.FromResult(
                    failedReject == default
                        ? GroupedJournalAccept.Applied(recovered)
                        : GroupedJournalAccept.Rejected(failedReject));
            }

            if (TryRejectStagedOrOutstandingUnlocked(artId, artHash, artSize, out var reject))
            {
                return Task.FromResult(GroupedJournalAccept.Rejected(reject));
            }

            if (ComputePressureUnlocked() == StorageWritePressure.Critical)
            {
                return Task.FromResult(GroupedJournalAccept.Rejected(ArticleAcceptOutcome.RejectedPressure));
            }

            var group = EnsureOpenAcceptGroupUnlocked();
            if (group.Slots.Count > 0
                && (_stagedRecoverableBytes + artSize > _acceptGroupMaxBytes
                    || group.Slots.Count >= _acceptGroupLimit))
            {
                FlushOpenAcceptGroupUnlocked();
                group = EnsureOpenAcceptGroupUnlocked();
            }

            var sequence = _nextSequence;
            var accept = new JournalAcceptRecord(
                version: ArticleJournalFrameCodec.SchemaVersion,
                sequence,
                artId,
                artHash,
                artSize,
                utcNow,
                artData);
            var encoded = ArticleJournalFrameCodec.EncodeAccept(accept);
            try
            {
                AppendGroupedFrameUnlocked(encoded, group.StartOffset);
            }
            catch
            {
                FailOpenAcceptGroupAfterWriteUnlocked(group);
                throw;
            }

            _nextSequence = sequence + 1;
            _stagedRecoverableBytes += artSize;
            _stagedArtIds[artId] = accept;
            var slot = new AcceptGroupSlot(accept, encoded);
            group.Slots.Add(slot);
            var arrivals = Volatile.Read(ref _groupedAcceptArrivals);
            var reachedBound = group.Slots.Count >= _acceptGroupLimit
                || _stagedRecoverableBytes >= _acceptGroupMaxBytes;
            if (reachedBound || arrivals <= 1)
            {
                FlushOpenAcceptGroupUnlocked();
            }
            else if (group.Slots.Count == 1 && _acceptGroupMaxDelay > TimeSpan.Zero)
            {
                ArmAcceptGroupDelayUnlocked(group);
            }

            return slot.Done.Task;
        }
    }

    /// <summary>
    /// Flushes a group left open for a caller that left the arrival section without appending.
    /// </summary>
    private void FlushLingeringAcceptGroup()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            FlushOpenAcceptGroupIfAnyUnlocked();
        }
    }

    private AcceptCommitGroup EnsureOpenAcceptGroupUnlocked()
    {
        if (_openAcceptGroup is not null)
        {
            return _openAcceptGroup;
        }

        _stream.Seek(0, SeekOrigin.End);
        _openAcceptGroup = new AcceptCommitGroup(_stream.Position);
        return _openAcceptGroup;
    }

    private void AppendGroupedFrameUnlocked(byte[] frame, long groupStart)
    {
        TestBeforeFrameAppend?.Invoke();
        _stream.Seek(0, SeekOrigin.End);
        var start = _stream.Position;
        try
        {
            _stream.Write(frame, 0, frame.Length);
            TestAfterWriteBeforeFlush?.Invoke(_stream, start, frame.Length);
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            if (_stream.Length != groupStart)
            {
                TruncateOrBlockUnlocked(groupStart, createdByThisCall: true);
            }

            throw new UnreconciledDurableTailException(
                "Article journal accept group write did not complete.",
                ex,
                createdByThisCall: true);
        }
    }

    private void ArmAcceptGroupDelayUnlocked(AcceptCommitGroup group)
    {
        if (group.DelayArmed)
        {
            return;
        }

        group.DelayArmed = true;
        var delay = _acceptGroupMaxDelay;
        var cancel = new CancellationTokenSource();
        group.DelayCancel = cancel;
        _ = DelayThenFlushAcceptGroupAsync(group, delay, cancel.Token);
    }

    private async Task DelayThenFlushAcceptGroupAsync(
        AcceptCommitGroup group,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            var custom = TestAcceptGroupDelay;
            if (custom is not null)
            {
                await custom(delay, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_openAcceptGroup, group))
                {
                    return;
                }

                FlushOpenAcceptGroupUnlocked();
            }
        }
        catch (OperationCanceledException)
        {
            // The group was flushed or the journal was disposed.
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!_disposed && ReferenceEquals(_openAcceptGroup, group))
                {
                    group.Flushing = true;
                    _openAcceptGroup = null;
                    RememberFailedAcceptGroupUnlocked(group, ex);
                }
            }
        }
        finally
        {
            group.DelayCancel?.Dispose();
            group.DelayCancel = null;
        }
    }

    private void FlushOpenAcceptGroupIfAnyUnlocked()
    {
        if (_openAcceptGroup is not null)
        {
            FlushOpenAcceptGroupUnlocked();
        }
    }

    private void FlushOpenAcceptGroupUnlocked()
    {
        var group = _openAcceptGroup;
        if (group is null || group.Flushing)
        {
            return;
        }

        group.Flushing = true;
        _openAcceptGroup = null;
        group.DelayCancel?.Cancel();
        try
        {
            DurableFlushUnlocked();
        }
        catch (Exception ex)
        {
            RememberFailedAcceptGroupUnlocked(group, ex);
            return;
        }

        foreach (var slot in group.Slots)
        {
            ApplyAcceptUnlocked(slot.Record);
            _ = _stagedArtIds.Remove(slot.Record.ArtId);
            _stagedRecoverableBytes -= slot.Record.ArtSize;
            slot.Frame = null;
            _ = slot.Done.TrySetResult(GroupedJournalAccept.Applied(slot.Record));
        }

        if (_stagedRecoverableBytes < 0)
        {
            _stagedRecoverableBytes = 0;
        }

        LogPressureIfChangedUnlocked();
    }

    private void RememberFailedAcceptGroupUnlocked(AcceptCommitGroup group, Exception exception)
    {
        _failedAcceptGroup = group;
        _openAcceptGroup = null;
        _stagedRecoverableBytes = 0;
        var wrapped = exception as UnreconciledDurableTailException
            ?? new UnreconciledDurableTailException(
                "Article journal accept group is present but not durable.",
                exception,
                createdByThisCall: true);
        foreach (var slot in group.Slots)
        {
            _ = slot.Done.TrySetException(wrapped);
        }
    }

    private void FailOpenAcceptGroupAfterWriteUnlocked(AcceptCommitGroup group)
    {
        _openAcceptGroup = null;
        group.DelayCancel?.Cancel();
        _stagedRecoverableBytes = 0;
        var failure = new UnreconciledDurableTailException(
            "Article journal accept group write did not complete.",
            new IOException("accept-group-write"),
            createdByThisCall: true);
        foreach (var slot in group.Slots)
        {
            _ = _stagedArtIds.Remove(slot.Record.ArtId);
            _ = slot.Done.TrySetException(failure);
        }
    }

    private bool TryCompleteFailedAcceptGroupUnlocked(
        ArticleId artId,
        ulong artHash,
        int artSize,
        out JournalAcceptRecord record,
        out ArticleAcceptOutcome rejectOutcome)
    {
        var failed = _failedAcceptGroup;
        record = null!;
        rejectOutcome = default;
        if (failed is null)
        {
            return false;
        }

        AcceptGroupSlot? member = null;
        foreach (var slot in failed.Slots)
        {
            if (slot.Record.ArtId != artId)
            {
                continue;
            }

            member = slot;
            break;
        }

        if (member is null)
        {
            return false;
        }

        if (member.Record.ArtHash != artHash || member.Record.ArtSize != artSize)
        {
            record = member.Record;
            rejectOutcome = ArticleAcceptOutcome.Conflict;
            return true;
        }

        if (!FailedGroupBytesMatchUnlocked(failed))
        {
            throw new UnreconciledDurableTailException(
                "Pending journal accept group bytes changed before the durable flush.",
                new IOException("Pending durable payload no longer matches the file."));
        }

        try
        {
            DurableFlushUnlocked();
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            throw new UnreconciledDurableTailException(
                "Article journal accept group is present but not durable.",
                ex,
                createdByThisCall: true);
        }

        foreach (var slot in failed.Slots)
        {
            ApplyAcceptUnlocked(slot.Record);
            _ = _stagedArtIds.Remove(slot.Record.ArtId);
            slot.Frame = null;
        }

        _failedAcceptGroup = null;
        LogPressureIfChangedUnlocked();
        record = member.Record;
        rejectOutcome = default;
        return true;
    }

    private bool FailedGroupBytesMatchUnlocked(AcceptCommitGroup group)
    {
        try
        {
            _stream.Flush(flushToDisk: false);
            var offset = group.StartOffset;
            foreach (var slot in group.Slots)
            {
                var frame = slot.Frame;
                if (frame is null || _stream.Length < offset + frame.Length)
                {
                    return false;
                }

                var observed = ReadExactUnlocked(offset, frame.Length);
                if (observed.Length != frame.Length || !observed.AsSpan().SequenceEqual(frame))
                {
                    return false;
                }

                offset += frame.Length;
            }

            return true;
        }
        catch (Exception ex) when (ex is not UnreconciledDurableTailException)
        {
            _tailUnreconciled = true;
            _tailValidEnd = group.StartOffset;
            throw new UnreconciledDurableTailException(
                "Article journal accept group could not be inspected after an ambiguous append.",
                ex,
                createdByThisCall: true);
        }
    }

    private bool TryRejectStagedOrOutstandingUnlocked(
        ArticleId artId,
        ulong artHash,
        int artSize,
        out ArticleAcceptOutcome rejectOutcome)
    {
        foreach (var prepared in _preparedQueue)
        {
            if (prepared.ReservedArtId != artId)
            {
                continue;
            }

            rejectOutcome = prepared.ReservedArtHash == artHash && prepared.ReservedArtSize == artSize
                ? ArticleAcceptOutcome.Duplicate
                : ArticleAcceptOutcome.Conflict;
            return true;
        }

        if (_stagedArtIds.TryGetValue(artId, out var staged))
        {
            rejectOutcome = staged.ArtHash == artHash && staged.ArtSize == artSize
                ? ArticleAcceptOutcome.Duplicate
                : ArticleAcceptOutcome.Conflict;
            return true;
        }

        if (_outstandingArtIdToSequence.TryGetValue(artId, out var existingSeq)
            && _bySequence.TryGetValue(existingSeq, out var existing)
            && !existing.IndexCommitted)
        {
            var retained = existing.IncompleteAccept;
            rejectOutcome = retained.ArtHash == artHash && retained.ArtSize == artSize
                ? ArticleAcceptOutcome.Duplicate
                : ArticleAcceptOutcome.Conflict;
            return true;
        }

        rejectOutcome = default;
        return false;
    }

    private void FailAcceptGroupsForDisposeUnlocked()
    {
        var open = _openAcceptGroup;
        _openAcceptGroup = null;
        if (open is not null)
        {
            open.DelayCancel?.Cancel();
            var disposed = new ObjectDisposedException(nameof(FileArticleJournal));
            foreach (var slot in open.Slots)
            {
                _ = slot.Done.TrySetException(disposed);
            }
        }

        _failedAcceptGroup = null;
        var preparedDisposed = new ObjectDisposedException(nameof(FileArticleJournal));
        while (_preparedQueue.Count > 0)
        {
            var waiting = _preparedQueue.Dequeue();
            _ = waiting.Done.TrySetException(preparedDisposed);
        }

        _stagedRecoverableBytes = 0;
        _stagedArtIds.Clear();
    }

    /// <summary>One open or failed Accept durability group. Not restored after restart.</summary>
    private sealed class AcceptCommitGroup
    {
        public AcceptCommitGroup(long startOffset)
        {
            StartOffset = startOffset;
        }

        public long StartOffset { get; }

        public List<AcceptGroupSlot> Slots { get; } = new();

        public bool Flushing { get; set; }

        public bool DelayArmed { get; set; }

        public CancellationTokenSource? DelayCancel { get; set; }
    }

    /// <summary>One staged Accept whose result waits on the group flush.</summary>
    private sealed class AcceptGroupSlot
    {
        public AcceptGroupSlot(JournalAcceptRecord record, byte[] frame)
        {
            _record = record;
            ReservedArtId = record.ArtId;
            ReservedArtHash = record.ArtHash;
            ReservedArtSize = record.ArtSize;
            Sequence = record.Sequence;
            Frame = frame;
            Done = new TaskCompletionSource<GroupedJournalAccept>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public AcceptGroupSlot(ulong sequence, ArticleId artId, ulong artHash, int artSize)
        {
            Sequence = sequence;
            ReservedArtId = artId;
            ReservedArtHash = artHash;
            ReservedArtSize = artSize;
            Done = new TaskCompletionSource<GroupedJournalAccept>(TaskCreationOptions.RunContinuationsAsynchronously);
            State = PreparedAcceptState.Preparing;
        }

        private JournalAcceptRecord? _record;

        public JournalAcceptRecord Record => _record ?? throw new InvalidOperationException("Prepared accept has no record.");

        public bool HasRecord => _record is not null;

        public ulong Sequence { get; }

        public ArticleId ReservedArtId { get; }

        public ulong ReservedArtHash { get; }

        public int ReservedArtSize { get; }

        public byte[]? Frame { get; set; }

        public TaskCompletionSource<GroupedJournalAccept> Done { get; }

        public PreparedAcceptState State { get; set; } = PreparedAcceptState.InGroup;

        public void AttachRecord(JournalAcceptRecord record)
        {
            if (record.Sequence != Sequence || record.ArtId != ReservedArtId)
            {
                throw new InvalidOperationException("Prepared accept record does not match the reservation.");
            }

            _record = record;
        }
    }

    /// <summary>Where a reserved Accept sits relative to the single journal writer.</summary>
    private enum PreparedAcceptState
    {
        /// <summary>Sequence is reserved and the frame is not ready.</summary>
        Preparing = 0,

        /// <summary>Frame is ready and must wait until every earlier sequence is written or abandoned.</summary>
        Ready = 1,

        /// <summary>Frame was appended to the open durability group.</summary>
        InGroup = 2,

        /// <summary>No journal frame will be written for this sequence.</summary>
        Abandoned = 3,
    }
}

/// <summary>Result of a grouped journal Accept after durability, or a rejection that did not write.</summary>
internal readonly struct GroupedJournalAccept
{
    private GroupedJournalAccept(bool appended, JournalAcceptRecord? record, ArticleAcceptOutcome rejectOutcome)
    {
        Appended = appended;
        Record = record;
        RejectOutcome = rejectOutcome;
    }

    /// <summary>True when the Accept frame is durable and applied.</summary>
    internal bool Appended { get; }

    /// <summary>Applied record when <see cref="Appended"/> is true.</summary>
    internal JournalAcceptRecord? Record { get; }

    /// <summary>Rejection when <see cref="Appended"/> is false.</summary>
    internal ArticleAcceptOutcome RejectOutcome { get; }

    internal static GroupedJournalAccept Applied(JournalAcceptRecord record) =>
        new(true, record, default);

    internal static GroupedJournalAccept Rejected(ArticleAcceptOutcome outcome) =>
        new(false, null, outcome);
}
