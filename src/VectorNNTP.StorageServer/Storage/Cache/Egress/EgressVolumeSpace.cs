namespace VectorNNTP.StorageServer.Storage.Cache.Egress;

/// <summary>
/// Free-space sample for the ControlDir volume that hosts the egress cache.
/// </summary>
/// <remarks>
/// A failed read refuses cache admission. It does not fail article reads or Accept.
/// </remarks>
internal interface IEgressVolumeSpace
{
    /// <summary>
    /// Reads free and total bytes for the ControlDir volume.
    /// </summary>
    /// <param name="availableBytes">Free bytes when this returns true.</param>
    /// <param name="totalBytes">Volume size when this returns true.</param>
    /// <returns>False when the volume cannot be measured.</returns>
    bool TryRead(out long availableBytes, out long totalBytes);
}

/// <summary>
/// Reads ControlDir free space through <see cref="DriveInfo"/>.
/// </summary>
internal sealed class DriveEgressVolumeSpace : IEgressVolumeSpace
{
    private readonly string _controlDir;

    /// <summary>Creates a reader for <paramref name="controlDir"/>.</summary>
    /// <param name="controlDir">Resolved NVMe control directory.</param>
    public DriveEgressVolumeSpace(string controlDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlDir);
        _controlDir = controlDir;
    }

    /// <inheritdoc />
    public bool TryRead(out long availableBytes, out long totalBytes)
    {
        availableBytes = 0;
        totalBytes = 0;
        try
        {
            var fullPath = Path.GetFullPath(_controlDir);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var drive = new DriveInfo(root);
            totalBytes = drive.TotalSize;
            availableBytes = drive.AvailableFreeSpace;
            if (totalBytes <= 0 || availableBytes < 0)
            {
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DriveNotFoundException or ArgumentException)
        {
            return false;
        }
    }
}
