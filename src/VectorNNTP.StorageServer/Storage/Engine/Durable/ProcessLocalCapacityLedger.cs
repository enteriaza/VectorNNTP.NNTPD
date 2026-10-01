using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>Physical compaction-journal frame reserved on the control ledger.</summary>
internal enum CompactionJournalFrameKind : byte
{
    /// <summary>CompactionBegin.</summary>
    Begin = 1,

    /// <summary>RelocationIntent.</summary>
    Intent = 2,

    /// <summary>RelocationWritten.</summary>
    Written = 3,

    /// <summary>CompactionCommitted.</summary>
    Committed = 4,

    /// <summary>CompactionRetired.</summary>
    Retired = 5,
}

/// <summary>
/// Identity of one durable compaction-journal frame.
/// RelocationId is zero for Begin, Committed, and Retired.
/// </summary>
internal readonly record struct CompactionJournalFrameKey(
    ulong CompactionId,
    CompactionJournalFrameKind Kind,
    ulong RelocationId);

/// <summary>
/// Process-local byte reservation for article Accept, compaction destination appends,
/// and checkpoint temporary files (Phase 5E.1 / 5E.2).
/// </summary>
/// <remarks>
/// Not a kernel or cross-process reservation. Callers must serialize mutate methods with the
/// owning <see cref="CapacityVolume"/> lock. Shared formula uses
/// <c>Used + ArticleReserved + JournalReserved + IndexReserved + CompactionReserved + CompactionJournalReserved + CheckpointReserved + Required</c>
/// against a policy ceiling. The ledger itself starts empty; the engine reconstructs journal and
/// index reservations from durable files after open.
/// </remarks>
internal sealed class ProcessLocalCapacityLedger
{
    private long _articleReservedBytes;
    private long _journalReservedBytes;
    private long _indexReservedBytes;
    private long _indexDirectUnboundBytes;
    private long _compactionReservedBytes;
    private long _compactionJournalReservedBytes;
    private long _checkpointReservedBytes;
    private ulong _nextCheckpointReservationId;
    private readonly Dictionary<ulong, ArticleSegmentReservation> _articleBySequence = new();
    private readonly Dictionary<ulong, long> _journalBySequence = new();
    private readonly Dictionary<ulong, long> _indexUnboundBySequence = new();
    private readonly List<IndexPresentReservation> _indexFrames = new();
    private readonly Dictionary<(ulong CompactionId, ulong RelocationId), CompactionDestinationHold> _compactionByKey = new();
    private readonly Dictionary<CompactionJournalFrameKey, long> _compactionJournalFrames = new();
    private readonly Dictionary<ulong, long> _checkpointById = new();

    /// <summary>
    /// Bytes reserved for physical segment copies on this ledger.
    /// One Accept may hold several copies when retirement-seal retries append again.
    /// </summary>
    public long ArticleReservedBytes => _articleReservedBytes;

    /// <summary>
    /// Bytes reserved for durable journal sequences still present before checkpoint retirement.
    /// Each sequence is <c>ArtSize + 132</c>.
    /// </summary>
    public long JournalReservedBytes => _journalReservedBytes;

    /// <summary>Number of durable journal sequences holding a reservation.</summary>
    public int JournalReservationCount => _journalBySequence.Count;

    /// <summary>
    /// Bytes reserved for physical index frames and for frames accepted but not yet appended.
    /// Each Present, Evicted, Invalid, or relocation frame reserves one index record on the control ledger.
    /// </summary>
    public long IndexReservedBytes => _indexReservedBytes;

    /// <summary>Physical index frames currently reserved, excluding not-yet-appended holds.</summary>
    public int IndexFrameCount => _indexFrames.Count;

    /// <summary>Accept sequences that hold an index-frame reservation before their Present frame is appended.</summary>
    public int IndexUnboundCount => _indexUnboundBySequence.Count;

    /// <summary>Bytes reserved for outstanding compaction destination appends.</summary>
    public long CompactionReservedBytes => _compactionReservedBytes;

    /// <summary>
    /// Bytes reserved for durable compaction-journal frames still present before a checkpoint
    /// omits that compaction. Distinct from <see cref="CompactionReservedBytes"/>.
    /// </summary>
    public long CompactionJournalReservedBytes => _compactionJournalReservedBytes;

    /// <summary>Durable compaction-journal frames currently reserved.</summary>
    public int CompactionJournalFrameCount => _compactionJournalFrames.Count;

    /// <summary>Bytes reserved for checkpoint temporary files that are not yet replaced.</summary>
    public long CheckpointReservedBytes => _checkpointReservedBytes;

    /// <summary>
    /// Total process-local reserved bytes (article + journal + index + compaction + compaction journal + checkpoint).
    /// </summary>
    public long ReservedBytes =>
        checked(
            _articleReservedBytes
            + _journalReservedBytes
            + _indexReservedBytes
            + _compactionReservedBytes
            + _compactionJournalReservedBytes
            + _checkpointReservedBytes);

    /// <summary>Number of Accept sequences currently holding an article reservation.</summary>
    public int ArticleReservationCount => _articleBySequence.Count;

    /// <summary>Number of physical segment-copy reservations across all sequences.</summary>
    public int SegmentCopyCount
    {
        get
        {
            var count = 0;
            foreach (var reservation in _articleBySequence.Values)
            {
                count += reservation.ReservedCopies;
            }

            return count;
        }
    }

    /// <summary>Number of compaction relocation keys currently holding a reservation.</summary>
    public int CompactionReservationCount => _compactionByKey.Count;

    /// <summary>Number of checkpoint temporary allocations currently reserved.</summary>
    public int CheckpointReservationCount => _checkpointById.Count;

    /// <summary>Alias for <see cref="ArticleReservationCount"/> (Phase 5E.1 tests).</summary>
    public int ReservationCount => ArticleReservationCount;

    /// <summary>Scaled integer factor shared by ceiling / WouldFit arithmetic (no floating multiply).</summary>
    internal const long UtilizationScale = 1_000_000L;

    /// <summary>
    /// Returns whether <paramref name="requiredBytes"/> fits under
    /// <c>(used + articleReserved + compactionReserved + checkpointReserved + required) ≤ ceilingUtilization × total</c>
    /// using this ledger's current reservation counters.
    /// </summary>
    public bool WouldFit(
        long usedBytes,
        long totalBytes,
        long requiredBytes,
        double ceilingUtilization) =>
        WouldFit(
            usedBytes,
            _articleReservedBytes,
            _compactionReservedBytes,
            totalBytes,
            requiredBytes,
            ceilingUtilization,
            _checkpointReservedBytes,
            _journalReservedBytes,
            _indexReservedBytes,
            _compactionJournalReservedBytes);

    /// <summary>
    /// Returns whether <paramref name="requiredBytes"/> fits under
    /// <c>(used + articleReserved + journalReserved + indexReserved + compactionReserved + compactionJournalReserved + checkpointReserved + required) ≤ ceilingUtilization × total</c>.
    /// </summary>
    public static bool WouldFit(
        long usedBytes,
        long articleReservedBytes,
        long compactionReservedBytes,
        long totalBytes,
        long requiredBytes,
        double ceilingUtilization,
        long checkpointReservedBytes = 0,
        long journalReservedBytes = 0,
        long indexReservedBytes = 0,
        long compactionJournalReservedBytes = 0)
    {
        if (requiredBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        }

        if (checkpointReservedBytes < 0
            || journalReservedBytes < 0
            || indexReservedBytes < 0
            || compactionJournalReservedBytes < 0
            || totalBytes <= 0
            || usedBytes < 0
            || articleReservedBytes < 0
            || compactionReservedBytes < 0)
        {
            return false;
        }

        long projected;
        try
        {
            projected = checked(
                usedBytes
                + articleReservedBytes
                + journalReservedBytes
                + indexReservedBytes
                + compactionReservedBytes
                + compactionJournalReservedBytes
                + checkpointReservedBytes
                + requiredBytes);
        }
        catch (OverflowException)
        {
            return false;
        }

        var utilScaled = ScaleUtilization(ceilingUtilization);
        if (utilScaled <= 0 || utilScaled >= UtilizationScale)
        {
            return false;
        }

        try
        {
            return checked(projected * UtilizationScale) <= checked(totalBytes * utilScaled);
        }
        catch (OverflowException)
        {
            return (decimal)projected <= (decimal)totalBytes * (decimal)ceilingUtilization;
        }
    }

    /// <summary>
    /// Floor ceiling bytes for <paramref name="ceilingUtilization"/> × <paramref name="totalBytes"/>
    /// using the same scaled-integer rounding as
    /// <see cref="WouldFit(long, long, long, long, long, double, long, long, long, long)"/>.
    /// </summary>
    public static long ComputeCeilingBytes(long totalBytes, double ceilingUtilization)
    {
        if (totalBytes <= 0)
        {
            return 0;
        }

        var utilScaled = ScaleUtilization(ceilingUtilization);
        if (utilScaled <= 0 || utilScaled >= UtilizationScale)
        {
            return 0;
        }

        try
        {
            return checked(totalBytes * utilScaled) / UtilizationScale;
        }
        catch (OverflowException)
        {
            return (long)decimal.Floor((decimal)totalBytes * (decimal)ceilingUtilization);
        }
    }

    /// <summary>
    /// Physical <c>UsedBytes</c> that must disappear before a minimum-size article admission can
    /// succeed under <paramref name="maximumUtilization"/>, accounting for outstanding reservations.
    /// Zero when admission already fits.
    /// </summary>
    public static long ComputeAdmissionRecoveryTargetBytes(
        long usedBytes,
        long articleReservedBytes,
        long compactionReservedBytes,
        long totalBytes,
        double maximumUtilization,
        long minimumRequiredBytes,
        long checkpointReservedBytes = 0,
        long journalReservedBytes = 0,
        long indexReservedBytes = 0,
        long compactionJournalReservedBytes = 0)
    {
        if (minimumRequiredBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRequiredBytes));
        }

        if (checkpointReservedBytes < 0
            || journalReservedBytes < 0
            || indexReservedBytes < 0
            || compactionJournalReservedBytes < 0)
        {
            return long.MaxValue;
        }

        if (WouldFit(
                usedBytes,
                articleReservedBytes,
                compactionReservedBytes,
                totalBytes,
                minimumRequiredBytes,
                maximumUtilization,
                checkpointReservedBytes,
                journalReservedBytes,
                indexReservedBytes,
                compactionJournalReservedBytes))
        {
            return 0;
        }

        var ceilingBytes = ComputeCeilingBytes(totalBytes, maximumUtilization);
        long occupied;
        try
        {
            occupied = checked(
                usedBytes
                + articleReservedBytes
                + journalReservedBytes
                + indexReservedBytes
                + compactionReservedBytes
                + compactionJournalReservedBytes
                + checkpointReservedBytes
                + minimumRequiredBytes);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }

        var deficit = occupied - ceilingBytes;
        return deficit > 0 ? deficit : 0;
    }

    private static long ScaleUtilization(double ceilingUtilization) =>
        (long)decimal.Round(
            (decimal)ceilingUtilization * UtilizationScale,
            MidpointRounding.AwayFromZero);

    /// <summary>
    /// Tentatively includes <paramref name="requiredBytes"/> in <see cref="ArticleReservedBytes"/>
    /// before durable Accept so concurrent admissions observe the claim.
    /// </summary>
    public void TentativeAddArticle(long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        _articleReservedBytes = checked(_articleReservedBytes + requiredBytes);
    }

    /// <summary>Alias for <see cref="TentativeAddArticle"/> (Phase 5E.1 call sites).</summary>
    public void TentativeAdd(long requiredBytes) => TentativeAddArticle(requiredBytes);

    /// <summary>
    /// Associates a tentative article reservation with the allocated journal sequence.
    /// This is the first segment-copy reservation for that sequence.
    /// </summary>
    public void BindArticleSequence(ulong sequence, long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        if (!_articleBySequence.TryAdd(sequence, new ArticleSegmentReservation(1, 0, requiredBytes)))
        {
            throw new InvalidOperationException($"Article capacity reservation already exists for sequence {sequence}.");
        }
    }

    /// <summary>Alias for <see cref="BindArticleSequence"/>.</summary>
    public void BindSequence(ulong sequence, long requiredBytes) =>
        BindArticleSequence(sequence, requiredBytes);

    /// <summary>Removes a tentative article reservation when durable Accept did not succeed.</summary>
    public void RollbackUnboundArticle(long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        _articleReservedBytes -= requiredBytes;
        if (_articleReservedBytes < 0)
        {
            _articleReservedBytes = 0;
        }
    }

    /// <summary>Alias for <see cref="RollbackUnboundArticle"/>.</summary>
    public void RollbackUnbound(long requiredBytes) => RollbackUnboundArticle(requiredBytes);

    /// <summary>True when <paramref name="sequence"/> has a segment copy reserved but not yet written.</summary>
    public bool HasUnwrittenSegmentCopy(ulong sequence) =>
        _articleBySequence.TryGetValue(sequence, out var reservation)
        && reservation.ReservedCopies > reservation.WrittenCopies;

    /// <summary>
    /// Reserves another physical segment copy for a sequence that already holds one.
    /// Caller must have already verified <see cref="WouldFit(long, long, long, double)"/>.
    /// </summary>
    public void AddSegmentCopy(ulong sequence, long bytesPerCopy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesPerCopy);
        if (!_articleBySequence.TryGetValue(sequence, out var current))
        {
            throw new InvalidOperationException(
                $"Article capacity reservation does not exist for sequence {sequence}.");
        }

        if (current.BytesPerCopy != bytesPerCopy)
        {
            throw new InvalidOperationException(
                $"Segment copy size {bytesPerCopy} does not match the reservation for sequence {sequence}.");
        }

        _articleBySequence[sequence] = new ArticleSegmentReservation(
            current.ReservedCopies + 1,
            current.WrittenCopies,
            current.BytesPerCopy);
        _articleReservedBytes = checked(_articleReservedBytes + bytesPerCopy);
    }

    /// <summary>Records that one reserved segment copy for <paramref name="sequence"/> was physically written.</summary>
    public void NoteSegmentCopyWritten(ulong sequence)
    {
        if (!_articleBySequence.TryGetValue(sequence, out var current))
        {
            throw new InvalidOperationException(
                $"Article capacity reservation does not exist for sequence {sequence}.");
        }

        if (current.WrittenCopies >= current.ReservedCopies)
        {
            throw new InvalidOperationException(
                $"Segment copy write has no reservation for sequence {sequence}.");
        }

        _articleBySequence[sequence] = new ArticleSegmentReservation(
            current.ReservedCopies,
            current.WrittenCopies + 1,
            current.BytesPerCopy);
    }

    /// <summary>
    /// Releases one segment-copy reservation that was not physically written.
    /// Written copies stay reserved.
    /// </summary>
    public bool ReleaseUnwrittenSegmentCopy(ulong sequence)
    {
        if (!_articleBySequence.TryGetValue(sequence, out var current)
            || current.ReservedCopies <= current.WrittenCopies)
        {
            return false;
        }

        var remaining = current.ReservedCopies - 1;
        _articleReservedBytes -= current.BytesPerCopy;
        if (_articleReservedBytes < 0)
        {
            _articleReservedBytes = 0;
        }

        if (remaining == 0)
        {
            _ = _articleBySequence.Remove(sequence);
        }
        else
        {
            _articleBySequence[sequence] = new ArticleSegmentReservation(
                remaining,
                current.WrittenCopies,
                current.BytesPerCopy);
        }

        return true;
    }

    /// <summary>
    /// Releases every segment-copy reservation for <paramref name="sequence"/>.
    /// Idempotent when the sequence was never reserved.
    /// </summary>
    public bool ReleaseArticle(ulong sequence)
    {
        if (!_articleBySequence.Remove(sequence, out var reservation))
        {
            return false;
        }

        _articleReservedBytes -= reservation.TotalBytes;
        if (_articleReservedBytes < 0)
        {
            _articleReservedBytes = 0;
        }

        return true;
    }

    /// <summary>Includes a journal-sequence reservation before durable Accept binds it.</summary>
    public void TentativeAddJournal(long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        _journalReservedBytes = checked(_journalReservedBytes + requiredBytes);
    }

    /// <summary>Binds a tentative journal reservation to the durable Accept sequence.</summary>
    public void BindJournalSequence(ulong sequence, long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        if (!_journalBySequence.TryAdd(sequence, requiredBytes))
        {
            throw new InvalidOperationException(
                $"Journal capacity reservation already exists for sequence {sequence}.");
        }
    }

    /// <summary>Drops a tentative journal reservation when durable Accept did not succeed.</summary>
    public void RollbackUnboundJournal(long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        _journalReservedBytes -= requiredBytes;
        if (_journalReservedBytes < 0)
        {
            _journalReservedBytes = 0;
        }
    }

    /// <summary>True when <paramref name="sequence"/> holds a journal-sequence reservation.</summary>
    public bool HoldsJournal(ulong sequence) => _journalBySequence.ContainsKey(sequence);

    /// <summary>Includes one not-yet-appended Present reservation before it is bound to an Accept sequence.</summary>
    public void TentativeAddIndex(long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        _indexReservedBytes = checked(_indexReservedBytes + requiredBytes);
    }

    /// <summary>Binds a tentative Present reservation to the durable Accept sequence.</summary>
    public void BindIndexUnbound(ulong sequence, long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        if (!_indexUnboundBySequence.TryAdd(sequence, requiredBytes))
        {
            throw new InvalidOperationException(
                $"Index capacity reservation already exists for sequence {sequence}.");
        }
    }

    /// <summary>True when <paramref name="sequence"/> holds a Present reservation that is not yet a frame.</summary>
    public bool HasUnboundIndex(ulong sequence) => _indexUnboundBySequence.ContainsKey(sequence);

    /// <summary>Drops a tentative Present reservation that was not bound to a sequence.</summary>
    public void RollbackUnboundIndex(long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        _indexReservedBytes -= requiredBytes;
        if (_indexReservedBytes < 0)
        {
            _indexReservedBytes = 0;
        }
    }

    /// <summary>
    /// Releases the not-yet-appended Present reservation for <paramref name="sequence"/>.
    /// Idempotent when that sequence holds none.
    /// </summary>
    public bool ReleaseUnboundIndex(ulong sequence)
    {
        if (!_indexUnboundBySequence.Remove(sequence, out var bytes))
        {
            return false;
        }

        _indexReservedBytes -= bytes;
        if (_indexReservedBytes < 0)
        {
            _indexReservedBytes = 0;
        }

        return true;
    }

    /// <summary>Moves a sequence's unbound Present reservation onto the physical frame at <paramref name="fileOffset"/>.</summary>
    public void BindIndexFrameFromSequence(ulong sequence, ArticleId artId, long fileOffset)
    {
        if (!_indexUnboundBySequence.Remove(sequence, out var bytes))
        {
            throw new InvalidOperationException(
                $"Index capacity reservation for sequence {sequence} is not unbound.");
        }

        _indexFrames.Add(new IndexPresentReservation(artId, fileOffset, SnapshotGeneration: 0, bytes));
    }

    /// <summary>Reserves one additional Present frame before a relocation append.</summary>
    public void TentativeAddDirectIndex(long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        _indexDirectUnboundBytes = checked(_indexDirectUnboundBytes + requiredBytes);
        _indexReservedBytes = checked(_indexReservedBytes + requiredBytes);
    }

    /// <summary>Binds a relocation Present reservation to the appended frame.</summary>
    public void BindDirectIndexFrame(ArticleId artId, long fileOffset, long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        if (_indexDirectUnboundBytes < requiredBytes)
        {
            throw new InvalidOperationException("Direct index capacity reservation is missing.");
        }

        _indexDirectUnboundBytes -= requiredBytes;
        _indexFrames.Add(new IndexPresentReservation(artId, fileOffset, SnapshotGeneration: 0, requiredBytes));
    }

    /// <summary>Drops a relocation Present reservation that did not append a frame.</summary>
    public void RollbackDirectIndex(long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        _indexDirectUnboundBytes -= requiredBytes;
        if (_indexDirectUnboundBytes < 0)
        {
            _indexDirectUnboundBytes = 0;
        }

        _indexReservedBytes -= requiredBytes;
        if (_indexReservedBytes < 0)
        {
            _indexReservedBytes = 0;
        }
    }

    /// <summary>Rebuilds one Present-frame reservation from durable index bytes. Does not consult the ceiling.</summary>
    public void AddRetainedIndexFrame(ArticleId artId, long fileOffset, ulong snapshotGeneration, long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        if (HoldsIndexFrame(artId, fileOffset, snapshotGeneration))
        {
            return;
        }

        _indexReservedBytes = checked(_indexReservedBytes + requiredBytes);
        _indexFrames.Add(new IndexPresentReservation(artId, fileOffset, snapshotGeneration, requiredBytes));
    }

    /// <summary>True when this ledger already holds the physical Present frame.</summary>
    public bool HoldsIndexFrame(ArticleId artId, long fileOffset, ulong snapshotGeneration)
    {
        foreach (var frame in _indexFrames)
        {
            if (frame.ArtId == artId
                && frame.FileOffset == fileOffset
                && frame.SnapshotGeneration == snapshotGeneration)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Releases index-frame reservations whose bytes are no longer in the authoritative index,
    /// rebinds frames that the replacement copied, and reserves one frame per installed snapshot row.
    /// Unbound Accept reservations are left in place.
    /// </summary>
    public void ApplyIndexPrefixRetirement(
        long coveredIndexLength,
        long newFrameBase,
        ulong snapshotGeneration,
        long frameBytes,
        IReadOnlyList<ArticleId> snapshotArticleIds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(coveredIndexLength);
        ArgumentOutOfRangeException.ThrowIfNegative(newFrameBase);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameBytes);
        ArgumentNullException.ThrowIfNull(snapshotArticleIds);

        var next = new List<IndexPresentReservation>(_indexFrames.Count + snapshotArticleIds.Count);
        foreach (var frame in _indexFrames)
        {
            if (frame.FileOffset >= coveredIndexLength)
            {
                var rebound = newFrameBase + (frame.FileOffset - coveredIndexLength);
                next.Add(frame with { FileOffset = rebound, SnapshotGeneration = 0 });
            }
        }

        foreach (var artId in snapshotArticleIds)
        {
            next.Add(new IndexPresentReservation(artId, FileOffset: -1, snapshotGeneration, frameBytes));
        }

        _indexFrames.Clear();
        _indexFrames.AddRange(next);
        RecalculateIndexReserved();
    }

    private void RecalculateIndexReserved()
    {
        long total = _indexDirectUnboundBytes;
        foreach (var bytes in _indexUnboundBySequence.Values)
        {
            total = checked(total + bytes);
        }

        foreach (var frame in _indexFrames)
        {
            total = checked(total + frame.Bytes);
        }

        _indexReservedBytes = total;
    }

    /// <summary>
    /// Releases the journal reservation for one sequence omitted by an installed checkpoint.
    /// Idempotent when the sequence was never reserved.
    /// </summary>
    public bool ReleaseJournal(ulong sequence)
    {
        if (!_journalBySequence.Remove(sequence, out var bytes))
        {
            return false;
        }

        _journalReservedBytes -= bytes;
        if (_journalReservedBytes < 0)
        {
            _journalReservedBytes = 0;
        }

        return true;
    }

    /// <summary>True when this exact compaction-journal frame already holds a reservation.</summary>
    public bool HoldsCompactionJournalFrame(
        ulong compactionId,
        CompactionJournalFrameKind kind,
        ulong relocationId) =>
        _compactionJournalFrames.ContainsKey(new CompactionJournalFrameKey(compactionId, kind, relocationId));

    /// <summary>
    /// Reserves one compaction-journal frame. Returns false when that identity is already reserved.
    /// Caller must have already verified <see cref="WouldFit(long, long, long, double)"/>.
    /// </summary>
    public bool TryAddCompactionJournalFrame(
        ulong compactionId,
        CompactionJournalFrameKind kind,
        ulong relocationId,
        long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        if (!_compactionJournalFrames.TryAdd(
                new CompactionJournalFrameKey(compactionId, kind, relocationId),
                bytes))
        {
            return false;
        }

        _compactionJournalReservedBytes += bytes;
        return true;
    }

    /// <summary>
    /// Releases one compaction-journal frame. Idempotent when that identity is not reserved.
    /// </summary>
    public bool ReleaseCompactionJournalFrame(
        ulong compactionId,
        CompactionJournalFrameKind kind,
        ulong relocationId)
    {
        if (!_compactionJournalFrames.Remove(
                new CompactionJournalFrameKey(compactionId, kind, relocationId),
                out var bytes))
        {
            return false;
        }

        _compactionJournalReservedBytes -= bytes;
        if (_compactionJournalReservedBytes < 0)
        {
            _compactionJournalReservedBytes = 0;
        }

        return true;
    }

    /// <summary>
    /// Releases every compaction-journal frame for <paramref name="compactionId"/>.
    /// Idempotent when that compaction has no reserved frames.
    /// </summary>
    public bool ReleaseCompactionJournal(ulong compactionId)
    {
        List<CompactionJournalFrameKey>? doomed = null;
        foreach (var key in _compactionJournalFrames.Keys)
        {
            if (key.CompactionId != compactionId)
            {
                continue;
            }

            doomed ??= new List<CompactionJournalFrameKey>();
            doomed.Add(key);
        }

        if (doomed is null)
        {
            return false;
        }

        long released = 0;
        foreach (var key in doomed)
        {
            if (_compactionJournalFrames.Remove(key, out var bytes))
            {
                released += bytes;
            }
        }

        _compactionJournalReservedBytes -= released;
        if (_compactionJournalReservedBytes < 0)
        {
            _compactionJournalReservedBytes = 0;
        }

        return released > 0;
    }

    /// <summary>Alias for <see cref="ReleaseArticle"/>.</summary>
    public bool Release(ulong sequence) => ReleaseArticle(sequence);

    /// <summary>True when <paramref name="sequence"/> still holds an article reservation.</summary>
    public bool HoldsArticle(ulong sequence) => _articleBySequence.ContainsKey(sequence);

    /// <summary>
    /// Binds a compaction destination reservation to <paramref name="compactionId"/> /
    /// <paramref name="relocationId"/> and increments <see cref="CompactionReservedBytes"/>.
    /// Caller must have already verified <see cref="WouldFit(long, long, long, double)"/>.
    /// </summary>
    /// <summary>
    /// True when this compaction destination already holds its reservation.
    /// </summary>
    public bool HoldsCompactionDestination(ulong compactionId, ulong relocationId) =>
        _compactionByKey.ContainsKey((compactionId, relocationId));

    public void ReserveCompaction(ulong compactionId, ulong relocationId, long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        var key = (compactionId, relocationId);
        if (!_compactionByKey.TryAdd(key, new CompactionDestinationHold(requiredBytes, Location: null)))
        {
            throw new InvalidOperationException(
                $"Compaction capacity reservation already exists for compaction {compactionId} relocation {relocationId}.");
        }

        _compactionReservedBytes = checked(_compactionReservedBytes + requiredBytes);
    }

    /// <summary>
    /// Records the segment that holds a destination whose <c>Flush(true)</c> has returned.
    /// The reservation stays until that segment is reclaimed.
    /// </summary>
    public void BindCompactionDestination(
        ulong compactionId,
        ulong relocationId,
        StoredArticleLocation location)
    {
        var key = (compactionId, relocationId);
        if (!_compactionByKey.TryGetValue(key, out var hold))
        {
            throw new InvalidOperationException(
                $"Compaction capacity reservation does not exist for compaction {compactionId} relocation {relocationId}.");
        }

        _compactionByKey[key] = hold with { Location = location };
    }

    /// <summary>Returns the durable destination already bound to this reservation.</summary>
    public bool TryGetCompactionDestination(
        ulong compactionId,
        ulong relocationId,
        out StoredArticleLocation location)
    {
        if (_compactionByKey.TryGetValue((compactionId, relocationId), out var hold)
            && hold.Location is { } bound)
        {
            location = bound;
            return true;
        }

        location = default;
        return false;
    }

    /// <summary>
    /// Releases destination reservations whose bound location is on <paramref name="segmentId"/>.
    /// Unbound reservations and other segments are left in place.
    /// </summary>
    public int ReleaseCompactionDestinationsOnSegment(SegmentId segmentId)
    {
        List<(ulong CompactionId, ulong RelocationId)>? keys = null;
        foreach (var pair in _compactionByKey)
        {
            if (pair.Value.Location is { } location && location.SegmentId == segmentId)
            {
                keys ??= [];
                keys.Add(pair.Key);
            }
        }

        if (keys is null)
        {
            return 0;
        }

        var released = 0;
        foreach (var key in keys)
        {
            if (ReleaseCompaction(key.CompactionId, key.RelocationId))
            {
                released++;
            }
        }

        return released;
    }

    /// <summary>
    /// Releases a compaction destination reservation. Idempotent when the key was never reserved.
    /// </summary>
    public bool ReleaseCompaction(ulong compactionId, ulong relocationId)
    {
        if (!_compactionByKey.Remove((compactionId, relocationId), out var hold))
        {
            return false;
        }

        _compactionReservedBytes -= hold.Bytes;
        if (_compactionReservedBytes < 0)
        {
            _compactionReservedBytes = 0;
        }

        return true;
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> for one checkpoint temporary file.
    /// Caller must have already verified <see cref="WouldFit(long, long, long, double)"/>.
    /// </summary>
    public ulong ReserveCheckpoint(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        var id = ++_nextCheckpointReservationId;
        if (!_checkpointById.TryAdd(id, bytes))
        {
            throw new InvalidOperationException($"Checkpoint capacity reservation id {id} already exists.");
        }

        _checkpointReservedBytes = checked(_checkpointReservedBytes + bytes);
        return id;
    }

    /// <summary>
    /// Adds <paramref name="additionalBytes"/> to an existing checkpoint reservation.
    /// Caller must have already verified the additional bytes fit.
    /// </summary>
    public bool TryIncreaseCheckpoint(ulong reservationId, long additionalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(additionalBytes);
        if (!_checkpointById.TryGetValue(reservationId, out var current))
        {
            return false;
        }

        _checkpointById[reservationId] = checked(current + additionalBytes);
        _checkpointReservedBytes = checked(_checkpointReservedBytes + additionalBytes);
        return true;
    }

    /// <summary>
    /// Releases a checkpoint reservation. Idempotent when the id was never reserved.
    /// </summary>
    public bool ReleaseCheckpoint(ulong reservationId)
    {
        if (!_checkpointById.Remove(reservationId, out var bytes))
        {
            return false;
        }

        _checkpointReservedBytes -= bytes;
        if (_checkpointReservedBytes < 0)
        {
            _checkpointReservedBytes = 0;
        }

        return true;
    }

    private readonly struct ArticleSegmentReservation
    {
        public ArticleSegmentReservation(int reservedCopies, int writtenCopies, long bytesPerCopy)
        {
            ReservedCopies = reservedCopies;
            WrittenCopies = writtenCopies;
            BytesPerCopy = bytesPerCopy;
        }

        public int ReservedCopies { get; }

        public int WrittenCopies { get; }

        public long BytesPerCopy { get; }

        public long TotalBytes => (long)ReservedCopies * BytesPerCopy;
    }

    private readonly record struct CompactionDestinationHold(long Bytes, StoredArticleLocation? Location);

    private readonly record struct IndexPresentReservation(
        ArticleId ArtId,
        long FileOffset,
        ulong SnapshotGeneration,
        long Bytes);
}
