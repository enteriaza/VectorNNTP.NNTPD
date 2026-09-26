using VectorNNTP.BackFiller.Nntp;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Phase 4 handler: retrieves from a backbone provider and maps the result onto Article Work outcomes.
/// Successful ARTICLE does not complete Success publication; the pipeline must not ACK yet.
/// </summary>
public sealed class ProviderArticleWorkHandler : IArticleWorkHandler
{
    private readonly INntpArticleRetriever _retriever;

    /// <summary>Initializes the handler.</summary>
    public ProviderArticleWorkHandler(INntpArticleRetriever retriever)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        _retriever = retriever;
    }

    /// <summary>Gets the last retrieval classification (tests).</summary>
    public ArticleRetrievalKind? LastKind { get; private set; }

    /// <summary>Gets a copy of the last retrieved payload (tests). Independent of the session.</summary>
    public byte[]? LastPayload { get; private set; }

    /// <inheritdoc />
    public async ValueTask<ArticleWorkHandlerResult> HandleAsync(
        ArticleWorkItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (cancellationToken.IsCancellationRequested)
        {
            LastKind = ArticleRetrievalKind.Cancelled;
            LastPayload = null;
            return new ArticleWorkHandlerResult(ArticleWorkOutcome.Cancelled, null);
        }

        ArticleRetrievalResult retrieval;
        try
        {
            retrieval = await _retriever.RetrieveAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LastKind = ArticleRetrievalKind.Cancelled;
            LastPayload = null;
            return new ArticleWorkHandlerResult(ArticleWorkOutcome.Cancelled, null);
        }

        using (retrieval)
        {
            LastKind = retrieval.Kind;
            LastPayload = retrieval.Article is null ? null : retrieval.Article.Memory.ToArray();
            return Map(retrieval);
        }
    }

    private static ArticleWorkHandlerResult Map(ArticleRetrievalResult retrieval)
    {
        return retrieval.Kind switch
        {
            ArticleRetrievalKind.ArticleRetrieved => new ArticleWorkHandlerResult(
                ArticleWorkOutcome.Success,
                null,
                retrieval.Article is null ? null : new RetrievedArticle(retrieval.Article.Memory.ToArray())),
            ArticleRetrievalKind.ArticleNotFound => new ArticleWorkHandlerResult(
                ArticleWorkOutcome.ArticleNotFound,
                retrieval.Reason),
            ArticleRetrievalKind.InvalidArticle => new ArticleWorkHandlerResult(
                ArticleWorkOutcome.InvalidArticle,
                retrieval.Reason),
            ArticleRetrievalKind.Cancelled => new ArticleWorkHandlerResult(
                ArticleWorkOutcome.Cancelled,
                null),
            ArticleRetrievalKind.AuthenticationFailure => new ArticleWorkHandlerResult(
                ArticleWorkOutcome.ProviderFailure,
                retrieval.Reason),
            _ => new ArticleWorkHandlerResult(ArticleWorkOutcome.ProviderFailure, retrieval.Reason),
        };
    }
}
