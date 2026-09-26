namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Identities recovered from a payload without synthesizing missing values.
/// </summary>
/// <param name="RequestId">Parsed non-empty GUID when present and valid; otherwise <see langword="null"/>.</param>
/// <param name="MessageId">Exact Message-ID when it passed validation; otherwise <see langword="null"/>.</param>
/// <param name="Backbone">JSON backbone string when present and non-empty; otherwise <see langword="null"/>.</param>
public readonly record struct ArticleWorkParsedIdentities(
    Guid? RequestId,
    string? MessageId,
    string? Backbone);

/// <summary>
/// Invalid-request parse failure. Identities are only those actually recovered.
/// </summary>
/// <param name="Reason">Validation reason. Must not contain the raw payload.</param>
/// <param name="Identities">Recovered identities. Missing fields stay null.</param>
public sealed record ArticleWorkParseFailure(
    string Reason,
    ArticleWorkParsedIdentities Identities);

/// <summary>
/// Result of parsing one delivery against the v1 request contract.
/// </summary>
/// <param name="Request">Validated request when parsing succeeds.</param>
/// <param name="Failure">Failure when the delivery is <see cref="ArticleWorkOutcome.InvalidRequest"/>.</param>
public sealed record ArticleWorkParseResult(
    ArticleWorkRequest? Request,
    ArticleWorkParseFailure? Failure)
{
    /// <summary>Gets a value indicating whether a validated request was produced.</summary>
    public bool IsValid => Request is not null && Failure is null;

    /// <summary>Creates a successful parse result.</summary>
    /// <param name="request">Validated request.</param>
    /// <returns>A valid parse result.</returns>
    public static ArticleWorkParseResult Valid(ArticleWorkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ArticleWorkParseResult(request, null);
    }

    /// <summary>Creates an invalid-request parse result without inventing identities.</summary>
    /// <param name="reason">Rejection reason.</param>
    /// <param name="identities">Recovered identities only.</param>
    /// <returns>An invalid parse result.</returns>
    public static ArticleWorkParseResult Invalid(string reason, ArticleWorkParsedIdentities identities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ArticleWorkParseResult(null, new ArticleWorkParseFailure(reason, identities));
    }
}
