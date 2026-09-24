using System.Runtime.InteropServices;
using StackExchange.Redis;

namespace VectorNNTP.NNTPD.Redis;

/// <summary>Adapts <see cref="IDatabase"/> to <see cref="IRedisDatabase"/>.</summary>
internal sealed class StackExchangeRedisDatabase : IRedisDatabase
{
    private readonly IDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="StackExchangeRedisDatabase"/> class.</summary>
    public StackExchangeRedisDatabase(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc />
    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default) =>
        await Await(_database.PingAsync(), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<bool> KeyExistsAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = _database.KeyExistsAsync(ToRedisKey(key));
            return AwaitExists(pending, cancellationToken);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            return ValueTask.FromException<bool>(new RedisUnavailableException("Redis EXISTS failed.", ex));
        }
    }

    /// <inheritdoc />
    public ValueTask SetAsync(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = _database.StringSetAsync(ToRedisKey(key), ToRedisValue(value), expiry);
            return AwaitSet(pending, cancellationToken);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            return ValueTask.FromException(new RedisUnavailableException("Redis SET failed.", ex));
        }
    }

    private static RedisKey ToRedisKey(ReadOnlyMemory<byte> key)
    {
        // RedisKey is byte[]-backed and has no ReadOnlyMemory implicit.
        // HistoryRedisKeys.Create returns a dedicated array; reuse it.
        if (MemoryMarshal.TryGetArray(key, out var segment)
            && segment.Array is { } array
            && segment.Offset == 0
            && segment.Count == array.Length)
        {
            return (RedisKey)array;
        }

        return (RedisKey)key.ToArray();
    }

    private static RedisValue ToRedisValue(ReadOnlyMemory<byte> value) =>
        value.IsEmpty ? RedisValue.EmptyString : (RedisValue)value;

    private static ValueTask<bool> AwaitExists(Task<bool> pending, CancellationToken cancellationToken)
    {
        if (pending.IsCompletedSuccessfully)
        {
            return new ValueTask<bool>(pending.Result);
        }

        return AwaitExistsSlow(pending, cancellationToken);
    }

    private static async ValueTask<bool> AwaitExistsSlow(Task<bool> pending, CancellationToken cancellationToken)
    {
        try
        {
            return await Await(pending, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            throw new RedisUnavailableException("Redis EXISTS failed.", ex);
        }
    }

    private static ValueTask AwaitSet(Task<bool> pending, CancellationToken cancellationToken)
    {
        if (pending.IsCompletedSuccessfully)
        {
            return ValueTask.CompletedTask;
        }

        return AwaitSetSlow(pending, cancellationToken);
    }

    private static async ValueTask AwaitSetSlow(Task<bool> pending, CancellationToken cancellationToken)
    {
        try
        {
            _ = await Await(pending, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            throw new RedisUnavailableException("Redis SET failed.", ex);
        }
    }

    private static Task<T> Await<T>(Task<T> pending, CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled ? pending.WaitAsync(cancellationToken) : pending;

    private static bool IsInfrastructureFailure(Exception exception) =>
        exception is RedisException or RedisTimeoutException or TimeoutException or IOException;
}
