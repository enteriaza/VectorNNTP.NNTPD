using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Storage.Engine.FileJournal;

public sealed partial class FileArticleJournal
{
    /// <summary>
    /// Replaces the journal with a checkpoint image. The base image is encoded and flushed
    /// outside <see cref="_gate"/>. Cutover appends only the records that landed after the
    /// snapshot, then installs the temp file.
    /// </summary>
    private long CheckpointReplaceCommitted(
        CheckpointCapacityReservation? capacity,
        out ulong[] omittedSequences,
        out ulong[] omittedCompactionIds)
    {
        omittedSequences = [];
        omittedCompactionIds = [];
        TestBeforeCheckpointSnapshot?.Invoke();
        CheckpointAttempt? attempt = null;
        var installed = false;
        try
        {
            attempt = CaptureCheckpointAttempt();
            if (attempt is null)
            {
                return 0;
            }

            var image = MaterializeCheckpointImage(attempt);
            if (capacity is not null)
            {
                attempt.ReservationId = capacity.TryReserve(image.Length);
                if (attempt.ReservationId is null)
                {
                    throw new CheckpointCapacityDeniedException(image.Length);
                }

                attempt.ReservedBytes = image.Length;
            }

            attempt.TempPath = WriteDurableCheckpointTemp(image);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ThrowIfPendingJournalAppendUnlocked();
                if (!CompactionPlanMatchesUnlocked(attempt))
                {
                    // Another caller can append compaction frames while the base image is
                    // encoded outside _gate. Retrying here would spin for as long as that
                    // writer keeps moving. Leave the live journal unchanged and let the
                    // maintenance cycle try again.
                    throw new CheckpointCompactionChangedException(
                        "Compaction state changed during checkpoint; the replacement was not installed.");
                }

                var suffix = BuildCutoverSuffixUnlocked(attempt);
                if (suffix.Length > 0)
                {
                    if (capacity is not null)
                    {
                        var total = attempt.ReservedBytes + suffix.Length;
                        if (!capacity.TryIncrease(attempt.ReservationId!.Value, suffix.Length))
                        {
                            throw new CheckpointCapacityDeniedException(total);
                        }

                        attempt.ReservedBytes = total;
                    }

                    AppendDurableCheckpointTemp(attempt.TempPath, suffix);
                }

                var before = _stream.Length;
                var tempPath = attempt.TempPath;
                attempt.TempPath = null;
                InstallFlushedCheckpointTempUnlocked(tempPath);
                var omitted = CollectOmittedSequencesUnlocked(attempt);
                PublishCheckpointMemoryUnlocked(omitted, attempt.OmittedCompactionIds);
                omittedSequences = omitted;
                omittedCompactionIds = attempt.OmittedCompactionIds;
                installed = true;
                return LogCheckpointUnlocked(before);
            }
        }
        finally
        {
            attempt?.Unpin();
            if (!installed && attempt?.TempPath is { } leftover && !TryDeleteCheckpointTemp(leftover))
            {
                _retainCheckpointTempReservation = true;
            }

            if (!_retainCheckpointTempReservation && attempt?.ReservationId is ulong reservationId && capacity is not null)
            {
                capacity.Release(reservationId);
            }

            _retainCheckpointTempReservation = false;
        }
    }

    private CheckpointAttempt? CaptureCheckpointAttempt()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfPendingJournalAppendUnlocked();
            if (!TryCollectCheckpointPlanUnlocked(
                    out var incomplete,
                    out _,
                    out var omittedCompactions))
            {
                return null;
            }

            var pinned = new List<CheckpointPinnedSequence>(incomplete.Length);
            try
            {
                foreach (var state in incomplete)
                {
                    var accept = state.IncompleteAccept;
                    accept.PinForCheckpoint();
                    pinned.Add(new CheckpointPinnedSequence(accept, state.PhysicalWritten));
                }
            }
            catch
            {
                foreach (var item in pinned)
                {
                    item.Accept.UnpinForCheckpoint();
                }

                throw;
            }

            Array.Sort(omittedCompactions);
            var omittedSet = new HashSet<ulong>(omittedCompactions);
            var compactions = new List<CompactionJournalSnapshot>();
            foreach (var compaction in _compactions.Values.OrderBy(static item => item.Begin.CompactionId))
            {
                if (omittedSet.Contains(compaction.Begin.CompactionId))
                {
                    continue;
                }

                compactions.Add(compaction.ToSnapshot());
            }

            return new CheckpointAttempt(
                pinned,
                omittedCompactions,
                compactions.ToArray(),
                _nextSequence,
                _nextSegmentId);
        }
    }

    private byte[] MaterializeCheckpointImage(CheckpointAttempt attempt)
    {
        TestWhileCheckpointEncoding?.Invoke();
        using var buffer = new MemoryStream();
        var sequenceFence = ArticleJournalFrameCodec.EncodeSequenceFence(attempt.NextSequence);
        buffer.Write(sequenceFence, 0, sequenceFence.Length);
        if (attempt.NextSegmentId > 0)
        {
            var segmentFence = ArticleJournalFrameCodec.EncodeSegmentIdFence(attempt.NextSegmentId);
            buffer.Write(segmentFence, 0, segmentFence.Length);
        }

        foreach (var item in attempt.Pinned)
        {
            // Pin is held by this thread until the attempt is released. DetachPayload waits
            // on that pin, so EncodeAccept cannot observe a cleared buffer.
            _ = item.Accept.CheckpointPinnedArtData();
            var acceptFrame = ArticleJournalFrameCodec.EncodeAccept(item.Accept);
            buffer.Write(acceptFrame, 0, acceptFrame.Length);
            if (item.PhysicalWritten is { } physicalWritten)
            {
                var physicalFrame = ArticleJournalFrameCodec.EncodePhysicalWritten(physicalWritten);
                buffer.Write(physicalFrame, 0, physicalFrame.Length);
            }
        }

        WriteCompactionSnapshots(buffer, attempt.Compactions);
        return buffer.ToArray();
    }

    private static void WriteCompactionSnapshots(Stream destination, CompactionJournalSnapshot[] compactions)
    {
        foreach (var compaction in compactions)
        {
            var begin = ArticleJournalFrameCodec.EncodeCompactionBegin(compaction.Begin);
            destination.Write(begin, 0, begin.Length);
            foreach (var relocation in compaction.Relocations)
            {
                var intent = ArticleJournalFrameCodec.EncodeRelocationIntent(relocation.Intent);
                destination.Write(intent, 0, intent.Length);
                if (relocation.Written is { } written)
                {
                    var writtenFrame = ArticleJournalFrameCodec.EncodeRelocationWritten(written);
                    destination.Write(writtenFrame, 0, writtenFrame.Length);
                }
            }

            if (compaction.Committed && compaction.CommittedRecord is { } committed)
            {
                var committedFrame = ArticleJournalFrameCodec.EncodeCompactionCommitted(committed);
                destination.Write(committedFrame, 0, committedFrame.Length);
            }

            if (compaction.Retired is { } retired)
            {
                var retiredFrame = ArticleJournalFrameCodec.EncodeCompactionRetired(retired);
                destination.Write(retiredFrame, 0, retiredFrame.Length);
            }
        }
    }

    private bool CompactionPlanMatchesUnlocked(CheckpointAttempt attempt)
    {
        var omitted = SelectOmittedRetiredCompactionsUnlocked();
        Array.Sort(omitted);
        if (!omitted.AsSpan().SequenceEqual(attempt.OmittedCompactionIds))
        {
            return false;
        }

        var liveCount = 0;
        foreach (var compaction in _compactions.Values.OrderBy(static item => item.Begin.CompactionId))
        {
            if (attempt.OmittedCompactions.Contains(compaction.Begin.CompactionId))
            {
                continue;
            }

            if (liveCount >= attempt.Compactions.Length
                || !CompactionSnapshotsEqual(compaction.ToSnapshot(), attempt.Compactions[liveCount]))
            {
                return false;
            }

            liveCount++;
        }

        return liveCount == attempt.Compactions.Length;
    }

    private static bool CompactionSnapshotsEqual(CompactionJournalSnapshot left, CompactionJournalSnapshot right)
    {
        if (left.Begin != right.Begin
            || left.Committed != right.Committed
            || left.CommittedRecord != right.CommittedRecord
            || left.Retired != right.Retired
            || left.Relocations.Count != right.Relocations.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Relocations.Count; i++)
        {
            if (left.Relocations[i].Intent != right.Relocations[i].Intent
                || left.Relocations[i].Written != right.Relocations[i].Written)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Frames that became durable after the snapshot and must be in the replacement.
    /// A snapshotted Accept that later reached IndexCommitted stays in the base image.
    /// A PhysicalWritten that landed after the snapshot is appended even if IndexCommitted followed.
    /// </summary>
    private byte[] BuildCutoverSuffixUnlocked(CheckpointAttempt attempt)
    {
        var frames = new List<byte[]>();
        foreach (var state in _bySequence.Values.OrderBy(static item => item.Sequence))
        {
            if (attempt.PinnedBySequence.TryGetValue(state.Sequence, out var pinned))
            {
                if (state.PhysicalWritten is not { } liveWritten)
                {
                    continue;
                }

                if (pinned.PhysicalWritten is null)
                {
                    frames.Add(ArticleJournalFrameCodec.EncodePhysicalWritten(liveWritten));
                    continue;
                }

                if (!LocationsEqual(pinned.PhysicalWritten.Value.Location, liveWritten.Location))
                {
                    throw new InvalidOperationException(
                        $"PhysicalWritten for sequence {state.Sequence} changed during checkpoint.");
                }

                continue;
            }

            if (state.IndexCommitted)
            {
                continue;
            }

            frames.Add(ArticleJournalFrameCodec.EncodeAccept(state.IncompleteAccept));
            if (state.PhysicalWritten is { } physicalWritten)
            {
                frames.Add(ArticleJournalFrameCodec.EncodePhysicalWritten(physicalWritten));
            }
        }

        if (_nextSequence > attempt.NextSequence)
        {
            frames.Add(ArticleJournalFrameCodec.EncodeSequenceFence(_nextSequence));
        }

        if (_nextSegmentId > attempt.NextSegmentId)
        {
            frames.Add(ArticleJournalFrameCodec.EncodeSegmentIdFence(_nextSegmentId));
        }

        return frames.Count == 0 ? [] : ConcatFrames(frames);
    }

    private ulong[] CollectOmittedSequencesUnlocked(CheckpointAttempt attempt)
    {
        var omitted = new List<ulong>();
        foreach (var pair in _bySequence)
        {
            if (!pair.Value.IndexCommitted || attempt.PinnedBySequence.ContainsKey(pair.Key))
            {
                continue;
            }

            omitted.Add(pair.Key);
        }

        omitted.Sort();
        return omitted.ToArray();
    }

    private string WriteDurableCheckpointTemp(byte[] image)
    {
        var directory = Path.GetDirectoryName(_journalPath)
            ?? throw new InvalidOperationException("Journal path has no directory.");
        var tempPath = Path.Combine(directory, $".{JournalFileName}.{Guid.NewGuid():N}.tmp");
        using var temp = OpenCheckpointTemp(tempPath, FileMode.CreateNew);
        temp.Write(image, 0, image.Length);
        temp.Flush(flushToDisk: true);
        return tempPath;
    }

    private static void AppendDurableCheckpointTemp(string tempPath, byte[] suffix)
    {
        using var temp = OpenCheckpointTemp(tempPath, FileMode.Append);
        temp.Write(suffix, 0, suffix.Length);
        temp.Flush(flushToDisk: true);
    }

    private static FileStream OpenCheckpointTemp(string path, FileMode mode) =>
        new(path, mode, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, FileOptions.None);

    private sealed class CheckpointPinnedSequence
    {
        public CheckpointPinnedSequence(JournalAcceptRecord accept, JournalPhysicalWrittenRecord? physicalWritten)
        {
            Accept = accept;
            PhysicalWritten = physicalWritten;
        }

        public JournalAcceptRecord Accept { get; }

        public JournalPhysicalWrittenRecord? PhysicalWritten { get; }
    }

    private sealed class CheckpointAttempt
    {
        private int _unpinned;

        public CheckpointAttempt(
            List<CheckpointPinnedSequence> pinned,
            ulong[] omittedCompactionIds,
            CompactionJournalSnapshot[] compactions,
            ulong nextSequence,
            ulong nextSegmentId)
        {
            Pinned = pinned;
            OmittedCompactionIds = omittedCompactionIds;
            OmittedCompactions = new HashSet<ulong>(omittedCompactionIds);
            Compactions = compactions;
            NextSequence = nextSequence;
            NextSegmentId = nextSegmentId;
            PinnedBySequence = new Dictionary<ulong, CheckpointPinnedSequence>(pinned.Count);
            foreach (var item in pinned)
            {
                PinnedBySequence.Add(item.Accept.Sequence, item);
            }
        }

        public List<CheckpointPinnedSequence> Pinned { get; }

        public Dictionary<ulong, CheckpointPinnedSequence> PinnedBySequence { get; }

        public ulong[] OmittedCompactionIds { get; }

        public HashSet<ulong> OmittedCompactions { get; }

        public CompactionJournalSnapshot[] Compactions { get; }

        public ulong NextSequence { get; }

        public ulong NextSegmentId { get; }

        public ulong? ReservationId { get; set; }

        public long ReservedBytes { get; set; }

        public string? TempPath { get; set; }

        public void Unpin()
        {
            if (Interlocked.Exchange(ref _unpinned, 1) != 0)
            {
                return;
            }

            foreach (var item in Pinned)
            {
                item.Accept.UnpinForCheckpoint();
            }
        }
    }
}
