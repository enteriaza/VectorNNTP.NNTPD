using System.Runtime.InteropServices;

namespace VectorNNTP.StorageServer.Storage;

/// <summary>
/// Byte counts for the filesystem that contains one directory.
/// </summary>
/// <param name="TotalBytes">Total bytes on that filesystem. Always positive when a read succeeds.</param>
/// <param name="AvailableBytes">Bytes available to this caller on that filesystem. Never negative when a read succeeds.</param>
/// <param name="VolumeIdentity">
/// Platform volume identity when it could be resolved. Empty when the byte count is valid but the
/// name lookup failed. Never a different volume's identity.
/// </param>
/// <param name="MountPoint">
/// Windows mount point for the measured filesystem, including a directory mount without a drive
/// letter. Empty when the platform does not report a mount point.
/// </param>
internal readonly record struct DirectoryVolumeSpaceMeasurement(
    long TotalBytes,
    long AvailableBytes,
    string VolumeIdentity,
    string MountPoint);

/// <summary>
/// Measures the filesystem that contains a directory.
/// </summary>
/// <remarks>
/// A false result is an unavailable measurement. Implementations must not substitute another
/// volume, including the process drive, and must not create the directory.
/// </remarks>
internal interface IDirectoryVolumeSpace
{
    /// <summary>
    /// Measures the filesystem that contains <paramref name="directoryPath"/>.
    /// </summary>
    /// <param name="directoryPath">Directory to measure. Not created by this call.</param>
    /// <param name="measurement">Valid counts when this returns true; otherwise default.</param>
    /// <returns>False when the path is invalid, missing, inaccessible, or the volume cannot be measured.</returns>
    bool TryMeasure(string directoryPath, out DirectoryVolumeSpaceMeasurement measurement);
}

/// <summary>
/// OS measurement of the filesystem that contains a directory.
/// </summary>
/// <remarks>
/// <para>
/// Windows calls <c>GetDiskFreeSpaceExW</c> on the directory. That API reports the mounted
/// filesystem, including an NTFS volume mounted at a directory without a drive letter.
/// <see cref="DriveInfo"/> is not used on Windows because it normalizes to
/// <see cref="Path.GetPathRoot(string)"/> and would account the directory against the drive letter.
/// Available bytes are the caller-available value, matching <see cref="DriveInfo.AvailableFreeSpace"/>
/// for an ordinary drive-letter path on the same volume.
/// </para>
/// <para>
/// Linux measures <see cref="DriveInfo"/> on the full directory path, which uses that path's
/// filesystem rather than the path root. Other platforms fail the read.
/// </para>
/// </remarks>
internal static class OsDirectoryVolumeSpace
{
    /// <summary>Shared process-wide measurement.</summary>
    public static IDirectoryVolumeSpace Shared { get; } = new Measurer();

    /// <summary>Process-wide measurement. Does not cache byte counts.</summary>
    private sealed class Measurer : IDirectoryVolumeSpace
    {
        /// <inheritdoc />
        public bool TryMeasure(string directoryPath, out DirectoryVolumeSpaceMeasurement measurement)
        {
            measurement = default;
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(directoryPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            if (!Directory.Exists(fullPath))
            {
                return false;
            }

            if (!TryReadBytes(fullPath, out var total, out var available))
            {
                return false;
            }

            var identity = "";
            if (OsStorageVolumeProbe.Shared.TryResolve(fullPath, out var resolved))
            {
                identity = resolved.Value;
            }

            var mountPoint = "";
            if (OsStorageVolumeProbe.TryGetMountPoint(fullPath, out var mount))
            {
                mountPoint = mount;
            }

            measurement = new DirectoryVolumeSpaceMeasurement(total, available, identity, mountPoint);
            return true;
        }
    }

    /// <summary>
    /// Reads total and caller-available bytes for <paramref name="fullPath"/>'s filesystem.
    /// Returns false on an unsupported platform or an API failure. Does not query a different path.
    /// </summary>
    private static bool TryReadBytes(string fullPath, out long totalBytes, out long availableBytes)
    {
        totalBytes = 0;
        availableBytes = 0;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return TryReadWindows(fullPath, out totalBytes, out availableBytes);
            }

            if (OperatingSystem.IsLinux())
            {
                return TryReadLinux(fullPath, out totalBytes, out availableBytes);
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DriveNotFoundException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Calls <c>GetDiskFreeSpaceExW</c> on <paramref name="fullPath"/>. A directory mount returns
    /// that mounted filesystem, not the drive letter that contains the mount point.
    /// </summary>
    private static bool TryReadWindows(string fullPath, out long totalBytes, out long availableBytes)
    {
        totalBytes = 0;
        availableBytes = 0;
        if (!GetDiskFreeSpaceEx(fullPath, out var availableToCaller, out var total, out _))
        {
            return false;
        }

        if (total <= 0 || availableToCaller < 0)
        {
            return false;
        }

        totalBytes = total;
        availableBytes = availableToCaller;
        return true;
    }

    /// <summary>
    /// Reads <see cref="DriveInfo"/> for <paramref name="fullPath"/> itself, not its path root.
    /// </summary>
    private static bool TryReadLinux(string fullPath, out long totalBytes, out long availableBytes)
    {
        totalBytes = 0;
        availableBytes = 0;
        var drive = new DriveInfo(fullPath);
        if (drive.TotalSize <= 0 || drive.AvailableFreeSpace < 0)
        {
            return false;
        }

        totalBytes = drive.TotalSize;
        availableBytes = drive.AvailableFreeSpace;
        return true;
    }

#pragma warning disable SYSLIB1054
    /// <summary>Windows free-space query for the filesystem that contains a directory.</summary>
    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetDiskFreeSpaceExW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out long freeBytesAvailableToCaller,
        out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes);
#pragma warning restore SYSLIB1054
}
