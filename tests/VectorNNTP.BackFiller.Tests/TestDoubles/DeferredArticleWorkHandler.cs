using VectorNNTP.BackFiller.ArticleWork;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

/// <summary>
/// Test handler that never invents Success. Returns <see cref="ArticleWorkOutcome.ProviderFailure"/>
/// so the delivery is NACK-requeued without a terminal RPC response.
/// </summary>
internal sealed class DeferredArticleWorkHandler : IArticleWorkHandler
{
    private const string DeferredReason = "Upstream provider retrieval is not implemented.";

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
