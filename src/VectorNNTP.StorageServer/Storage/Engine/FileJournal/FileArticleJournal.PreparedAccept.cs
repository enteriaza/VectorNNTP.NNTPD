using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Storage.Engine.FileJournal;

public sealed partial class FileArticleJournal
{
    /// <summary>
    /// When true, Accept reserves a journal sequence, encodes the frame outside
    /// <see cref="_gate"/>, then the same lock appends ready frames in sequence order.
    /// One group <see cref="FileStream.Flush(bool)"/> still completes the group.
    /// The getter does not take <see cref="_gate"/>.
    /// </summary>
    internal bool AcceptParallelPreparation
    {
        get => Volatile.Read(ref _acceptParallelPreparation) != 0;
        set => Volatile.Write(ref _acceptParallelPreparation, value ? 1 : 0);
    }

    /// <summary>Accept records reserved or written and not yet durability-flushed.</summary>
    internal int PreparedAcceptCount
    {
        get
        {
            lock (_gate)
            {
                return _preparedQueue.Count + (_openAcceptGroup?.Slots.Count ?? 0);
            }
        }
    }

    /// <summary>Reserved Accept records whose frames are ready but not yet appended.</summary>
    internal int PreparedReadyCount
    {
        get
        {
            lock (_gate)
            {
                var ready = 0;
                foreach (var slot in _preparedQueue)
                {
                    if (slot.State == PreparedAcceptState.Ready)
                    {
                        ready++;
                    }
                }

                return ready;
            }
        }
    }

    /// <summary>Highest number of reserved Accepts waiting to be appended.</summary>
    internal int PreparedQueueHighWater
    {
        get
        {
            lock (_gate)
            {
                return _preparedQueueHighWater;
            }
        }
    }

    /// <summary>Highest ArtSize reserved for preparation and not yet flushed, in bytes.</summary>
    internal long PreparedPayloadHighWater
    {
        get
        {
            lock (_gate)
            {
                return _preparedPayloadHighWater;
            }
        }
    }

    /// <summary>
    /// Invoked after a sequence is reserved and before <see cref="ArticleJournalFrameCodec.EncodeAccept"/>.
    /// Tests only. The production path is null. Runs outside <see cref="_gate"/>.
    /// </summary>
    internal Func<ulong, CancellationToken, Task>? TestPreparedEncodeGate { get; set; }

    private int _acceptParallelPreparation;
    private int _preparedQueueHighWater;
    private long _preparedPayloadHighWater;
    private readonly Queue<AcceptGroupSlot> _preparedQueue = new();

    /// <summary>Runs <see cref="TestPreparedEncodeGate"/> outside the journal lock.</summary>
    internal Task InvokePreparedEncodeGateAsync(ulong sequence, CancellationToken cancellationToken)
    {
        var gate = TestPreparedEncodeGate;
        return gate is null ? Task.CompletedTask : gate(sequence, cancellationToken);
    }

    /// <summary>
    /// Reserves the next journal sequence without writing. The caller encodes outside the lock,
    /// then <see cref="PublishPreparedAccept"/> or <see cref="AbandonPreparedAccept"/>.
    /// </summary>
    internal PreparedAcceptReservation TryReservePreparedAccept(
        ArticleId artId,
        ulong artHash,
        int artSize,
        DateTimeOffset utcNow,
        ReadOnlyMemory<byte> artData)
    {
        if (artSize != artData.Length || artSize is < 1 or > ArticleResourceLimits.MaxArticleBytes)
        {
            return PreparedAcceptReservation.Reject(ArticleAcceptOutcome.RejectedInvalid);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending is not null || _pendingBatch is not null || _tailUnreconciled)
            {
                throw new UnreconciledDurableTailException(
                    "Article journal accept cannot be reserved while another append is pending.",
                    new IOException("pending-journal-frame"));
            }

            if (_failedAcceptGroup is not null)
            {
                if (!TryCompleteFailedAcceptGroupUnlocked(artId, artHash, artSize, out var recovered, out var failedReject))
                {
                    throw PendingOwnedByOther();
                }

                return failedReject == default
                    ? PreparedAcceptReservation.Durable(recovered)
                    : PreparedAcceptReservation.Reject(failedReject);
            }

            if (TryRejectStagedOrOutstandingUnlocked(artId, artHash, artSize, out var reject))
            {
                return PreparedAcceptReservation.Reject(reject);
            }

            if (ComputePressureUnlocked() == StorageWritePressure.Critical)
            {
                return PreparedAcceptReservation.Reject(ArticleAcceptOutcome.RejectedPressure);
            }

            var sequence = _nextSequence;
            _nextSequence = sequence + 1;
            var slot = new AcceptGroupSlot(sequence, artId, artHash, artSize);
            _preparedQueue.Enqueue(slot);
            if (_preparedQueue.Count > _preparedQueueHighWater)
            {
                _preparedQueueHighWater = _preparedQueue.Count;
            }

            _stagedRecoverableBytes += artSize;
            if (_stagedRecoverableBytes > _preparedPayloadHighWater)
            {
                _preparedPayloadHighWater = _stagedRecoverableBytes;
            }

            return PreparedAcceptReservation.Reserve(sequence, utcNow, slot.Done.Task);
        }
    }

    /// <summary>
    /// Hands an encoded frame to the single writer. Later sequences stay queued until this
    /// sequence has been appended or abandoned.
    /// </summary>
    internal void PublishPreparedAccept(ulong sequence, JournalAcceptRecord record, byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var slot = FindPreparedSlotUnlocked(sequence);
            if (slot.State != PreparedAcceptState.Preparing || slot.Frame is not null)
            {
                throw new InvalidOperationException($"Prepared accept {sequence} is not waiting for a frame.");
            }

            slot.AttachRecord(record);
            _stagedArtIds[record.ArtId] = record;
            slot.Frame = frame;
            slot.State = PreparedAcceptState.Ready;
            DrainPreparedAcceptsUnlocked();
        }
    }

    /// <summary>
    /// Drops a reserved sequence without writing a journal frame. Later sequences may then append.
    /// </summary>
    internal void AbandonPreparedAccept(ulong sequence)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            AcceptGroupSlot? slot = null;
            foreach (var queued in _preparedQueue)
            {
                if (queued.Sequence == sequence)
                {
                    slot = queued;
                    break;
                }
            }

            if (slot is null || slot.State is PreparedAcceptState.Abandoned or PreparedAcceptState.InGroup)
            {
                return;
            }

            slot.State = PreparedAcceptState.Abandoned;
            slot.Frame = null;
            DrainPreparedAcceptsUnlocked();
        }
    }

    private AcceptGroupSlot FindPreparedSlotUnlocked(ulong sequence)
    {
        foreach (var slot in _preparedQueue)
        {
            if (slot.Sequence == sequence)
            {
                return slot;
            }
        }

        throw new InvalidOperationException($"Prepared accept {sequence} is not queued.");
    }

    private void DrainPreparedAcceptsUnlocked()
    {
        while (_preparedQueue.Count > 0)
        {
            var head = _preparedQueue.Peek();
            if (head.State == PreparedAcceptState.Preparing)
            {
                return;
            }

            _ = _preparedQueue.Dequeue();
            if (head.State == PreparedAcceptState.Abandoned)
            {
                ReleasePreparedReservationUnlocked(head);
                _ = head.Done.TrySetResult(GroupedJournalAccept.Rejected(ArticleAcceptOutcome.RejectedInvalid));
                continue;
            }

            if (head.Frame is null)
            {
                ReleasePreparedReservationUnlocked(head);
                _ = head.Done.TrySetException(new InvalidOperationException("Prepared accept frame is missing."));
                continue;
            }

            try
            {
                AppendPreparedFrameUnlocked(head);
            }
            catch (Exception ex)
            {
                if (_openAcceptGroup is not null)
                {
                    FailOpenAcceptGroupAfterWriteUnlocked(_openAcceptGroup);
                }

                if (head.State != PreparedAcceptState.InGroup)
                {
                    ReleasePreparedReservationUnlocked(head);
                    _ = head.Done.TrySetException(ex);
                }

                FailRemainingPreparedQueueUnlocked(ex);
                throw;
            }
        }
    }

    private void AppendPreparedFrameUnlocked(AcceptGroupSlot slot)
    {
        var frame = slot.Frame ?? throw new InvalidOperationException("Prepared accept frame is missing.");
        var group = EnsureOpenAcceptGroupUnlocked();
        var groupBytes = OpenGroupArtBytesUnlocked(group);
        if (group.Slots.Count > 0
            && (groupBytes + slot.ReservedArtSize > _acceptGroupMaxBytes
                || group.Slots.Count >= _acceptGroupLimit))
        {
            FlushOpenAcceptGroupUnlocked();
            group = EnsureOpenAcceptGroupUnlocked();
            groupBytes = 0;
        }

        AppendGroupedFrameUnlocked(frame, group.StartOffset);
        slot.State = PreparedAcceptState.InGroup;
        group.Slots.Add(slot);
        groupBytes += slot.ReservedArtSize;
        var full = group.Slots.Count >= _acceptGroupLimit
            || groupBytes >= _acceptGroupMaxBytes
            || _acceptGroupMaxDelay <= TimeSpan.Zero;
        if (full)
        {
            FlushOpenAcceptGroupUnlocked();
        }
        else if (group.Slots.Count == 1)
        {
            ArmAcceptGroupDelayUnlocked(group);
        }
    }

    private static long OpenGroupArtBytesUnlocked(AcceptCommitGroup group)
    {
        long bytes = 0;
        foreach (var slot in group.Slots)
        {
            bytes += slot.Record.ArtSize;
        }

        return bytes;
    }

    private void ReleasePreparedReservationUnlocked(AcceptGroupSlot slot)
    {
        _stagedRecoverableBytes = Math.Max(0, _stagedRecoverableBytes - slot.ReservedArtSize);
        _ = _stagedArtIds.Remove(slot.ReservedArtId);
        slot.State = PreparedAcceptState.Abandoned;
    }

    private void FailRemainingPreparedQueueUnlocked(Exception exception)
    {
        while (_preparedQueue.Count > 0)
        {
            var slot = _preparedQueue.Dequeue();
            ReleasePreparedReservationUnlocked(slot);
            _ = slot.Done.TrySetException(exception);
        }
    }
}

/// <summary>A reserved journal sequence, or a rejection that reserved nothing.</summary>
internal readonly struct PreparedAcceptReservation
{
    private PreparedAcceptReservation(
        bool reserved,
        bool alreadyDurable,
        ulong sequence,
        DateTimeOffset acceptedUtc,
        Task<GroupedJournalAccept>? durability,
        JournalAcceptRecord? record,
        ArticleAcceptOutcome rejectOutcome)
    {
        Reserved = reserved;
        AlreadyDurable = alreadyDurable;
        Sequence = sequence;
        AcceptedUtc = acceptedUtc;
        Durability = durability;
        Record = record;
        RejectOutcome = rejectOutcome;
    }

    /// <summary>True when the caller must encode and then publish or abandon.</summary>
    internal bool Reserved { get; }

    /// <summary>True when a previous failed flush is now durable and <see cref="Record"/> is applied.</summary>
    internal bool AlreadyDurable { get; }

    /// <summary>Reserved sequence. Zero when nothing was reserved.</summary>
    internal ulong Sequence { get; }

    /// <summary>UTC timestamp captured with the sequence reservation.</summary>
    internal DateTimeOffset AcceptedUtc { get; }

    /// <summary>Completes after the group durability flush. Null when nothing was reserved.</summary>
    internal Task<GroupedJournalAccept>? Durability { get; }

    /// <summary>Applied record when <see cref="AlreadyDurable"/> is true.</summary>
    internal JournalAcceptRecord? Record { get; }

    /// <summary>Rejection when <see cref="Reserved"/> and <see cref="AlreadyDurable"/> are false.</summary>
    internal ArticleAcceptOutcome RejectOutcome { get; }

    internal static PreparedAcceptReservation Reserve(
        ulong sequence,
        DateTimeOffset acceptedUtc,
        Task<GroupedJournalAccept> durability) =>
        new(true, false, sequence, acceptedUtc, durability, null, default);

    internal static PreparedAcceptReservation Reject(ArticleAcceptOutcome outcome) =>
        new(false, false, 0, default, null, null, outcome);

    internal static PreparedAcceptReservation Durable(JournalAcceptRecord record) =>
        new(false, true, record.Sequence, record.AcceptedUtc, null, record, default);
}
