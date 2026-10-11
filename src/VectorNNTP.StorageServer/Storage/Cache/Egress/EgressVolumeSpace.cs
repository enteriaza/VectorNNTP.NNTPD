using VectorNNTP.StorageServer.Storage;

namespace VectorNNTP.StorageServer.Storage.Cache.Egress;

/// <summary>
/// Free-space sample for the filesystem that contains the ControlDir egress cache.
/// </summary>
/// <remarks>
/// A failed read refuses cache admission. It does not fail article reads or Accept.
/// </remarks>
internal interface IEgressVolumeSpace
{
    /// <summary>
    /// Reads free and total bytes for the ControlDir filesystem.
    /// </summary>
    /// <param name="availableBytes">Free bytes when this returns true.</param>
    /// <param name="totalBytes">Volume size when this returns true.</param>
    /// <returns>False when the filesystem cannot be measured. Another volume is not substituted.</returns>
    bool TryRead(out long availableBytes, out long totalBytes);
}

/// <summary>
/// Reads ControlDir free space from the filesystem that contains the directory.
/// </summary>
internal sealed class DriveEgressVolumeSpace : IEgressVolumeSpace
{
    private readonly string _controlDir;
    private readonly IDirectoryVolumeSpace _volumeSpace;

    /// <summary>Creates a reader for <paramref name="controlDir"/>.</summary>
    /// <param name="controlDir">Resolved control directory.</param>
    public DriveEgressVolumeSpace(string controlDir)
        : this(controlDir, OsDirectoryVolumeSpace.Shared)
    {
    }

    /// <summary>Creates a reader that measures <paramref name="controlDir"/> through <paramref name="volumeSpace"/>.</summary>
    /// <param name="controlDir">Resolved control directory.</param>
    /// <param name="volumeSpace">Filesystem measurement. A false result refuses the sample.</param>
    internal DriveEgressVolumeSpace(string controlDir, IDirectoryVolumeSpace volumeSpace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlDir);
        ArgumentNullException.ThrowIfNull(volumeSpace);
        _controlDir = controlDir;
        _volumeSpace = volumeSpace;
    }

    /// <inheritdoc />
    public bool TryRead(out long availableBytes, out long totalBytes)
    {
        availableBytes = 0;
        totalBytes = 0;
        if (!_volumeSpace.TryMeasure(_controlDir, out var measured))
        {
            return false;
        }

        if (measured.TotalBytes <= 0 || measured.AvailableBytes < 0)
        {
            return false;
        }

        totalBytes = measured.TotalBytes;
        availableBytes = measured.AvailableBytes;
        return true;
    }
}
