using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace VectorNNTP.NNTPD.Authentication;

/// <summary>
/// Short-lived post-success burst cache. Entries expire after 10 seconds.
/// Failed lookups and disabled accounts are never stored.
/// </summary>
internal sealed class NntpUserRecordCache
{
    internal static readonly byte[] UsernameOnlyFingerprint = "username-only"u8.ToArray();

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;

    /// <summary>Initializes a cache with the reference 10-second TTL.</summary>
    public NntpUserRecordCache(TimeProvider? timeProvider = null, TimeSpan? ttl = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _ttl = ttl ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Tries to retrieve a still-valid cached record.</summary>
    public bool TryGet(string username, ReadOnlySpan<byte> fingerprint, out NntpUserRecord? record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        var key = FormatKey(username, fingerprint);
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > _time.GetUtcNow())
        {
            record = entry.Record;
            return true;
        }

        _entries.TryRemove(key, out _);
        record = null;
        return false;
    }

    /// <summary>Stores a successful-authentication snapshot.</summary>
    public void Put(string username, ReadOnlySpan<byte> fingerprint, NntpUserRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(record);
        var key = FormatKey(username, fingerprint);
        _entries[key] = new CacheEntry(record, _time.GetUtcNow() + _ttl);
    }

    /// <summary>SHA-256 fingerprint of an ASCII password for AUTHINFO cache keys.</summary>
    public static byte[] ComputePasswordFingerprint(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return SHA256.HashData(Encoding.ASCII.GetBytes(password));
    }

    private static string FormatKey(string username, ReadOnlySpan<byte> fingerprint)
    {
        var userHash = SHA256.HashData(Encoding.UTF8.GetBytes(username));
        var combined = new byte[userHash.Length + 1 + fingerprint.Length];
        userHash.CopyTo(combined, 0);
        combined[userHash.Length] = (byte)'|';
        fingerprint.CopyTo(combined.AsSpan(userHash.Length + 1));
        return Convert.ToHexString(combined);
    }

    private readonly record struct CacheEntry(NntpUserRecord Record, DateTimeOffset ExpiresAt);
}
