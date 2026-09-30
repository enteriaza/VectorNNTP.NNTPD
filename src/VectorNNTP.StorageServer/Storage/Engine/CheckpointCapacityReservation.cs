namespace VectorNNTP.StorageServer.Storage.Engine;

/// <summary>
/// Process-local reservation for one checkpoint temporary file.
/// </summary>
/// <remarks>
/// Callers must not hold the article index lock or the journal lock while invoking these
/// delegates. Accept takes the engine gate and then those locks; calling back under either
/// lock deadlocks. A denied reserve must not create the temp file. Release is idempotent.
/// </remarks>
internal sealed class CheckpointCapacityReservation
{
    /// <summary>
    /// Reserves the supplied byte count and returns an id, or null when the ceiling refuses.
    /// </summary>
    public required Func<long, ulong?> TryReserve { get; init; }

    /// <summary>
    /// Adds the supplied additional byte count to an existing reservation id.
    /// False when the ceiling refuses or the id is unknown.
    /// </summary>
    public required Func<ulong, long, bool> TryIncrease { get; init; }

    /// <summary>Releases one reservation. Unknown ids are ignored.</summary>
    public required Action<ulong> Release { get; init; }
}

/// <summary>
/// A checkpoint temp could not be reserved without exceeding the article capacity ceiling.
/// The authoritative file is unchanged and the temp was not created for this refusal.
/// </summary>
internal sealed class CheckpointCapacityDeniedException : Exception
{
    /// <summary>Creates a denial for <paramref name="requiredBytes"/>.</summary>
    public CheckpointCapacityDeniedException(long requiredBytes)
        : base($"Checkpoint temporary allocation of {requiredBytes} bytes was denied by process-local capacity.")
    {
        RequiredBytes = requiredBytes;
    }

    /// <summary>Bytes the checkpoint needed to reserve.</summary>
    public long RequiredBytes { get; }
}
