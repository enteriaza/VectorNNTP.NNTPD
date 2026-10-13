using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.ArticleIndex;

/// <summary>Result of asking to own ingestion of one hash.</summary>
internal enum ArticleIngestionHold : byte
{
    /// <summary>This caller owns the in-flight reservation. Release it on success or failure.</summary>
    Acquired = 1,

    /// <summary>The hash is already in the membership index. Do not write another copy.</summary>
    AlreadyPresent = 2,
}

/// <summary>
/// Transient per-hash ingestion reservations. Published membership is a different structure.
/// </summary>
/// <remarks>
/// Only hashes currently being ingested occupy a slot. Unrelated hashes take different shard locks.
/// A waiter resumes after the holder releases the reservation and then observes membership again.
/// </remarks>
internal sealed class ArticleIngestionReservations
{
    private readonly ReservationShard[] _shards = new ReservationShard[ArticlePresenceIndex.ShardCount];

    /// <summary>Initializes an empty reservation table.</summary>
    internal ArticleIngestionReservations()
    {
        for (var i = 0; i < _shards.Length; i++)
        {
            _shards[i] = new ReservationShard();
        }
    }

    /// <summary>
    /// Waits until <paramref name="articleId"/> is published or this caller owns its reservation.
    /// </summary>
    /// <param name="articleId">Canonical article identity.</param>
    /// <param name="presence">Published membership. A hit does not create a reservation and does not stat the payload.</param>
    /// <param name="cancellationToken">Cancels the wait. A cancelled wait does not own the reservation.</param>
    /// <returns>Whether the caller must ingest or treat the article as already stored.</returns>
    internal async ValueTask<ArticleIngestionHold> ReserveAsync(
        ArticleId articleId,
        ArticlePresenceIndex presence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presence);
        var shard = _shards[Shard(articleId)];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? wait = null;
            lock (shard.Gate)
            {
                if (presence.Contains(articleId))
                {
                    return ArticleIngestionHold.AlreadyPresent;
                }

                if (shard.InFlight.TryGetValue(articleId, out var pending))
                {
                    wait = pending.Task;
                    OnWaiting?.Invoke();
                }
                else
                {
                    shard.InFlight.Add(articleId, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    return ArticleIngestionHold.Acquired;
                }
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Gets or sets a test signal invoked under the shard lock when a caller is about to wait.</summary>
    internal Action? OnWaiting { get; set; }

    /// <summary>Drops a reservation this caller acquired. Published membership is left unchanged.</summary>
    /// <param name="articleId">Identity passed to <see cref="ReserveAsync"/>.</param>
    internal void Release(ArticleId articleId)
    {
        var shard = _shards[Shard(articleId)];
        TaskCompletionSource? wake;
        lock (shard.Gate)
        {
            if (!shard.InFlight.Remove(articleId, out wake))
            {
                return;
            }
        }

        wake.TrySetResult();
    }

    /// <summary>Selects the same shard as membership, from the first digest byte.</summary>
    /// <param name="articleId">Canonical article identity.</param>
    /// <returns>Shard index.</returns>
    private static int Shard(ArticleId articleId)
    {
        Span<byte> hash = stackalloc byte[ArticleIndexSettings.HashLength];
        articleId.CopyTo(hash);
        return hash[0] & (ArticlePresenceIndex.ShardCount - 1);
    }

    /// <summary>One shard's in-flight hashes. The lock is not shared with other shards.</summary>
    private sealed class ReservationShard
    {
        /// <summary>Gets the lock for this shard's dictionary.</summary>
        internal object Gate { get; } = new();

        /// <summary>Gets hashes reserved on this shard and the signal released to their waiters.</summary>
        internal Dictionary<ArticleId, TaskCompletionSource> InFlight { get; } = new();
    }
}
