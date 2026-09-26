using System.Collections.Concurrent;
using VectorNNTP.NNTPD.History;
using VectorNNTP.NNTPD.Redis;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Bench-only Redis: binary HistoryDB keys, optional delay, optional first-call failure/cooldown.
/// </summary>
internal sealed class CheckDelayedRedis : IRedisService
{
    private readonly ConcurrentDictionary<HistoryDigest, byte> _keys = new();

    public int ExistsCount { get; set; }

    public TimeSpan Delay { get; set; }

    public bool FailExists { get; set; }

    public IRedisDatabase Database => throw new NotSupportedException();

    public bool IsUnavailable { get; private set; }

    public void Seed(HistoryDigest digest) => _keys[digest] = 1;

    public bool TryBeginOperation(out bool isRecoveryProbe)
    {
        isRecoveryProbe = false;
        return !IsUnavailable;
    }

    public void CompleteOperation(bool isRecoveryProbe, bool succeeded, Exception? exception = null)
    {
        if (!succeeded)
        {
            IsUnavailable = true;
        }
    }

    public void AbandonOperation(bool isRecoveryProbe)
    {
    }

    public async ValueTask<bool> KeyExistsAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        ExistsCount++;
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
        }

        if (FailExists)
        {
            throw new RedisUnavailableException("down");
        }

        var digest = HistoryDigest.FromSpan(key.Span[HistoryRedisKeys.NamespacePrefix.Length..]);
        return _keys.ContainsKey(digest);
    }

    public ValueTask<byte[]?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<byte[]?>(null);

    public ValueTask SetAsync(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        TimeSpan expiry,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
