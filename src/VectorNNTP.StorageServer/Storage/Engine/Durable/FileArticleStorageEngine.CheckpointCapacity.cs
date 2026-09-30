using VectorNNTP.StorageServer.Storage;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

public sealed partial class FileArticleStorageEngine
{
    /// <summary>
    /// Invoked under the ledger lock immediately before a checkpoint reservation is attempted.
    /// Tests only. Must not take the engine gate, the journal lock, or the index lock.
    /// </summary>
    internal Action<long>? TestBeforeCheckpointReserve { get; set; }

    /// <summary>
    /// Invoked after an admission samples <c>UsedBytes</c> and before that admission decides,
    /// without the ledger lock held. Tests only. The decision re-reads <c>UsedBytes</c> under
    /// the ledger lock after this returns, so a release performed here cannot be paired with
    /// the earlier sample. Must not take the engine gate, the journal lock, or the index lock,
    /// and must not perform filesystem I/O.
    /// </summary>
    internal Action<StorageCapacitySnapshot>? TestAfterCapacitySample { get; set; }

    /// <summary>
    /// When a test hook is installed, samples <c>UsedBytes</c> under the ledger lock, drops
    /// that lock, and invokes the hook. Production admission does not call this.
    /// </summary>
    private void NotifyCapacitySample(CapacityVolume volume)
    {
        var hook = TestAfterCapacitySample;
        if (hook is null)
        {
            return;
        }

        var observed = volume.WithLedger(_ => volume.Reader.Read());
        hook(observed);
    }

    private readonly record struct CapacityAdmitResult(
        bool Admitted,
        bool AlreadySatisfied,
        StorageCapacitySnapshot Snapshot,
        long ArticleReservedBytes,
        long CompactionReservedBytes,
        long CheckpointReservedBytes,
        long ReservedBytes);

    /// <summary>
    /// Samples <c>UsedBytes</c>, evaluates <see cref="ProcessLocalCapacityLedger.WouldFit(long, long, long, double)"/>,
    /// and runs <paramref name="reserve"/> while holding <paramref name="volume"/>'s ledger lock.
    /// The lock does not cover filesystem I/O. A test hook, if set, runs before that critical section.
    /// </summary>
    private CapacityAdmitResult Admit(
        CapacityVolume volume,
        long requiredBytes,
        double ceilingUtilization,
        Func<ProcessLocalCapacityLedger, bool> alreadySatisfied,
        Action<ProcessLocalCapacityLedger> reserve)
    {
        NotifyCapacitySample(volume);
        return volume.WithLedger(ledger =>
        {
            if (alreadySatisfied(ledger))
            {
                return new CapacityAdmitResult(
                    Admitted: true,
                    AlreadySatisfied: true,
                    Snapshot: default,
                    ledger.ArticleReservedBytes,
                    ledger.CompactionReservedBytes,
                    ledger.CheckpointReservedBytes,
                    ledger.ReservedBytes);
            }

            var snap = volume.Reader.Read();
            if (!ledger.WouldFit(snap.UsedBytes, snap.TotalBytes, requiredBytes, ceilingUtilization))
            {
                return new CapacityAdmitResult(
                    Admitted: false,
                    AlreadySatisfied: false,
                    snap,
                    ledger.ArticleReservedBytes,
                    ledger.CompactionReservedBytes,
                    ledger.CheckpointReservedBytes,
                    ledger.ReservedBytes);
            }

            reserve(ledger);
            return new CapacityAdmitResult(
                Admitted: true,
                AlreadySatisfied: false,
                snap,
                ledger.ArticleReservedBytes,
                ledger.CompactionReservedBytes,
                ledger.CheckpointReservedBytes,
                ledger.ReservedBytes);
        });
    }

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
        NotifyCapacitySample(volume);
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
        NotifyCapacitySample(volume);
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
