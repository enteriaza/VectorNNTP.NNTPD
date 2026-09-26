namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Result of handling an already-validated work item.
/// </summary>
/// <param name="Outcome">Processing outcome. Must not be <see cref="ArticleWorkOutcome.InvalidRequest"/>.</param>
/// <param name="Error">Optional diagnostic reason. Never a secret.</param>
public readonly record struct ArticleWorkHandlerResult(ArticleWorkOutcome Outcome, string? Error);

/// <summary>
/// Processes admitted Article Work. Phase 3 does not retrieve articles.
/// </summary>
public interface IArticleWorkHandler
{
    /// <summary>
    /// Handles one validated work item.
    /// </summary>
    /// <param name="item">Admitted work.</param>
    /// <param name="cancellationToken">Processing cancellation. Distinct from host shutdown only when linked that way by the caller.</param>
    /// <returns>The processing outcome used for disposition.</returns>
    ValueTask<ArticleWorkHandlerResult> HandleAsync(ArticleWorkItem item, CancellationToken cancellationToken);
}

/// <summary>
/// Phase 3 default handler. Does not invent Success. Returns <see cref="ArticleWorkOutcome.ProviderFailure"/>
/// so the delivery is NACK-requeued without a terminal RPC response.
/// </summary>
public sealed class DeferredArticleWorkHandler : IArticleWorkHandler
{
    /// <summary>Reason recorded for the deferred provider path.</summary>
    public const string DeferredReason = "Upstream provider retrieval is not implemented.";

    /// <inheritdoc />
    public ValueTask<ArticleWorkHandlerResult> HandleAsync(ArticleWorkItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(new ArticleWorkHandlerResult(ArticleWorkOutcome.Cancelled, null));
        }

        return ValueTask.FromResult(new ArticleWorkHandlerResult(ArticleWorkOutcome.ProviderFailure, DeferredReason));
    }
}
