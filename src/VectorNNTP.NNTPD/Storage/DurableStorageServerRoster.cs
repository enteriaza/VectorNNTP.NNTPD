using System.Buffers.Binary;
using System.Text;

namespace VectorNNTP.NNTPD.Storage;

/// <summary>Durable dial identity for one StorageServer. Capacity is not stored.</summary>
/// <param name="ServerId">Stable numeric identity used by replication pins.</param>
/// <param name="Fqdn">Last advertised FQDN.</param>
/// <param name="VatpPort">Last advertised TLS VATP port.</param>
public readonly record struct StorageServerRosterEntry(int ServerId, string Fqdn, int VatpPort);

/// <summary>
/// Remembers StorageServers after their advertisements go stale.
/// </summary>
public interface IStorageServerRoster
{
    /// <summary>Creates or updates the entry for <paramref name="serverId"/> and flushes it.</summary>
    void Observe(int serverId, string fqdn, int vatpPort);

    /// <summary>Reads the durable entry for <paramref name="serverId"/>.</summary>
    bool TryGet(int serverId, out StorageServerRosterEntry entry);

    /// <summary>Returns every durable entry. Advertisement expiry does not remove entries.</summary>
    IReadOnlyList<StorageServerRosterEntry> Snapshot();
}

/// <summary>
/// One durable file per <see cref="StorageServerRosterEntry.ServerId"/>.
/// There is no automatic deletion when an advertisement stops.
/// </summary>
public sealed class DurableStorageServerRoster : IStorageServerRoster
{
    private const int Version = 1;
    private readonly object _gate = new();
    private readonly string _directory;

    private DurableStorageServerRoster(string directory)
    {
        _directory = directory;
    }

    /// <summary>Opens or creates the roster directory under <paramref name="replicationDirectory"/>.</summary>
    public static DurableStorageServerRoster Open(string replicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replicationDirectory);
        var directory = Path.Combine(replicationDirectory, "roster");
        Directory.CreateDirectory(directory);
        return new DurableStorageServerRoster(directory);
    }

    /// <inheritdoc />
    public void Observe(int serverId, string fqdn, int vatpPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
        if (vatpPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(vatpPort));
        }

        var trimmed = fqdn.Trim();
        lock (_gate)
        {
            var finalPath = PathFor(serverId);
            var temporary = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteDurable(temporary, serverId, trimmed, vatpPort);
                File.Move(temporary, finalPath, overwrite: true);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGet(int serverId, out StorageServerRosterEntry entry)
    {
        lock (_gate)
        {
            var path = PathFor(serverId);
            if (!File.Exists(path))
            {
                entry = default;
                return false;
            }

            entry = Read(path);
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StorageServerRosterEntry> Snapshot()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory))
            {
                return [];
            }

            var files = Directory.GetFiles(_directory, "*.roster");
            if (files.Length == 0)
            {
                return [];
            }

            var entries = new StorageServerRosterEntry[files.Length];
            for (var i = 0; i < files.Length; i++)
            {
                entries[i] = Read(files[i]);
            }

            return entries;
        }
    }

    private string PathFor(int serverId) =>
        Path.Combine(_directory, serverId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".roster");

    private static void WriteDurable(string path, int serverId, string fqdn, int vatpPort)
    {
        var fqdnBytes = Encoding.UTF8.GetBytes(fqdn);
        if (fqdnBytes.Length is < 1 or > 512)
        {
            throw new ArgumentException("FQDN must be 1 to 512 UTF-8 bytes.", nameof(fqdn));
        }

        var buffer = new byte[4 + 4 + 4 + 4 + 4 + fqdnBytes.Length];
        buffer[0] = (byte)'V';
        buffer[1] = (byte)'N';
        buffer[2] = (byte)'S';
        buffer[3] = (byte)'R';
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), serverId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), vatpPort);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), fqdnBytes.Length);
        fqdnBytes.CopyTo(buffer.AsSpan(20));
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(buffer);
        stream.Flush(flushToDisk: true);
    }

    private static StorageServerRosterEntry Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20
            || bytes[0] != (byte)'V'
            || bytes[1] != (byte)'N'
            || bytes[2] != (byte)'S'
            || bytes[3] != (byte)'R'
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) != Version)
        {
            throw new InvalidDataException("StorageServer roster file is corrupt.");
        }

        var serverId = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var port = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
        if (port is < 1 or > 65535 || length < 1 || bytes.Length != 20 + length)
        {
            throw new InvalidDataException("StorageServer roster file is corrupt.");
        }

        var fqdn = Encoding.UTF8.GetString(bytes, 20, length);
        return new StorageServerRosterEntry(serverId, fqdn, port);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Roster used when this NNTPD does not persist StorageServer dial identity.</summary>
public sealed class NullStorageServerRoster : IStorageServerRoster
{
    /// <summary>Shared instance.</summary>
    public static NullStorageServerRoster Instance { get; } = new();

    private NullStorageServerRoster()
    {
    }

    /// <inheritdoc />
    public void Observe(int serverId, string fqdn, int vatpPort)
    {
    }

    /// <inheritdoc />
    public bool TryGet(int serverId, out StorageServerRosterEntry entry)
    {
        entry = default;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<StorageServerRosterEntry> Snapshot() => [];
}
