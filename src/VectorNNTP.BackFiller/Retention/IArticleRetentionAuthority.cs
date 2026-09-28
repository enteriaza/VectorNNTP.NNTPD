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
    ArticleRetentionResult Retain(string messageId, byte[] payload);

    /// <summary>
    /// Retains a CanonicalV1 article record and attaches a pending VATP RequestId for OPEN.
    /// </summary>
    /// <remarks>
    /// Does not copy ArtData. On AlreadyPresent, the pending RequestId slot is overwritten
    /// when the prior RequestId was not yet consumed.
    /// </remarks>
    ArticleRetentionResult RetainCanonical(
        string messageId,
        Guid requestId,
        VectorNNTP.Common.Articles.ArticleRecord record,
        VectorNNTP.Common.Articles.Parsing.NntpArticleHeaderName selectedDateHeaderName);

    /// <summary>
    /// Resolves a VATP OPEN: RequestId is primary; ArticleId is verified.
    /// Successful OPEN consumes RequestId. Wrong ArticleId does not.
    /// </summary>
    VatpOpenResult TryOpenTransfer(Guid requestId, VectorNNTP.Common.Articles.ArticleId expectedArticleId);

    /// <summary>Cancels a pending RequestId without releasing the Message-ID entry.</summary>
    bool TryCancelPendingRequest(Guid requestId);

    /// <summary>Looks up by exact Message-ID. Expired entries are not returned.</summary>
    ArticleLookupResult TryGetByMessageId(string messageId);

    /// <summary>Looks up by lowercase MD5 hex. Expired entries are not returned.</summary>
    ArticleLookupResult TryGetByMd5(string md5Hex);

    /// <summary>Reclaims TTL-expired entries.</summary>
    long SweepExpired();

    /// <summary>Stops new admissions.</summary>
    void BeginShutdown();
}
