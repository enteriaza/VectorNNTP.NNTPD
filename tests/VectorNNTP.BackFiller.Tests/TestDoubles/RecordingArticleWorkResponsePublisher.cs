using VectorNNTP.BackFiller.ArticleWork;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

/// <summary>
/// In-process recorder used by tests that do not exercise broker publication.
/// </summary>
internal sealed class RecordingArticleWorkResponsePublisher : IArticleWorkResponsePublisher
{
    private readonly List<ArticleWorkResponseIntent> _published = [];

    /// <summary>Gets recorded publish intents in admission order.</summary>
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

    /// <inheritdoc />
    public bool CompletesSuccessPublication { get; set; }

    /// <summary>When set, the next publication throws this exception.</summary>
    public Exception? PublishException { get; set; }

    /// <inheritdoc />
    public Task PublishAsync(ArticleWorkResponseIntent intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();
        if (PublishException is not null)
        {
            throw PublishException;
        }

        lock (_published)
        {
            _published.Add(intent);
        }

        return Task.CompletedTask;
    }
}
