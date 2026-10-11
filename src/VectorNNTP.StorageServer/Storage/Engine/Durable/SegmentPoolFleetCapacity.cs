using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>
/// Folds every configured spool into the single <see cref="StorageCapacitySnapshot"/> the fleet
/// advertisement already publishes. The wire fields stay <c>totalBytes</c>, <c>usedBytes</c>, and
/// <c>availableBytes</c>.
/// </summary>
/// <remarks>
/// Each filesystem is counted once. Remaining room is the minimum of that filesystem's admission
/// room and the unused <c>MaxCapacity</c> of the spools on it. A failed measurement contributes
/// accounted bytes and no free bytes. When every filesystem is unmeasured the call throws, so the
/// publisher skips the heartbeat instead of advertising free space.
/// </remarks>
internal static class SegmentPoolFleetCapacity
{
    /// <summary>One spool's cap, accounted bytes, and filesystem sample.</summary>
    /// <param name="VolumeKey">Filesystem identity. Equal keys share one physical budget.</param>
    /// <param name="MaxCapacity">Configured hard ceiling. <see cref="SegmentPoolCapacity.Uncapped"/> is not a cap.</param>
    /// <param name="AccountedBytes">File length plus outstanding reservations.</param>
    /// <param name="ReservedBytes">Reservations included in <paramref name="AccountedBytes"/> that are not yet in the filesystem used sample.</param>
    /// <param name="Measured">False when this spool's filesystem could not be read.</param>
    /// <param name="FilesystemTotalBytes">Filesystem total when <paramref name="Measured"/> is true.</param>
    /// <param name="FilesystemAvailableBytes">Filesystem free bytes when <paramref name="Measured"/> is true.</param>
    internal readonly record struct SpoolSample(
        string VolumeKey,
        long MaxCapacity,
        long AccountedBytes,
        long ReservedBytes,
        bool Measured,
        long FilesystemTotalBytes,
        long FilesystemAvailableBytes);

    /// <summary>
    /// Builds the advertisement triple from <paramref name="spools"/>.
    /// </summary>
    /// <param name="spools">Every configured spool. Empty is invalid.</param>
    /// <param name="maximumUtilization">Filesystem utilization ceiling, the same percent admission uses.</param>
    /// <param name="policy">Reserve watermarks admission uses at High and above.</param>
    /// <returns>
    /// <see cref="StorageCapacitySnapshot.UsedBytes"/> is accounted bytes.
    /// <see cref="StorageCapacitySnapshot.AvailableBytes"/> is remaining admission room.
    /// <see cref="StorageCapacitySnapshot.TotalBytes"/> is their sum.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// No spool is configured, a cap is not positive, or every filesystem measurement failed.
    /// </exception>
    public static StorageCapacitySnapshot Combine(
        ReadOnlySpan<SpoolSample> spools,
        int maximumUtilization,
        BulkStoragePressurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (spools.Length == 0)
        {
            throw new InvalidOperationException("No segment spool is configured.");
        }

        var volumes = new List<Volume>(spools.Length);
        for (var i = 0; i < spools.Length; i++)
        {
            var spool = spools[i];
            if (spool.MaxCapacity <= 0)
            {
                throw new InvalidOperationException("A segment spool capacity must be positive.");
            }

            if (spool.AccountedBytes < 0 || spool.ReservedBytes < 0)
            {
                throw new InvalidOperationException("Segment spool accounted bytes cannot be negative.");
            }

            if (string.IsNullOrWhiteSpace(spool.VolumeKey))
            {
                throw new InvalidOperationException("A segment spool is missing its filesystem identity.");
            }

            var volume = Find(volumes, spool.VolumeKey);
            volume.Accounted = Add(volume.Accounted, spool.AccountedBytes);
            volume.Reserved = Add(volume.Reserved, spool.ReservedBytes);
            if (spool.MaxCapacity == SegmentPoolCapacity.Uncapped)
            {
                volume.Uncapped = true;
            }
            else
            {
                var room = spool.MaxCapacity > spool.AccountedBytes
                    ? spool.MaxCapacity - spool.AccountedBytes
                    : 0;
                volume.CapRoom = Add(volume.CapRoom, room);
            }

            if (!spool.Measured || spool.FilesystemTotalBytes <= 0 || spool.FilesystemAvailableBytes < 0)
            {
                continue;
            }

            if (!volume.Measured || spool.FilesystemAvailableBytes < volume.Available)
            {
                volume.Measured = true;
                volume.Total = spool.FilesystemTotalBytes;
                volume.Available = spool.FilesystemAvailableBytes;
            }
        }

        long used = 0;
        long available = 0;
        var anyMeasured = false;
        foreach (var volume in volumes)
        {
            used = Add(used, volume.Accounted);
            if (!volume.Measured)
            {
                continue;
            }

            anyMeasured = true;
            var room = FilesystemAdmissionRoom(
                volume.Total,
                volume.Available,
                volume.Reserved,
                maximumUtilization,
                policy);
            if (!volume.Uncapped && room > volume.CapRoom)
            {
                room = volume.CapRoom;
            }

            available = Add(available, room);
        }

        if (!anyMeasured)
        {
            throw new InvalidOperationException("Unable to measure any segment spool filesystem.");
        }

        return new StorageCapacitySnapshot(Add(used, available), used, available);
    }

    private static Volume Find(List<Volume> volumes, string key)
    {
        foreach (var volume in volumes)
        {
            if (string.Equals(volume.Key, key, StringComparison.Ordinal))
            {
                return volume;
            }
        }

        var created = new Volume(key);
        volumes.Add(created);
        return created;
    }

    private static long FilesystemAdmissionRoom(
        long total,
        long available,
        long reserved,
        int maximumUtilization,
        BulkStoragePressurePolicy policy)
    {
        var used = total > available ? total - available : 0;
        long utilRoom;
        try
        {
            utilRoom = checked(ProcessLocalCapacityLedger.ComputeCeilingBytes(total, maximumUtilization) - used - reserved);
        }
        catch (OverflowException)
        {
            utilRoom = 0;
        }

        if (utilRoom < 0)
        {
            utilRoom = 0;
        }

        var evaluation = policy.Evaluate(total, used, available);
        long freeRoom;
        try
        {
            freeRoom = checked(available - reserved);
            if (evaluation.State is BulkStoragePressureState.High
                or BulkStoragePressureState.Critical
                or BulkStoragePressureState.Emergency)
            {
                freeRoom = checked(freeRoom - evaluation.RecoveryReserveBytes);
            }
        }
        catch (OverflowException)
        {
            freeRoom = 0;
        }

        if (freeRoom < 0)
        {
            freeRoom = 0;
        }

        return utilRoom < freeRoom ? utilRoom : freeRoom;
    }

    private static long Add(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException("Segment pool capacity overflowed.", ex);
        }
    }

    private sealed class Volume(string key)
    {
        public string Key { get; } = key;

        public long Accounted { get; set; }

        public long Reserved { get; set; }

        public long CapRoom { get; set; }

        public bool Uncapped { get; set; }

        public bool Measured { get; set; }

        public long Total { get; set; }

        public long Available { get; set; }
    }
}
