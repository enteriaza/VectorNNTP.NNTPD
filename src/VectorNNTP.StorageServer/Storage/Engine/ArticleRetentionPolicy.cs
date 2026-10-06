namespace VectorNNTP.StorageServer.Storage.Engine;

/// <summary>
/// Decides whether a bulk-committed article is eligible to leave authoritative retention.
/// </summary>
/// <remarks>
/// <para>
/// The decision is pure. It does not read disks, update the index, touch the cache, or delete
/// bytes. The maintenance batch is the executor: it calls this method and then the existing
/// <see cref="ArticleStorageState.Evicted"/> transition. Until that transition, a bulk article
/// stays <see cref="ArticleStorageState.Present"/> and readable.
/// </para>
/// <para>
/// Arrival time is the journal Accept instant. <c>IndexCommitted</c> releases the Accept
/// record, and the index keeps that same instant on
/// <see cref="StoredArticleMetadata.AcceptedUtc"/>. Schema 2 rows have no field and restore as
/// <see cref="DateTimeOffset.MinValue"/>. This predicate does not read the index. The maintenance
/// batch passes the stored instant and evicts only when this method returns true.
/// <see cref="StoredArticleMetadata.LastAccessUtc"/> is a soft hint: reads update it only in
/// memory, and the durable copy is the publication or tombstone time. It is not an arrival time.
/// </para>
/// <para>
/// A wall clock behind the arrival instant does not expire the article. A wall clock ahead of
/// it can make the same stored arrival eligible sooner. The arrival instant itself is not
/// rewritten. Future coarse recency already exists only in process: <c>TouchHint</c> and
/// <c>UseCount</c>. Neither is durable, and neither is an input to this predicate.
/// </para>
/// </remarks>
public static class ArticleRetentionPolicy
{
    /// <summary>
    /// Returns whether <paramref name="arrivalUtc"/> has reached <paramref name="maxRetentionAge"/>
    /// for a bulk-committed article.
    /// </summary>
    /// <param name="bulkCommitted">True only when the article's published bulk location is authoritative.</param>
    /// <param name="arrivalUtc">
    /// Durable Accept instant. Null, or <see cref="DateTimeOffset.MinValue"/>, means the instant
    /// cannot be established.
    /// </param>
    /// <param name="maxRetentionAge">
    /// Configured maximum age. <see cref="TimeSpan.Zero"/> or a negative value disables expiration.
    /// </param>
    /// <param name="nowUtc">Evaluation instant. Not taken from an article Date header.</param>
    /// <returns>
    /// True when the article is bulk-committed, the policy is enabled, the arrival instant is
    /// known and not after <paramref name="nowUtc"/>, and the elapsed time is at least
    /// <paramref name="maxRetentionAge"/>.
    /// </returns>
    public static bool IsExpirationEligible(
        bool bulkCommitted,
        DateTimeOffset? arrivalUtc,
        TimeSpan maxRetentionAge,
        DateTimeOffset nowUtc)
    {
        if (!bulkCommitted || maxRetentionAge <= TimeSpan.Zero || arrivalUtc is not { } arrival)
        {
            return false;
        }

        if (arrival == DateTimeOffset.MinValue || arrival.UtcTicks > nowUtc.UtcTicks)
        {
            return false;
        }

        return nowUtc.UtcTicks - arrival.UtcTicks >= maxRetentionAge.Ticks;
    }
}
