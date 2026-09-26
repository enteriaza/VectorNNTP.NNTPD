using VectorNNTP.BackFiller.ArticleWork;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

internal sealed class ControllableArticleWorkHandler : IArticleWorkHandler
{
    public ArticleWorkOutcome Outcome { get; set; } = ArticleWorkOutcome.ProviderFailure;

    public string? Error { get; set; }

    public Exception? Throw { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource? Started { get; set; }

    public int HandleCount { get; private set; }

    public ArticleWorkItem? LastItem { get; private set; }

    public async ValueTask<ArticleWorkHandlerResult> HandleAsync(
        ArticleWorkItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        LastItem = item;
        HandleCount++;
        Started?.TrySetResult();
        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Throw is not null)
        {
            throw Throw;
        }

        return new ArticleWorkHandlerResult(Outcome, Error);
    }
}
