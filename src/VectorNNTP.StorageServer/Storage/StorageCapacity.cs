namespace VectorNNTP.StorageServer.Storage;

/// <summary>Byte counts carried in a capacity advertisement.</summary>
/// <param name="TotalBytes">Total bytes reported to peers.</param>
/// <param name="UsedBytes">Used bytes reported to peers.</param>
/// <param name="AvailableBytes">Available bytes reported to peers.</param>
public readonly record struct StorageCapacitySnapshot(
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes);

/// <summary>Supplies the capacity fields of a cache advertisement.</summary>
public interface IStorageCapacityReader
{
    /// <summary>Reads the current capacity snapshot.</summary>
    StorageCapacitySnapshot Read();
}

/// <summary>
/// Advertises no durable spool. StorageServer no longer stores articles on disk,
/// so the published totals are zero. The advertisement exchange and payload shape
/// are unchanged.
/// </summary>
public sealed class NoSpoolCapacityReader : IStorageCapacityReader
{
    /// <inheritdoc />
    public StorageCapacitySnapshot Read() => new(0, 0, 0);
}
