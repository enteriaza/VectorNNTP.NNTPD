using System.Runtime.InteropServices;

namespace VectorNNTP.StorageServer.Storage;

/// <summary>
/// Resolves a directory to its OS volume identity.
/// </summary>
/// <remarks>
/// Windows uses the volume GUID from <c>GetVolumeNameForVolumeMountPoint</c>.
/// Linux uses <c>statx</c> device major/minor. Other platforms do not resolve.
/// </remarks>
internal static class OsStorageVolumeProbe
{
    /// <summary>Shared process-wide probe.</summary>
    public static IStorageVolumeProbe Shared { get; } = new Probe();

    private sealed class Probe : IStorageVolumeProbe
    {
        /// <inheritdoc />
        public bool TryResolve(string directoryPath, out StorageVolumeIdentity identity)
        {
            identity = default;
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

            if (OperatingSystem.IsWindows())
            {
                return TryResolveWindows(fullPath, out identity);
            }

            if (OperatingSystem.IsLinux())
            {
                return TryResolveLinux(fullPath, out identity);
            }

            return false;
        }
    }

    private static bool TryResolveWindows(string fullPath, out StorageVolumeIdentity identity)
    {
        identity = default;
        var mountPoint = new char[32768];
        if (!GetVolumePathName(fullPath, mountPoint, (uint)mountPoint.Length))
        {
            return false;
        }

        var mount = ToNullTerminated(mountPoint);
        if (mount.Length == 0)
        {
            return false;
        }

        if (!mount.EndsWith('\\'))
        {
            mount += "\\";
        }

        var volumeName = new char[64];
        if (!GetVolumeNameForVolumeMountPoint(mount, volumeName, (uint)volumeName.Length))
        {
            return false;
        }

        var name = ToNullTerminated(volumeName);
        if (name.Length == 0)
        {
            return false;
        }

        identity = new StorageVolumeIdentity(name);
        return true;
    }

    private static bool TryResolveLinux(string fullPath, out StorageVolumeIdentity identity)
    {
        identity = default;
        var stat = default(LinuxStatx);
        if (statx(AtFdCwd, fullPath, 0, LinuxStatxBasicStats, ref stat) != 0)
        {
            return false;
        }

        identity = new StorageVolumeIdentity($"linux:{stat.DevMajor}:{stat.DevMinor}");
        return true;
    }

    private static string ToNullTerminated(char[] buffer)
    {
        var length = Array.IndexOf(buffer, '\0');
        if (length < 0)
        {
            length = buffer.Length;
        }

        return new string(buffer, 0, length);
    }

    private const int AtFdCwd = -100;

    private const uint LinuxStatxBasicStats = 0x000007ff;

#pragma warning disable SYSLIB1054
    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetVolumePathNameW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(
        string fileName,
        char[] volumePathName,
        uint bufferLength);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetVolumeNameForVolumeMountPointW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        char[] volumeName,
        uint bufferLength);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int statx(
        int dirFd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        ref LinuxStatx stat);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint Nlink;
        public uint Uid;
        public uint Gid;
        public ushort Mode;
        public ushort Spare0;
        public ulong Ino;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public LinuxStatxTimestamp AccessTime;
        public LinuxStatxTimestamp BirthTime;
        public LinuxStatxTimestamp ChangeTime;
        public LinuxStatxTimestamp ModifyTime;
        public uint RdevMajor;
        public uint RdevMinor;
        public uint DevMajor;
        public uint DevMinor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }
}
