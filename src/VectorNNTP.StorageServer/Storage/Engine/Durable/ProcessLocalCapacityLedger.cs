namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>
/// Process-local byte reservation for article Accept admission (Phase 5E.1).
/// </summary>
/// <remarks>
/// Not a kernel or cross-process reservation. Callers must serialize mutate methods with
/// Accept admission (engine <c>_gate</c>). Lifecycle:
/// <list type="number">
/// <item><see cref="WouldFit"/> then <see cref="TentativeAdd"/> under the admission gate.</item>
/// <item>Durable journal Accept.</item>
/// <item><see cref="BindSequence"/> on success, or <see cref="RollbackUnbound"/> on Accept failure.</item>
/// <item><see cref="Release"/> after durable PhysicalWritten (not on SATA retry failure).</item>
/// </list>
/// Restart clears all state; durable Accept recovery does not reconstruct reservations.
/// </remarks>
internal sealed class ProcessLocalCapacityLedger
{
    private long _reservedBytes;
    private readonly Dictionary<ulong, long> _bySequence = new();

    /// <summary>Sum of bytes reserved for outstanding Accepts awaiting PhysicalWritten.</summary>
    public long ReservedBytes => _reservedBytes;

    /// <summary>Number of sequences currently holding a reservation.</summary>
    public int ReservationCount => _bySequence.Count;

    /// <summary>
    /// Returns whether <paramref name="requiredBytes"/> fits under
    /// <c>(used + reserved + required) ≤ maximumUtilization × total</c>.
    /// </summary>
    public bool WouldFit(
        long usedBytes,
        long totalBytes,
        long requiredBytes,
        double maximumUtilization)
    {
        if (requiredBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        }

        if (totalBytes <= 0 || usedBytes < 0 || _reservedBytes < 0)
        {
            return false;
        }

        long projected;
        try
        {
            projected = checked(usedBytes + _reservedBytes + requiredBytes);
        }
        catch (OverflowException)
        {
            return false;
        }

        // projected / total <= maximumUtilization via scaled integer compare.
        const long scale = 1_000_000L;
        var utilScaled = (long)decimal.Round(
            (decimal)maximumUtilization * scale,
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
            return (decimal)projected <= (decimal)totalBytes * (decimal)maximumUtilization;
        }
    }

    /// <summary>
    /// Tentatively includes <paramref name="requiredBytes"/> in <see cref="ReservedBytes"/>
    /// before durable Accept so concurrent admissions observe the claim.
    /// </summary>
    public void TentativeAdd(long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        _reservedBytes = checked(_reservedBytes + requiredBytes);
    }

    /// <summary>Associates a tentative reservation with the allocated journal sequence.</summary>
    public void BindSequence(ulong sequence, long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        if (!_bySequence.TryAdd(sequence, requiredBytes))
        {
            throw new InvalidOperationException($"Capacity reservation already exists for sequence {sequence}.");
        }
    }

    /// <summary>Removes a tentative reservation when durable Accept did not succeed.</summary>
    public void RollbackUnbound(long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        _reservedBytes -= requiredBytes;
        if (_reservedBytes < 0)
        {
            _reservedBytes = 0;
        }
    }

    /// <summary>
    /// Releases the reservation for <paramref name="sequence"/> after PhysicalWritten.
    /// Idempotent when the sequence was never reserved (e.g. recovery after restart).
    /// </summary>
    public bool Release(ulong sequence)
    {
        if (!_bySequence.Remove(sequence, out var bytes))
        {
            return false;
        }

        _reservedBytes -= bytes;
        if (_reservedBytes < 0)
        {
            _reservedBytes = 0;
        }

        return true;
    }
}
