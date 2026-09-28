using System.Collections.Concurrent;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

internal sealed class ControllableArticleWorkHandler : IArticleWorkHandler
{
    public ArticleWorkOutcome Outcome { get; set; } = ArticleWorkOutcome.ProviderFailure;

    public string? Error { get; set; }

    public string? CacheUri { get; set; }

    public ArticleId? ArticleId { get; set; }

    public Exception? Throw { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource? Started { get; set; }

    public ConcurrentQueue<ArticleWorkControlStage> Stages { get; } = new();

    public int HandleCount { get; private set; }

    public ArticleWorkItem? LastItem { get; private set; }

    public async ValueTask<ArticleWorkHandlerResult> HandleAsync(
        ArticleWorkItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        LastItem = item;
        HandleCount++;
        if (Stages.TryDequeue(out var stage))
        {
            stage.Started.TrySetResult();
            if (stage.Gate is not null)
            {
                await stage.Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (stage.Throw is not null)
            {
                throw stage.Throw;
            }

            return new ArticleWorkHandlerResult(
                stage.Outcome,
                stage.Error,
                CacheUri: stage.CacheUri,
                ArticleId: stage.ArticleId);
        }

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

        return new ArticleWorkHandlerResult(Outcome, Error, CacheUri: CacheUri, ArticleId: ArticleId);
    }
}

internal sealed record ArticleWorkControlStage(
    TaskCompletionSource Started,
    TaskCompletionSource? Gate,
    ArticleWorkOutcome Outcome,
    string? Error = null,
    string? CacheUri = null,
    Exception? Throw = null,
    ArticleId? ArticleId = null);
