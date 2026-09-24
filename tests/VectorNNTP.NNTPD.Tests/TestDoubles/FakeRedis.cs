using System.Collections.Concurrent;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Redis;

namespace VectorNNTP.NNTPD.Tests.TestDoubles;

/// <summary>In-memory Redis connection factory for offline tests.</summary>
internal sealed class FakeRedisConnectionFactory : IRedisConnectionFactory
{
    public int ConnectCount { get; private set; }

    public Exception? ConnectException { get; set; }

    public FakeRedisConnection? LastConnection { get; private set; }

    public Task<IRedisConnection> ConnectAsync(RedisOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (ConnectException is not null)
        {
            throw ConnectException;
        }

        ConnectCount++;
        LastConnection = new FakeRedisConnection();
        return Task.FromResult<IRedisConnection>(LastConnection);
    }
}

/// <summary>In-memory Redis connection wrapping one <see cref="FakeRedisDatabase"/>.</summary>
internal sealed class FakeRedisConnection : IRedisConnection
{
    public FakeRedisDatabase Database { get; } = new();

    public int DisposeCount { get; private set; }

    public IRedisDatabase GetDatabase() => Database;

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>In-memory Redis database with optional failure injection.</summary>
internal sealed class FakeRedisDatabase : IRedisDatabase
{
    private readonly ConcurrentDictionary<string, (byte[] Value, DateTimeOffset Expiry)> _keys =
        new(StringComparer.Ordinal);

    public int PingCount { get; set; }

    public int KeyExistsCount { get; set; }

    private int _existsStartedCount;

    public int ExistsStartedCount => Volatile.Read(ref _existsStartedCount);

    public ConcurrentDictionary<string, TaskCompletionSource> BlockExistsByKey { get; } = new(StringComparer.Ordinal);

    public int SetCount { get; set; }

    public Exception? ExistsException { get; set; }

    public Exception? SetException { get; set; }

    public TaskCompletionSource? BlockSet { get; set; }

    public TaskCompletionSource? BlockExists { get; set; }

    public TaskCompletionSource? ExistsStarted { get; set; }

    public int NotifyExistsStartedAt { get; set; }

    public TaskCompletionSource? ExistsReached { get; set; }

    public Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PingCount++;
        return Task.FromResult(TimeSpan.FromMilliseconds(1));
    }

    public async ValueTask<bool> KeyExistsAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        var started = Interlocked.Increment(ref _existsStartedCount);
        ExistsStarted?.TrySetResult();
        if (NotifyExistsStartedAt > 0 && started >= NotifyExistsStartedAt)
        {
            ExistsReached?.TrySetResult();
        }
        if (BlockExistsByKey.TryGetValue(ToKey(key), out var keyedBlock))
        {
            await keyedBlock.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (BlockExists is not null)
        {
            await BlockExists.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        KeyExistsCount++;
        if (ExistsException is not null)
        {
            throw ExistsException;
        }

        if (!_keys.TryGetValue(ToKey(key), out var entry))
        {
            return false;
        }

        if (entry.Expiry <= DateTimeOffset.UtcNow)
        {
            _keys.TryRemove(ToKey(key), out _);
            return false;
        }

        return true;
    }

    public async ValueTask SetAsync(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        if (BlockSet is not null)
        {
            await BlockSet.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        SetCount++;
        if (SetException is not null)
        {
            throw SetException;
        }

        _keys[ToKey(key)] = (value.ToArray(), DateTimeOffset.UtcNow.Add(expiry));
    }

    public void Seed(ReadOnlyMemory<byte> key, TimeSpan? expiry = null) =>
        _keys[ToKey(key)] = ([1], DateTimeOffset.UtcNow.Add(expiry ?? TimeSpan.FromHours(2)));

    public bool Contains(ReadOnlyMemory<byte> key) => _keys.ContainsKey(ToKey(key));

    private static string ToKey(ReadOnlyMemory<byte> key) => Convert.ToHexString(key.Span);
}

/// <summary>Direct <see cref="IRedisService"/> double for HistoryDB tests.</summary>
internal sealed class FakeRedisService : IRedisService
{
    public FakeRedisDatabase Database { get; } = new();

    IRedisDatabase IRedisService.Database => Database;

    public bool IsUnavailable { get; set; }

    public int BeginRejectCount { get; private set; }

    public bool TryBeginOperation(out bool isRecoveryProbe)
    {
        isRecoveryProbe = false;
        if (IsUnavailable)
        {
            BeginRejectCount++;
            return false;
        }

        return true;
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

    public void Recover() => IsUnavailable = false;

    public ValueTask<bool> KeyExistsAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
        Database.KeyExistsAsync(key, cancellationToken);

    public ValueTask SetAsync(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        TimeSpan expiry,
        CancellationToken cancellationToken = default) =>
        Database.SetAsync(key, value, expiry, cancellationToken);
}
