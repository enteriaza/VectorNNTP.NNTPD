namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Identities recovered from a payload without synthesizing missing values.
/// </summary>
/// <param name="RequestId">Parsed non-empty GUID when present and valid; otherwise <see langword="null"/>.</param>
/// <param name="MessageId">Exact Message-ID when it passed validation; otherwise <see langword="null"/>.</param>
/// <param name="Backbone">JSON backbone string when present and non-empty; otherwise <see langword="null"/>.</param>
internal readonly record struct ArticleWorkParsedIdentities(
    Guid? RequestId,
    string? MessageId,
    string? Backbone);

/// <summary>
/// Invalid-request parse failure. Identities are only those actually recovered.
/// </summary>
/// <param name="Reason">Validation reason. Must not contain the raw payload.</param>
/// <param name="Identities">Recovered identities. Missing fields stay null.</param>
internal sealed record ArticleWorkParseFailure(
    string Reason,
    ArticleWorkParsedIdentities Identities);

/// <summary>
/// Result of parsing one delivery against the v1 request contract.
/// </summary>
/// <param name="Request">Validated request when parsing succeeds.</param>
/// <param name="Failure">Failure when the delivery is <see cref="ArticleWorkOutcome.InvalidRequest"/>.</param>
internal sealed record ArticleWorkParseResult(
    ArticleWorkRequest? Request,
    ArticleWorkParseFailure? Failure)
{
    /// <summary>Gets a value indicating whether a validated request was produced.</summary>
    internal bool IsValid => Request is not null && Failure is null;

    /// <summary>Creates a successful parse result.</summary>
    /// <param name="request">Validated request.</param>
    /// <returns>A valid parse result whose failure is null.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    internal static ArticleWorkParseResult Valid(ArticleWorkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ArticleWorkParseResult(request, null);
    }

    /// <summary>Creates an invalid-request parse result without inventing identities.</summary>
    /// <param name="reason">Rejection reason. Must not be null or whitespace, and must not contain the raw payload.</param>
    /// <param name="identities">Recovered identities only.</param>
    /// <returns>An invalid parse result whose request is null.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reason"/> is null or whitespace.</exception>
    internal static ArticleWorkParseResult Invalid(string reason, ArticleWorkParsedIdentities identities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ArticleWorkParseResult(null, new ArticleWorkParseFailure(reason, identities));
    }
}
