using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Articles.Processing;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Retrieves an ARTICLE, validates and materializes it through the Common article pipeline, then retains the canonical bytes.
/// </summary>
public sealed class ProviderArticleWorkHandler : IArticleWorkHandler
{
    private readonly INntpArticleRetriever _retriever;
    private readonly IArticleRetentionAuthority _retention;
    private readonly NntpArticleParser _parser;

    /// <summary>Initializes the handler with the test-host Path identity <c>backfiller.test</c>.</summary>
    public ProviderArticleWorkHandler(INntpArticleRetriever retriever, IArticleRetentionAuthority retention)
        : this(retriever, retention, new NntpArticleParser("backfiller.test"))
    {
    }

    /// <summary>Initializes the handler.</summary>
    public ProviderArticleWorkHandler(
        INntpArticleRetriever retriever,
        IArticleRetentionAuthority retention,
        NntpArticleParser parser)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(parser);
        _retriever = retriever;
        _retention = retention;
        _parser = parser;
    }

    /// <summary>Gets the last retrieval classification (tests).</summary>
    public ArticleRetrievalKind? LastKind { get; private set; }

    /// <summary>Gets the last canonical retained payload (tests). The same buffer is transferred into retention.</summary>
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
        LastPayload = null;
        if (cancellationToken.IsCancellationRequested)
        {
            LastKind = ArticleRetrievalKind.Cancelled;
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
            return new ArticleWorkHandlerResult(ArticleWorkOutcome.Cancelled, null);
        }

        using (retrieval)
        {
            LastKind = retrieval.Kind;
            if (retrieval.Kind != ArticleRetrievalKind.ArticleRetrieved)
            {
                return MapRetrieval(retrieval);
            }

            if (retrieval.Article is null)
            {
                LastRetentionKind = ArticleRetentionKind.InvalidPayload;
                return new ArticleWorkHandlerResult(
                    ArticleWorkOutcome.RetentionRejected,
                    "Retrieved article payload could not be transferred into retention.");
            }

            var parse = _parser.Parse(retrieval.Article.Memory);
            if (!parse.IsAccepted)
            {
                return MapParseFailure(parse.FailureCode);
            }

            if (!NntpArticleIdentity.MatchesRequest(parse.OriginalMessageIdValue.Span, item.Request.MessageId))
            {
                return new ArticleWorkHandlerResult(
                    ArticleWorkOutcome.InvalidArticle,
                    "MessageIdMismatch");
            }

            var materialized = NntpArticleCanonicalMaterializer.Materialize(parse);
            if (!materialized.IsAccepted || materialized.ArticleBytes is null)
            {
                return new ArticleWorkHandlerResult(
                    ArticleWorkOutcome.InvalidArticle,
                    materialized.FailureCode.ToString());
            }

            var payload = materialized.ArticleBytes;
            LastPayload = payload;
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

    private static ArticleWorkHandlerResult MapParseFailure(NntpArticleParseFailureCode failureCode)
    {
        return new ArticleWorkHandlerResult(
            ArticleWorkOutcome.InvalidArticle,
            failureCode.ToString());
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
