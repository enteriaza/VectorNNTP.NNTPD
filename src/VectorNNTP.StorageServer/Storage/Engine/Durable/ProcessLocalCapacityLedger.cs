namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>
/// Process-local byte reservation for article Accept and compaction destination appends
/// (Phase 5E.1 / 5E.2).
/// </summary>
/// <remarks>
/// Not a kernel or cross-process reservation. Callers must serialize mutate methods with
/// engine <c>_gate</c>. Shared formula uses
/// <c>Used + ArticleReserved + CompactionReserved + Required</c> against a policy ceiling.
/// Restart clears all state; durable recovery does not reconstruct reservations.
/// </remarks>
internal sealed class ProcessLocalCapacityLedger
{
    private long _articleReservedBytes;
    private long _compactionReservedBytes;
    private readonly Dictionary<ulong, long> _articleBySequence = new();
    private readonly Dictionary<(ulong CompactionId, ulong RelocationId), long> _compactionByKey = new();

    /// <summary>Bytes reserved for outstanding Accepts awaiting PhysicalWritten.</summary>
    public long ArticleReservedBytes => _articleReservedBytes;

    /// <summary>Bytes reserved for outstanding compaction destination appends.</summary>
    public long CompactionReservedBytes => _compactionReservedBytes;

    /// <summary>
    /// Total process-local reserved bytes (article + compaction). Alias for callers that need
    /// the combined figure used in capacity formulas.
    /// </summary>
    public long ReservedBytes => checked(_articleReservedBytes + _compactionReservedBytes);

    /// <summary>Number of Accept sequences currently holding an article reservation.</summary>
    public int ArticleReservationCount => _articleBySequence.Count;

    /// <summary>Number of compaction relocation keys currently holding a reservation.</summary>
    public int CompactionReservationCount => _compactionByKey.Count;

    /// <summary>Alias for <see cref="ArticleReservationCount"/> (Phase 5E.1 tests).</summary>
    public int ReservationCount => ArticleReservationCount;

    /// <summary>
    /// Returns whether <paramref name="requiredBytes"/> fits under
    /// <c>(used + articleReserved + compactionReserved + required) ≤ ceilingUtilization × total</c>.
    /// </summary>
    public bool WouldFit(
        long usedBytes,
        long totalBytes,
        long requiredBytes,
        double ceilingUtilization)
    {
        if (requiredBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        }

        if (totalBytes <= 0
            || usedBytes < 0
            || _articleReservedBytes < 0
            || _compactionReservedBytes < 0)
        {
            return false;
        }

        long projected;
        try
        {
            projected = checked(
                usedBytes + _articleReservedBytes + _compactionReservedBytes + requiredBytes);
        }
        catch (OverflowException)
        {
            return false;
        }

        const long scale = 1_000_000L;
        var utilScaled = (long)decimal.Round(
            (decimal)ceilingUtilization * scale,
            MidpointRounding.AwayFromZero);
        if (utilScaled <= 0 || utilScaled >= scale)
        {
            return false;
        }

        try
        {
            return checked(projected * scale) <= checked(totalBytes * utilScaled);
        }
        catch (OverflowException)
        {
            return (decimal)projected <= (decimal)totalBytes * (decimal)ceilingUtilization;
        }
    }

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

    /// <summary>Associates a tentative article reservation with the allocated journal sequence.</summary>
    public void BindArticleSequence(ulong sequence, long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        if (!_articleBySequence.TryAdd(sequence, requiredBytes))
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

    /// <summary>
    /// Releases the article reservation for <paramref name="sequence"/> after PhysicalWritten.
    /// Idempotent when the sequence was never reserved.
    /// </summary>
    public bool ReleaseArticle(ulong sequence)
    {
        if (!_articleBySequence.Remove(sequence, out var bytes))
        {
            return false;
        }

        _articleReservedBytes -= bytes;
        if (_articleReservedBytes < 0)
        {
            _articleReservedBytes = 0;
        }

        return true;
    }

    /// <summary>Alias for <see cref="ReleaseArticle"/>.</summary>
    public bool Release(ulong sequence) => ReleaseArticle(sequence);

    /// <summary>
    /// Binds a compaction destination reservation to <paramref name="compactionId"/> /
    /// <paramref name="relocationId"/> and increments <see cref="CompactionReservedBytes"/>.
    /// Caller must have already verified <see cref="WouldFit"/>.
    /// </summary>
    public void ReserveCompaction(ulong compactionId, ulong relocationId, long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        var key = (compactionId, relocationId);
        if (!_compactionByKey.TryAdd(key, requiredBytes))
        {
            throw new InvalidOperationException(
                $"Compaction capacity reservation already exists for compaction {compactionId} relocation {relocationId}.");
        }

        _compactionReservedBytes = checked(_compactionReservedBytes + requiredBytes);
    }

    /// <summary>
    /// Releases a compaction destination reservation. Idempotent when the key was never reserved.
    /// </summary>
    public bool ReleaseCompaction(ulong compactionId, ulong relocationId)
    {
        if (!_compactionByKey.Remove((compactionId, relocationId), out var bytes))
        {
            return false;
        }

        _compactionReservedBytes -= bytes;
        if (_compactionReservedBytes < 0)
        {
            _compactionReservedBytes = 0;
        }

        return true;
    }
}
