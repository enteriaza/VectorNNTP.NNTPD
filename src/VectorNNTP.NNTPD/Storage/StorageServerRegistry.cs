using System.Collections.Concurrent;
using VectorNNTP.Common.Messaging.Cache;

namespace VectorNNTP.NNTPD.Storage;

/// <summary>Liveness classification for a known StorageServer.</summary>
public enum StorageServerFleetState
{
    /// <summary>LastSeen is within the cache-fleet liveness window.</summary>
    Active = 0,

    /// <summary>LastSeen is older than the cache-fleet liveness window.</summary>
    Stale = 1,
}

/// <summary>
/// One in-memory StorageServer fleet entry maintained from cache broadcast advertisements.
/// </summary>
/// <param name="ServerId">Numeric StorageServer identity.</param>
/// <param name="Fqdn">StorageServer FQDN (registry key).</param>
/// <param name="TotalBytes">Latest advertised total storage bytes.</param>
/// <param name="UsedBytes">Latest advertised used storage bytes.</param>
/// <param name="AvailableBytes">Latest advertised available storage bytes.</param>
/// <param name="LastSeen">UTC time when the latest advertisement was applied.</param>
public readonly record struct StorageServerFleetEntry(
    int ServerId,
    string Fqdn,
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes,
    DateTimeOffset LastSeen)
{
    /// <summary>
    /// Classifies this entry relative to <paramref name="utcNow"/> using
    /// <see cref="CacheFleetTopology.LivenessWindow"/>.
    /// </summary>
    public StorageServerFleetState GetState(DateTimeOffset utcNow) =>
        utcNow - LastSeen <= CacheFleetTopology.LivenessWindow
            ? StorageServerFleetState.Active
            : StorageServerFleetState.Stale;

    /// <summary>Returns whether this entry is active at <paramref name="utcNow"/>.</summary>
    public bool IsActive(DateTimeOffset utcNow) => GetState(utcNow) == StorageServerFleetState.Active;
}

/// <summary>
/// In-memory authoritative view of currently known StorageServers for future placement decisions.
/// </summary>
/// <remarks>
/// Hot-path lookups must use this registry only. Do not call RabbitMQ, disk, or network from
/// selection logic merely to learn which StorageServers are alive.
/// </remarks>
public interface IStorageServerRegistry
{
    /// <summary>
    /// Applies a parsed advertisement, creating or updating the FQDN-keyed entry and refreshing
    /// <see cref="StorageServerFleetEntry.LastSeen"/>.
    /// </summary>
    /// <param name="advertisement">Validated advertisement payload.</param>
    /// <param name="receivedAtUtc">UTC receive time used as LastSeen.</param>
    void ApplyAdvertisement(StorageServerAdvertisement advertisement, DateTimeOffset receivedAtUtc);

    /// <summary>Attempts to read the entry for <paramref name="fqdn"/>.</summary>
    bool TryGet(string fqdn, out StorageServerFleetEntry entry);

    /// <summary>Returns a snapshot of all known entries (active and stale).</summary>
    IReadOnlyList<StorageServerFleetEntry> Snapshot();

    /// <summary>Returns entries whose LastSeen is within the liveness window at <paramref name="utcNow"/>.</summary>
    IReadOnlyList<StorageServerFleetEntry> GetActive(DateTimeOffset utcNow);
}

/// <summary>Thread-safe in-memory StorageServer fleet registry.</summary>
public sealed class StorageServerRegistry : IStorageServerRegistry
{
    private readonly ConcurrentDictionary<string, StorageServerFleetEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void ApplyAdvertisement(StorageServerAdvertisement advertisement, DateTimeOffset receivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        ArgumentException.ThrowIfNullOrWhiteSpace(advertisement.Fqdn);

        var key = advertisement.Fqdn.Trim();
        var entry = new StorageServerFleetEntry(
            advertisement.ServerId,
            key,
            advertisement.TotalBytes,
            advertisement.UsedBytes,
            advertisement.AvailableBytes,
            receivedAtUtc.ToUniversalTime());

        _entries.AddOrUpdate(key, entry, (_, _) => entry);
    }

    /// <inheritdoc />
    public bool TryGet(string fqdn, out StorageServerFleetEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
        return _entries.TryGetValue(fqdn.Trim(), out entry);
    }

    /// <inheritdoc />
    public IReadOnlyList<StorageServerFleetEntry> Snapshot() => [.. _entries.Values];

    /// <inheritdoc />
    public IReadOnlyList<StorageServerFleetEntry> GetActive(DateTimeOffset utcNow)
    {
        var now = utcNow.ToUniversalTime();
        List<StorageServerFleetEntry>? active = null;
        foreach (var entry in _entries.Values)
        {
            if (!entry.IsActive(now))
            {
                continue;
            }

            active ??= [];
            active.Add(entry);
        }

        return active is null ? Array.Empty<StorageServerFleetEntry>() : active;
    }
}
