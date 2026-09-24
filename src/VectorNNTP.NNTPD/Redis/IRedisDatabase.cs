namespace VectorNNTP.NNTPD.Redis;

/// <summary>Generic Redis key/value operations used by application consumers.</summary>
public interface IRedisDatabase
{
    /// <summary>Sends <c>PING</c> and returns the observed round-trip time.</summary>
    Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns whether <paramref name="key"/> exists.</summary>
    ValueTask<bool> KeyExistsAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/> with a Redis-native TTL.</summary>
    ValueTask SetAsync(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);
}
