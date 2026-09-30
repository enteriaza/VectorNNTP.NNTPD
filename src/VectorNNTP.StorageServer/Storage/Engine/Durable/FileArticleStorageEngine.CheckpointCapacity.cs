namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

public sealed partial class FileArticleStorageEngine
{
    /// <summary>
    /// Invoked under the engine gate immediately before a checkpoint reservation is attempted.
    /// Tests only. Must not take the engine gate, the journal lock, or the index lock.
    /// </summary>
    internal Action<long>? TestBeforeCheckpointReserve { get; set; }

    /// <summary>Process-local checkpoint temporary-file reserved bytes on the control ledger (tests).</summary>
    internal long ProcessLocalCheckpointReservedBytes =>
        _controlCapacity is null
            ? 0
            : _controlCapacity.WithLedger(static ledger => ledger.CheckpointReservedBytes);

    private CheckpointCapacityReservation CreateCheckpointCapacity() =>
        new()
        {
            TryReserve = TryReserveCheckpoint,
            TryIncrease = TryIncreaseCheckpoint,
            Release = ReleaseCheckpoint,
        };

    private ulong? TryReserveCheckpoint(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        var volume = RequireControlVolume();
        return volume.WithLedger(ledger =>
        {
            TestBeforeCheckpointReserve?.Invoke(bytes);
            if (!TryAdmitCheckpointBytes(volume, ledger, bytes))
            {
                return (ulong?)null;
            }

            return ledger.ReserveCheckpoint(bytes);
        });
    }

    private bool TryIncreaseCheckpoint(ulong reservationId, long additionalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(additionalBytes);
        var volume = RequireControlVolume();
        return volume.WithLedger(ledger =>
        {
            TestBeforeCheckpointReserve?.Invoke(additionalBytes);
            if (!TryAdmitCheckpointBytes(volume, ledger, additionalBytes))
            {
                return false;
            }

            return ledger.TryIncreaseCheckpoint(reservationId, additionalBytes);
        });
    }

    private void ReleaseCheckpoint(ulong reservationId)
    {
        var volume = RequireControlVolume();
        _ = volume.WithLedger(ledger => ledger.ReleaseCheckpoint(reservationId));
    }

    /// <summary>
    /// Checkpoint temps use the article ceiling on the control volume. When that volume is also
    /// the segment volume, the reservation participates in article and compaction decisions.
    /// </summary>
    private bool TryAdmitCheckpointBytes(
        CapacityVolume volume,
        ProcessLocalCapacityLedger ledger,
        long bytes)
    {
        var snap = volume.Reader.Read();
        if (ledger.WouldFit(
                snap.UsedBytes,
                snap.TotalBytes,
                bytes,
                _capacityMaximumUtilization))
        {
            return true;
        }

        FileArticleStorageEngineLogMessages.CheckpointCapacityDenied(
            _logger,
            bytes,
            snap.UsedBytes,
            ledger.ArticleReservedBytes,
            ledger.CompactionReservedBytes,
            ledger.CheckpointReservedBytes,
            snap.TotalBytes,
            snap.AvailableBytes,
            _capacityMaximumUtilization);
        return false;
    }
}
