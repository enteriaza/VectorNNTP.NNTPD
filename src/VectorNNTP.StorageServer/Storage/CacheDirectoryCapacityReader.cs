namespace VectorNNTP.StorageServer.Storage;

/// <summary>Authoritative byte counts for the StorageServer cache volume.</summary>
/// <param name="TotalBytes">Total bytes on the cache volume.</param>
/// <param name="UsedBytes">Used bytes on the cache volume.</param>
/// <param name="AvailableBytes">Available bytes on the cache volume.</param>
public readonly record struct StorageCapacitySnapshot(
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes);

/// <summary>Reads raw cache-volume capacity for fleet advertisements.</summary>
public interface IStorageCapacityReader
{
    /// <summary>Reads the current capacity snapshot.</summary>
    StorageCapacitySnapshot Read();
}

/// <summary>
/// Reads capacity for the configured cache directory via <see cref="DriveInfo"/>.
/// </summary>
public sealed class CacheDirectoryCapacityReader : IStorageCapacityReader
{
    private readonly string _cacheDir;

    /// <summary>Initializes a reader for <paramref name="cacheDir"/>.</summary>
    public CacheDirectoryCapacityReader(string cacheDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDir);
        _cacheDir = cacheDir;
    }

    /// <inheritdoc />
    public StorageCapacitySnapshot Read()
    {
        var fullPath = Path.GetFullPath(_cacheDir);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException($"Unable to resolve a drive root for cache directory '{_cacheDir}'.");
        }

        var drive = new DriveInfo(root);
        var total = drive.TotalSize;
        var available = drive.AvailableFreeSpace;
        var used = Math.Max(0L, total - available);
        return new StorageCapacitySnapshot(total, used, available);
    }
}
