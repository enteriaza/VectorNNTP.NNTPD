namespace VectorNNTP.StorageServer.Storage;

/// <summary>Authoritative byte counts for one storage filesystem.</summary>
/// <param name="TotalBytes">Total bytes on that filesystem.</param>
/// <param name="UsedBytes">Used bytes, <c>max(0, TotalBytes - AvailableBytes)</c>, from the same filesystem.</param>
/// <param name="AvailableBytes">Bytes available to this caller on that filesystem.</param>
public readonly record struct StorageCapacitySnapshot(
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes);

/// <summary>Reads raw cache-volume capacity for fleet advertisements and bulk pressure.</summary>
public interface IStorageCapacityReader
{
    /// <summary>Reads the current capacity snapshot.</summary>
    /// <exception cref="InvalidOperationException">The filesystem containing the directory could not be measured.</exception>
    StorageCapacitySnapshot Read();
}

/// <summary>
/// Reads capacity for the filesystem that contains the configured cache directory.
/// </summary>
/// <remarks>
/// The measurement is the filesystem that contains the directory, including a Windows volume
/// mounted at that directory without a drive letter. It does not use
/// <see cref="Path.GetPathRoot(string)"/>. A failed measurement throws and does not substitute
/// another volume. This type does not create the directory.
/// </remarks>
public sealed class CacheDirectoryCapacityReader : IStorageCapacityReader
{
    private readonly string _cacheDir;
    private readonly IDirectoryVolumeSpace _volumeSpace;

    /// <summary>Initializes a reader for <paramref name="cacheDir"/>.</summary>
    /// <param name="cacheDir">Retention or control directory to measure.</param>
    public CacheDirectoryCapacityReader(string cacheDir)
        : this(cacheDir, OsDirectoryVolumeSpace.Shared)
    {
    }

    /// <summary>Initializes a reader that measures <paramref name="cacheDir"/> through <paramref name="volumeSpace"/>.</summary>
    /// <param name="cacheDir">Retention or control directory to measure.</param>
    /// <param name="volumeSpace">Filesystem measurement. A false result fails this read.</param>
    internal CacheDirectoryCapacityReader(string cacheDir, IDirectoryVolumeSpace volumeSpace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDir);
        ArgumentNullException.ThrowIfNull(volumeSpace);
        _cacheDir = cacheDir;
        _volumeSpace = volumeSpace;
    }

    /// <inheritdoc />
    public StorageCapacitySnapshot Read()
    {
        if (!_volumeSpace.TryMeasure(_cacheDir, out var measured)
            || measured.TotalBytes <= 0
            || measured.AvailableBytes < 0)
        {
            throw new InvalidOperationException(
                $"Unable to measure the filesystem that contains '{_cacheDir}'. Another volume's capacity was not substituted.");
        }

        var used = Math.Max(0L, measured.TotalBytes - measured.AvailableBytes);
        return new StorageCapacitySnapshot(measured.TotalBytes, used, measured.AvailableBytes);
    }
}
