namespace VectorNNTP.BackFiller.Retention;

/// <summary>
/// Single owner of retained CanonicalV1 <see cref="VectorNNTP.Common.Articles.ArticleRecord"/> values,
/// identity, TTL, and capacity. Independent of RabbitMQ and NNTP sessions.
/// VATP OPEN is the only data-plane consumer of retained ArtData.
/// </summary>
public interface IArticleRetentionAuthority
{
    /// <summary>Gets configured sweep interval.</summary>
    TimeSpan SweepInterval { get; }

    /// <summary>Gets currently owned retained ArtData bytes.</summary>
    long RetainedPayloadBytes { get; }

    /// <summary>Gets the number of physically retained entries.</summary>
    int RetainedCount { get; }

    /// <summary>
    /// Retains a CanonicalV1 article record and attaches a pending VATP RequestId for OPEN.
    /// </summary>
    /// <remarks>
    /// Does not copy ArtData. On AlreadyPresent, the existing retained
    /// <see cref="VectorNNTP.Common.Articles.ArticleRecord"/> remains authoritative (first-wins);
    /// only the pending RequestId slot is overwritten when the prior RequestId was not yet consumed.
    /// The caller retains ownership of the unused incoming ArtData buffer on AlreadyPresent.
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

    /// <summary>Reclaims TTL-expired entries.</summary>
    long SweepExpired();

    /// <summary>Stops new admissions.</summary>
    void BeginShutdown();
}
