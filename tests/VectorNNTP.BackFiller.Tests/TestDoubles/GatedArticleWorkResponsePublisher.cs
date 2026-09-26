using VectorNNTP.BackFiller.ArticleWork;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

/// <summary>
/// Publisher seam that exposes deterministic publish/confirm holds for shutdown races.
/// </summary>
internal sealed class GatedArticleWorkResponsePublisher : IArticleWorkResponsePublisher
{
    private readonly List<ArticleWorkResponseIntent> _published = [];

    public bool CompletesSuccessPublication { get; set; } = true;

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource? AfterConfirmHold { get; set; }

    public Exception? PublishException { get; set; }

    public IReadOnlyList<ArticleWorkResponseIntent> Published
    {
        get
        {
            lock (_published)
            {
                return [.. _published];
            }
        }
    }

    public async Task PublishAsync(ArticleWorkResponseIntent intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();
        Started.TrySetResult();
        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (PublishException is not null)
        {
            throw PublishException;
        }

        lock (_published)
        {
            _published.Add(intent);
        }

        if (AfterConfirmHold is not null)
        {
            await AfterConfirmHold.Task.ConfigureAwait(false);
        }
    }
}
