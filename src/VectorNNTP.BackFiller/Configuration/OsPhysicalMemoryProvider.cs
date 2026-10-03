using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Resolves total physical RAM from the operating system for startup validation.
/// </summary>
/// <remarks>
/// Linux reads <c>/proc/meminfo</c> <c>MemTotal</c> (kB × 1024).
/// Windows uses <c>GlobalMemoryStatusEx</c> <c>ullTotalPhys</c>.
/// There is no GC, available-memory, or cgroup fallback.
/// </remarks>
internal sealed class OsPhysicalMemoryProvider : IPhysicalMemoryProvider
{
    internal const string LinuxMemInfoPath = "/proc/meminfo";

    /// <inheritdoc />
    public long GetTotalPhysicalMemoryBytes()
    {
        if (OperatingSystem.IsLinux())
        {
            return ReadLinuxMemTotalBytes(LinuxMemInfoPath);
        }

        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsTotalPhysicalMemoryBytes();
        }

        throw new PlatformNotSupportedException(
            "Total physical memory detection is supported only on Linux and Windows.");
    }

    /// <summary>
    /// Reads <c>MemTotal</c> from a Linux <c>/proc/meminfo</c>-shaped file and converts kB to bytes.
    /// </summary>
    /// <param name="memInfoPath">Path to the meminfo file.</param>
    /// <returns>Total physical memory in bytes.</returns>
    internal static long ReadLinuxMemTotalBytes(string memInfoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memInfoPath);
        if (!File.Exists(memInfoPath))
        {
            throw new InvalidOperationException(
                "Unable to determine total physical memory from /proc/meminfo: file does not exist.");
        }

        try
        {
            using var reader = File.OpenText(memInfoPath);
            return ParseLinuxMemTotalBytes(reader);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "Unable to determine total physical memory from /proc/meminfo: file could not be read.",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                "Unable to determine total physical memory from /proc/meminfo: access was denied.",
                ex);
        }
    }

    /// <summary>
    /// Parses <c>MemTotal</c> from <c>/proc/meminfo</c> text. Other keys are ignored.
    /// </summary>
    /// <param name="reader">meminfo text.</param>
    /// <returns>Total physical memory in bytes.</returns>
    internal static long ParseLinuxMemTotalBytes(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !parts[2].Equals("kB", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Unable to determine total physical memory from /proc/meminfo: MemTotal entry format is invalid.");
            }

            if (!ulong.TryParse(parts[1], out var kibibytes))
            {
                throw new InvalidOperationException(
                    "Unable to determine total physical memory from /proc/meminfo: MemTotal value is not numeric.");
            }

            try
            {
                return ToSignedPhysicalBytes(checked(kibibytes * 1024UL), "MemTotal");
            }
            catch (OverflowException ex)
            {
                throw new InvalidOperationException(
                    "Unable to determine total physical memory from /proc/meminfo: MemTotal value overflowed byte conversion.",
                    ex);
            }
        }

        throw new InvalidOperationException(
            "Unable to determine total physical memory from /proc/meminfo: MemTotal entry was not found.");
    }

    /// <summary>
    /// Selects Windows total physical RAM. <paramref name="ullAvailPhys"/> is accepted only so tests
    /// can prove available memory is not the source.
    /// </summary>
    /// <param name="ullTotalPhys"><c>MEMORYSTATUSEX.ullTotalPhys</c>.</param>
    /// <param name="ullAvailPhys"><c>MEMORYSTATUSEX.ullAvailPhys</c>; unused.</param>
    /// <returns>Total physical memory in bytes.</returns>
    internal static long SelectWindowsTotalPhysicalMemoryBytes(ulong ullTotalPhys, ulong ullAvailPhys)
    {
        _ = ullAvailPhys;
        return ToSignedPhysicalBytes(ullTotalPhys, "ullTotalPhys");
    }

    private static long ToSignedPhysicalBytes(ulong bytes, string fieldName)
    {
        if (bytes == 0)
        {
            throw new InvalidOperationException(
                $"Unable to determine total physical memory: {fieldName} was zero.");
        }

        if (bytes > long.MaxValue)
        {
            throw new InvalidOperationException(
                $"Unable to determine total physical memory: {fieldName} exceeds the signed 64-bit byte range.");
        }

        return (long)bytes;
    }

    [SupportedOSPlatform("windows")]
    private static long ReadWindowsTotalPhysicalMemoryBytes()
    {
        var status = new MemoryStatusEx
        {
            dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>(),
        };

        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException(
                "Unable to determine total physical memory from GlobalMemoryStatusEx.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return SelectWindowsTotalPhysicalMemoryBytes(status.ullTotalPhys, status.ullAvailPhys);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        internal uint dwLength;
        internal uint dwMemoryLoad;
        internal ulong ullTotalPhys;
        internal ulong ullAvailPhys;
        internal ulong ullTotalPageFile;
        internal ulong ullAvailPageFile;
        internal ulong ullTotalVirtual;
        internal ulong ullAvailVirtual;
        internal ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
