using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Retention;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Phase 5 handler: retrieves, retains, and exposes a cache URI. Does not complete Success publication.
/// </summary>
public sealed class ProviderArticleWorkHandler : IArticleWorkHandler
{
    private readonly INntpArticleRetriever _retriever;
    private readonly IArticleRetentionAuthority _retention;

    /// <summary>Initializes the handler.</summary>
    public ProviderArticleWorkHandler(INntpArticleRetriever retriever, IArticleRetentionAuthority retention)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(retention);
        _retriever = retriever;
        _retention = retention;
    }

    /// <summary>Gets the last retrieval classification (tests).</summary>
    public ArticleRetrievalKind? LastKind { get; private set; }

    /// <summary>Gets a copy of the last retrieved payload (tests). Independent of the session.</summary>
    public byte[]? LastPayload { get; private set; }

    /// <summary>Gets the last retention classification (tests).</summary>
    public ArticleRetentionKind? LastRetentionKind { get; private set; }

    /// <summary>Gets the last cache URI (tests).</summary>
    public string? LastCacheUri { get; private set; }

    /// <inheritdoc />
    public async ValueTask<ArticleWorkHandlerResult> HandleAsync(
        ArticleWorkItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        LastRetentionKind = null;
        LastCacheUri = null;
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
            if (retrieval.Kind != ArticleRetrievalKind.ArticleRetrieved)
            {
                return MapRetrieval(retrieval);
            }

            if (retrieval.Article is null || !retrieval.Article.TryDetach(out var payload))
            {
                LastRetentionKind = ArticleRetentionKind.InvalidPayload;
                return new ArticleWorkHandlerResult(
                    ArticleWorkOutcome.RetentionRejected,
                    "Retrieved article payload could not be transferred into retention.");
            }

            var retained = _retention.Retain(item.Request.MessageId, payload);
            LastRetentionKind = retained.Kind;
            LastCacheUri = retained.CacheUri;
            if (retained.IsAvailable)
            {
                return new ArticleWorkHandlerResult(
                    ArticleWorkOutcome.Success,
                    null,
                    Article: null,
                    CacheUri: retained.CacheUri);
            }

            return new ArticleWorkHandlerResult(
                ArticleWorkOutcome.RetentionRejected,
                retained.Kind.ToString(),
                Article: null,
                CacheUri: null);
        }
    }

    private static ArticleWorkHandlerResult MapRetrieval(ArticleRetrievalResult retrieval)
    {
        return retrieval.Kind switch
        {
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
