using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using VectorNNTP.NNTPD.SessionState.BytesAccounting;
using VectorNNTP.NNTPD.SessionState;
using VectorNNTP.NNTPD.Transit;
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

    public int ScriptEvaluateCount { get; set; }

    public Exception? ScriptException { get; set; }

    public TaskCompletionSource? BlockScript { get; set; }

    internal SessionStateEngine SessionStateEngine { get; } = new();

    internal TransitPeerStateEngine TransitPeerStateEngine { get; } = new();

    internal AccountByteEngine AccountByteEngine { get; } = new();

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

    public async ValueTask<long> ScriptEvaluateAsync(
        string script,
        ReadOnlyMemory<byte>[] keys,
        ReadOnlyMemory<byte>[] values,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(script);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(values);
        if (BlockScript is not null)
        {
            await BlockScript.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ScriptEvaluateCount++;
        if (ScriptException is not null)
        {
            throw ScriptException;
        }

        if (keys.Length == 1)
        {
            var key = Encoding.UTF8.GetString(keys[0].Span);
            if (script == AccountByteScripts.Apply)
            {
                return AccountByteEngine.Apply(key, Utf8(values[0]), ParseLong(values[1]), ParseLong(values[2]));
            }

            if (script == AccountByteScripts.Observe)
            {
                return AccountByteEngine.Observe(key);
            }

            if (script == AccountByteScripts.Delete)
            {
                return AccountByteEngine.Delete(key);
            }

            return EvaluateTransit(script, key, values);
        }

        if (keys.Length >= 3 && script == SessionStateScripts.RenewAndApply)
        {
            return EvaluateRenewAndApply(keys, values);
        }

        if (keys.Length < 2)
        {
            throw new InvalidOperationException("Admission EVAL requires KEYS[1] source and KEYS[2] session.");
        }

        var sourceKey = Encoding.UTF8.GetString(keys[0].Span);
        var sessionKey = Encoding.UTF8.GetString(keys[1].Span);
        if (script == SessionStateScripts.TryAdmit)
        {
            return SessionStateEngine.TryAdmit(
                sourceKey,
                sessionKey,
                Utf8(values[0]),
                Utf8(values[1]),
                ParseInt(values[2]),
                ParseInt(values[3]),
                ParseLong(values[4]),
                ParseLong(values[5]),
                ParseLong(values[6]),
                ParseLong(values[7]),
                trackSessions: values.Length > 8 && ParseInt(values[8]) == 1);
        }

        if (script == SessionStateScripts.Release)
        {
            return SessionStateEngine.Release(
                sourceKey,
                sessionKey,
                Utf8(values[0]),
                Utf8(values[1]),
                ParseLong(values[2]),
                ParseLong(values[3]));
        }

        if (script == SessionStateScripts.Renew)
        {
            var ipCount = ParseInt(values[4]);
            var sources = new (string Ip, long Generation)[ipCount];
            for (var i = 0; i < ipCount; i++)
            {
                sources[i] = (Utf8(values[5 + (i * 2)]), ParseLong(values[6 + (i * 2)]));
            }

            return SessionStateEngine.Renew(
                sourceKey,
                sessionKey,
                Utf8(values[0]),
                ParseLong(values[1]),
                ParseLong(values[2]),
                ParseLong(values[3]),
                sources);
        }

        if (script == SessionStateScripts.ReleaseOwner)
        {
            return SessionStateEngine.ReleaseOwner(sourceKey, sessionKey, Utf8(values[0]));
        }

        throw new NotSupportedException("FakeRedis only evaluates cluster admission scripts.");
    }

    private long EvaluateRenewAndApply(ReadOnlyMemory<byte>[] keys, ReadOnlyMemory<byte>[] values)
    {
        var sourceKey = Encoding.UTF8.GetString(keys[0].Span);
        var sessionKey = Encoding.UTF8.GetString(keys[1].Span);
        var bytesKey = Encoding.UTF8.GetString(keys[2].Span);
        var ipCount = ParseInt(values[4]);
        var sources = new (string Ip, long Generation)[ipCount];
        for (var i = 0; i < ipCount; i++)
        {
            sources[i] = (Utf8(values[5 + (i * 2)]), ParseLong(values[6 + (i * 2)]));
        }

        var renewed = SessionStateEngine.Renew(
            sourceKey,
            sessionKey,
            Utf8(values[0]),
            ParseLong(values[1]),
            ParseLong(values[2]),
            ParseLong(values[3]),
            sources);
        var remaining = AccountByteEngine.Apply(
            bytesKey,
            Utf8(values[5 + (ipCount * 2)]),
            ParseLong(values[6 + (ipCount * 2)]),
            ParseLong(values[7 + (ipCount * 2)]));
        return SessionStateBytePack.Encode(renewed == 1, remaining);
    }

    private long EvaluateTransit(string script, string connectionKey, ReadOnlyMemory<byte>[] values)
    {
        if (script == TransitPeerStateScripts.TryAdmit)
        {
            return TransitPeerStateEngine.TryAdmit(
                connectionKey,
                Utf8(values[0]),
                ParseInt(values[1]),
                ParseLong(values[2]),
                ParseLong(values[3]),
                ParseLong(values[4]));
        }

        if (script == TransitPeerStateScripts.Release)
        {
            return TransitPeerStateEngine.Release(connectionKey, Utf8(values[0]), ParseLong(values[1]));
        }

        if (script == TransitPeerStateScripts.Renew)
        {
            return TransitPeerStateEngine.Renew(
                connectionKey,
                Utf8(values[0]),
                ParseLong(values[1]),
                ParseLong(values[2]),
                ParseLong(values[3]));
        }

        if (script == TransitPeerStateScripts.ReleaseOwner)
        {
            return TransitPeerStateEngine.ReleaseOwner(connectionKey, Utf8(values[0]));
        }

        throw new NotSupportedException("FakeRedis only evaluates cluster admission scripts.");
    }

    public void Seed(ReadOnlyMemory<byte> key, TimeSpan? expiry = null) =>
        _keys[ToKey(key)] = ([1], DateTimeOffset.UtcNow.Add(expiry ?? TimeSpan.FromHours(2)));

    public bool Contains(ReadOnlyMemory<byte> key) => _keys.ContainsKey(ToKey(key));

    private static string ToKey(ReadOnlyMemory<byte> key) => Convert.ToHexString(key.Span);

    private static string Utf8(ReadOnlyMemory<byte> value) => Encoding.UTF8.GetString(value.Span);

    private static int ParseInt(ReadOnlyMemory<byte> value) =>
        int.Parse(Utf8(value), CultureInfo.InvariantCulture);

    private static long ParseLong(ReadOnlyMemory<byte> value) =>
        long.Parse(Utf8(value), CultureInfo.InvariantCulture);
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
