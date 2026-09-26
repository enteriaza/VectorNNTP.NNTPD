namespace VectorNNTP.BackFiller.Retention;

/// <summary>
/// Single owner of retained article bytes, identity, TTL, and capacity.
/// Independent of RabbitMQ, NNTP sessions, and Transit.
/// </summary>
public interface IArticleRetentionAuthority
{
    /// <summary>Gets configured sweep interval.</summary>
    TimeSpan SweepInterval { get; }

    /// <summary>Gets currently owned retained payload bytes.</summary>
    long RetainedPayloadBytes { get; }

    /// <summary>Gets the number of physically retained entries.</summary>
    int RetainedCount { get; }

    /// <summary>
    /// Attempts to take ownership of <paramref name="payload"/> for <paramref name="messageId"/>.
    /// On any non-<see cref="ArticleRetentionKind.Retained"/> result, the caller still owns the payload.
    /// </summary>
    /// <param name="messageId">Exact Message-ID. Not normalized.</param>
    /// <param name="payload">Destuffed article bytes offered for ownership transfer.</param>
    /// <returns>The admission result, including cache URI when available.</returns>
    ArticleRetentionResult Retain(string messageId, byte[] payload);

    /// <summary>Looks up by exact Message-ID. Expired entries are not returned.</summary>
    /// <param name="messageId">Exact Message-ID.</param>
    /// <returns>Found lease, missing, or expired.</returns>
    ArticleLookupResult TryGetByMessageId(string messageId);

    /// <summary>Looks up by lowercase MD5 hex. Expired entries are not returned.</summary>
    /// <param name="md5Hex">32-character lowercase MD5 hex.</param>
    /// <returns>Found lease, missing, or expired.</returns>
    ArticleLookupResult TryGetByMd5(string md5Hex);

    /// <summary>Reclaims TTL-expired entries. Safe to call concurrently with retain/lookup.</summary>
    /// <returns>Payload bytes physically released.</returns>
    long SweepExpired();

    /// <summary>Stops new admissions. Existing entries remain until expiry, eviction, or dispose.</summary>
    void BeginShutdown();
}
