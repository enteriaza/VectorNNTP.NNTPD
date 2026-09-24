using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Redis;

namespace VectorNNTP.NNTPD.History;

/// <summary>
/// HistoryDB consumer of <see cref="IRedisService"/>: local memory first, then Redis.
/// </summary>
public sealed class HistoryDb : IHistoryDb
{
    private readonly LocalHistoryStore _local;
    private readonly IRedisService _redis;
    private readonly HistoryWriteQueue _writes;
    private readonly TimeSpan _retention;
    private readonly ILogger<HistoryDb> _logger;

    /// <summary>Initializes a new instance of the <see cref="HistoryDb"/> class.</summary>
    public HistoryDb(
        IRedisService redis,
        IOptions<NntpdOptions> options,
        ILogger<HistoryDb> logger)
        : this(
            redis,
            options?.Value.HistoryTime ?? throw new ArgumentNullException(nameof(options)),
            logger,
            TimeProvider.System,
            new HistoryWriteQueue())
    {
    }

    /// <summary>Initializes a new instance with explicit retention (tests).</summary>
    internal HistoryDb(
        IRedisService redis,
        TimeSpan retention,
        ILogger<HistoryDb> logger,
        TimeProvider timeProvider,
        HistoryWriteQueue writeQueue,
        int maxEntries = LocalHistoryStore.MaxEntries)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(writeQueue);
        _redis = redis;
        _retention = retention;
        _logger = logger;
        _local = new LocalHistoryStore(retention, timeProvider, maxEntries);
        _writes = writeQueue;
    }

    /// <summary>Gets the Redis write queue drained by <see cref="HistoryWriteService"/>.</summary>
    internal HistoryWriteQueue Writes => _writes;

    /// <summary>Gets the configured retention period.</summary>
    internal TimeSpan Retention => _retention;

    /// <summary>Gets the local store (tests and maintenance).</summary>
    internal LocalHistoryStore Local => _local;

    /// <inheritdoc />
    public bool ContainsLocal(in HistoryDigest digest) => _local.Contains(digest);

    /// <summary>Removes expired local entries. Invoked by <see cref="HistoryMaintenanceService"/> only.</summary>
    internal int Maintain() => _local.Maintain();

    /// <inheritdoc />
    public ValueTask<HistoryLookupResult> LookupAsync(
        ReadOnlyMemory<byte> messageId,
        CancellationToken cancellationToken = default)
    {
        var digest = HistoryDigest.FromMessageId(messageId.Span);
        if (_local.Contains(digest))
        {
            return new ValueTask<HistoryLookupResult>(HistoryLookupResult.Seen);
        }

        if (!_redis.TryBeginOperation(out var isRecoveryProbe))
        {
            return new ValueTask<HistoryLookupResult>(HistoryLookupResult.Unavailable);
        }

        return LookupRedisAsync(digest, isRecoveryProbe, recordOnMiss: true, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<HistoryLookupResult> PeekAsync(
        ReadOnlyMemory<byte> messageId,
        CancellationToken cancellationToken = default)
    {
        var digest = HistoryDigest.FromMessageId(messageId.Span);
        if (_local.Contains(digest))
        {
            return new ValueTask<HistoryLookupResult>(HistoryLookupResult.Seen);
        }

        if (!_redis.TryBeginOperation(out var isRecoveryProbe))
        {
            return new ValueTask<HistoryLookupResult>(HistoryLookupResult.Unavailable);
        }

        return LookupRedisAsync(digest, isRecoveryProbe, recordOnMiss: false, cancellationToken);
    }

    /// <inheritdoc />
    public void Remember(ReadOnlyMemory<byte> messageId)
    {
        var digest = HistoryDigest.FromMessageId(messageId.Span);
        _local.Add(digest);
        if (!_writes.TryEnqueue(digest))
        {
            HistoryLogMessages.WriteQueueFull(_logger);
        }
    }

    private async ValueTask<HistoryLookupResult> LookupRedisAsync(
        HistoryDigest digest,
        bool isRecoveryProbe,
        bool recordOnMiss,
        CancellationToken cancellationToken)
    {
        try
        {
            var key = HistoryRedisKeys.Create(digest);
            var exists = await _redis.KeyExistsAsync(key, cancellationToken).ConfigureAwait(false);
            _redis.CompleteOperation(isRecoveryProbe, succeeded: true);

            if (exists)
            {
                _local.Add(digest);
                return HistoryLookupResult.Seen;
            }

            if (recordOnMiss)
            {
                _local.Add(digest);
                if (!_writes.TryEnqueue(digest))
                {
                    HistoryLogMessages.WriteQueueFull(_logger);
                }
            }

            return HistoryLookupResult.Unseen;
        }
        catch (OperationCanceledException)
        {
            _redis.AbandonOperation(isRecoveryProbe);
            throw;
        }
        catch (RedisUnavailableException ex)
        {
            _redis.CompleteOperation(isRecoveryProbe, succeeded: false, ex);
            return HistoryLookupResult.Unavailable;
        }
        catch (Exception ex)
        {
            _redis.CompleteOperation(isRecoveryProbe, succeeded: false, ex);
            throw;
        }
    }
}
