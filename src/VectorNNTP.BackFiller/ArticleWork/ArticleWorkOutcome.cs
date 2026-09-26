namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Article-work classification used for disposition planning.
/// </summary>
/// <remarks>
/// Terminal protocol outcomes are <see cref="Success"/>, <see cref="ArticleNotFound"/>,
/// <see cref="InvalidArticle"/>, and <see cref="InvalidRequest"/>. Retryable internal
/// outcomes are <see cref="ProviderFailure"/>, <see cref="Cancelled"/>,
/// <see cref="UnexpectedFailure"/>, and <see cref="RetentionRejected"/>.
/// </remarks>
public enum ArticleWorkOutcome
{
    /// <summary>Article recovered. Phase 3 never produces this from the default handler.</summary>
    Success = 0,

    /// <summary>Provider did not have the article. Not produced in Phase 3.</summary>
    ArticleNotFound = 1,

    /// <summary>Provider returned bytes that failed article validation. Not produced in Phase 3.</summary>
    InvalidArticle = 2,

    /// <summary>Malformed protocol payload or AMQP metadata.</summary>
    InvalidRequest = 3,

    /// <summary>Transient provider/infrastructure failure. No terminal RPC response.</summary>
    ProviderFailure = 4,

    /// <summary>Processing cancelled. No terminal RPC response.</summary>
    Cancelled = 5,

    /// <summary>Unexpected failure. No terminal RPC response.</summary>
    UnexpectedFailure = 6,

    /// <summary>
    /// Retrieval succeeded but retention could not admit the payload.
    /// Distinct from <see cref="ArticleNotFound"/> and <see cref="ProviderFailure"/>.
    /// Phase 5 temporary settlement is NACK requeue; Phase 6 decides the final policy.
    /// </summary>
    RetentionRejected = 7,
}
