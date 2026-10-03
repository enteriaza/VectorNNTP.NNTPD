namespace VectorNNTP.BackFiller.Retention;

/// <summary>Result of one <see cref="IArticleRetentionAuthority.RetainCanonical"/> attempt.</summary>
/// <param name="Kind">Admission classification.</param>
/// <param name="Identity">Computed identity when hashing ran.</param>
/// <param name="Fqdn">BackFiller FQDN when the article is or remains available.</param>
/// <param name="VatpPort">TLS VATP listen port when the article is or remains available.</param>
/// <param name="RetainedPayloadBytes">Authority-owned payload bytes after the attempt.</param>
/// <param name="ReleasedPayloadBytes">Bytes released by expiry/eviction during this attempt.</param>
internal readonly record struct ArticleRetentionResult(
    ArticleRetentionKind Kind,
    ArticleIdentity? Identity,
    string? Fqdn,
    int? VatpPort,
    long RetainedPayloadBytes,
    long ReleasedPayloadBytes)
{
    /// <summary>
    /// Returns whether the article is available under the existing or new identity.
    /// True for <see cref="ArticleRetentionKind.Retained"/> and <see cref="ArticleRetentionKind.AlreadyPresent"/>.
    /// </summary>
    internal bool IsAvailable =>
        Kind is ArticleRetentionKind.Retained or ArticleRetentionKind.AlreadyPresent;

    /// <summary>
    /// Returns whether <see cref="Kind"/> is <see cref="ArticleRetentionKind.PayloadExceedsCapacity"/>,
    /// <see cref="ArticleRetentionKind.CapacityUnavailable"/>, or
    /// <see cref="ArticleRetentionKind.OpenableRequestIdLimitExceeded"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ArticleRetentionKind.OpenableRequestIdLimitExceeded"/> is the per-article RequestId bound,
    /// not a payload-byte shortage. The property is still true for that kind.
    /// </remarks>
    internal bool IsCapacityRejected =>
        Kind is ArticleRetentionKind.PayloadExceedsCapacity
            or ArticleRetentionKind.CapacityUnavailable
            or ArticleRetentionKind.OpenableRequestIdLimitExceeded;
}
