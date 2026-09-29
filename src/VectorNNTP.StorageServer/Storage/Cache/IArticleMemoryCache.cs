using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.Cache;

/// <summary>
/// Process-local, volatile, size-bounded LRU cache of complete CanonicalV1 articles.
/// </summary>
/// <remarks>
/// Non-durable. Restart empties the cache. The durable storage engine remains authoritative.
/// Does not perform IO.
/// </remarks>
public interface IArticleMemoryCache
{
    /// <summary>Gets the configured maximum ArtData bytes (0 = disabled).</summary>
    long MaxBytes { get; }

    /// <summary>Gets the sum of cached ArtData lengths.</summary>
    long CurrentBytes { get; }

    /// <summary>Gets the number of cached articles.</summary>
    int Count { get; }

    /// <summary>
    /// Looks up <paramref name="artId"/> and marks it most-recently-used on hit.
    /// </summary>
    bool TryGet(ArticleId artId, out ArticleRecord record);

    /// <summary>
    /// Inserts or refreshes a CanonicalV1 article. Evicts least-recently-used entries as needed.
    /// </summary>
    ArticleMemoryCachePutOutcome Put(in ArticleRecord record);

    /// <summary>Removes <paramref name="artId"/> when present.</summary>
    bool Remove(ArticleId artId);

    /// <summary>Removes all entries and resets byte accounting.</summary>
    void Clear();
}

/// <summary>Outcome of <see cref="IArticleMemoryCache.Put"/>.</summary>
public enum ArticleMemoryCachePutOutcome : byte
{
    /// <summary>Article inserted (may have evicted LRU entries).</summary>
    Inserted = 1,

    /// <summary>Identical ArtId/ArtHash/ArtSize/ArtData already present; LRU refreshed.</summary>
    IdempotentNoOp = 2,

    /// <summary>ArtData length exceeds <see cref="IArticleMemoryCache.MaxBytes"/>; cache untouched.</summary>
    RejectedOversized = 3,

    /// <summary>Cache disabled (<c>MaxBytes == 0</c>); nothing retained.</summary>
    RejectedDisabled = 4,

    /// <summary>Not CanonicalV1 or failed article integrity proof.</summary>
    RejectedInvalid = 5,

    /// <summary>Same ArtId with conflicting ArtHash/ArtSize/ArtData; existing entry kept.</summary>
    RejectedConflict = 6,
}
