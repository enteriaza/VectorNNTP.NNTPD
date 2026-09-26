namespace VectorNNTP.NNTPD.RabbitMq.ArticleWork;

/// <summary>Structured article-work RPC result returned to NNTP command callers.</summary>
/// <remarks>
/// This type does not contain article bytes. A later phase consumes <see cref="Uri"/>
/// for retrieval. This phase only classifies the RPC outcome.
/// </remarks>
/// <param name="Outcome">Classified RPC outcome.</param>
/// <param name="RequestId">Application request identity used for the lookup.</param>
/// <param name="MessageId">Message-ID that was queried.</param>
/// <param name="Backbone">Winning source backbone when a wire response completed the operation.</param>
/// <param name="Uri">Success-only cache URI from the winning response.</param>
/// <param name="Error">Failure detail when the classified outcome is not success.</param>
/// <param name="SourceExchange">Exchange that produced the winning response, when applicable.</param>
public sealed record ArticleWorkRpcResult(
    ArticleWorkOutcome Outcome,
    Guid RequestId,
    string MessageId,
    string? Backbone,
    string? Uri,
    string? Error,
    string? SourceExchange)
{
    /// <summary>Builds an overall not-found result after the lookup deadline elapses.</summary>
    internal static ArticleWorkRpcResult NotFound(Guid requestId, string messageId, string error) =>
        new(ArticleWorkOutcome.ArticleNotFound, requestId, messageId, Backbone: null, Uri: null, error, SourceExchange: null);
}
