namespace VectorNNTP.BackFiller.Retention;

/// <summary>Result of one <see cref="IArticleRetentionAuthority.Retain"/> attempt.</summary>
/// <param name="Kind">Admission classification.</param>
/// <param name="Identity">Computed identity when hashing ran.</param>
/// <param name="CacheUri">URI when the article is or remains available.</param>
/// <param name="RetainedPayloadBytes">Authority-owned payload bytes after the attempt.</param>
/// <param name="ReleasedPayloadBytes">Bytes released by expiry/eviction during this attempt.</param>
public readonly record struct ArticleRetentionResult(
    ArticleRetentionKind Kind,
    ArticleIdentity? Identity,
    string? CacheUri,
    long RetainedPayloadBytes,
    long ReleasedPayloadBytes)
{
    /// <summary>Returns whether the article is available under the existing or new identity.</summary>
    public bool IsAvailable =>
        Kind is ArticleRetentionKind.Retained or ArticleRetentionKind.AlreadyPresent;

    /// <summary>Returns whether admission failed because of capacity.</summary>
    public bool IsCapacityRejected =>
        Kind is ArticleRetentionKind.PayloadExceedsCapacity or ArticleRetentionKind.CapacityUnavailable;
}
