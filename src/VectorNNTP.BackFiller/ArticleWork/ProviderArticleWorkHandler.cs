using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Articles.Processing;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Retrieves an ARTICLE, builds a CanonicalV1 <see cref="ArticleRecord"/>, then retains it for
/// VATP OPEN by RequestId. Article Work Success publishes this BackFiller's FQDN, VATP port, and the
/// lowercase hexadecimal <see cref="ArticleId"/>. Those fields are routing metadata, not a transfer protocol.
/// </summary>
internal sealed class ProviderArticleWorkHandler : IArticleWorkHandler
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
    internal ArticleRetrievalKind? LastKind { get; private set; }

    /// <summary>Gets the last canonical retained ArtData (tests). The same buffer is transferred into retention.</summary>
    internal byte[]? LastPayload { get; private set; }

    /// <summary>Gets the last retained CanonicalV1 record (tests).</summary>
    internal ArticleRecord? LastRecord { get; private set; }

    /// <summary>Gets the last retention classification (tests).</summary>
    internal ArticleRetentionKind? LastRetentionKind { get; private set; }

    /// <summary>Gets the last retained BackFiller FQDN (tests).</summary>
    internal string? LastFqdn { get; private set; }

    /// <summary>Gets the last retained VATP listen port (tests).</summary>
    internal int? LastVatpPort { get; private set; }

    /// <inheritdoc />
    public async ValueTask<ArticleWorkHandlerResult> HandleAsync(
        ArticleWorkItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        LastRetentionKind = null;
        LastFqdn = null;
        LastVatpPort = null;
        LastPayload = null;
        LastRecord = null;
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

            var created = ArticleRecordFactory.TryCreate(_parser, retrieval.Article.Memory, ArticlePathMode.Traverse);
            if (!created.IsAccepted)
            {
                if (created.ParseFailure != NntpArticleParseFailureCode.None)
                {
                    return MapParseFailure(created.ParseFailure);
                }

                return new ArticleWorkHandlerResult(
                    ArticleWorkOutcome.InvalidArticle,
                    created.MaterializeFailure.ToString());
            }

            var record = created.Record;
            if (!NntpArticleIdentity.MatchesRequest(record.MessageId, item.Request.MessageId))
            {
                return new ArticleWorkHandlerResult(
                    ArticleWorkOutcome.InvalidArticle,
                    "MessageIdMismatch");
            }

            if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(record.ArtData, out var segment)
                || segment.Array is null
                || segment.Offset != 0
                || segment.Count != record.ArtSize)
            {
                LastRetentionKind = ArticleRetentionKind.InvalidPayload;
                return new ArticleWorkHandlerResult(
                    ArticleWorkOutcome.RetentionRejected,
                    "Canonical ArtData is not an owned contiguous buffer.");
            }

            LastPayload = segment.Array;
            LastRecord = record;
            var retained = _retention.RetainCanonical(
                item.Request.MessageId,
                item.Request.RequestId,
                record,
                created.SelectedDateHeaderName);
            LastRetentionKind = retained.Kind;
            LastFqdn = retained.Fqdn;
            LastVatpPort = retained.VatpPort;
            if (retained.IsAvailable)
            {
                return new ArticleWorkHandlerResult(
                    ArticleWorkOutcome.Success,
                    null,
                    Article: null,
                    Fqdn: retained.Fqdn,
                    VatpPort: retained.VatpPort,
                    ArticleId: record.ArtId);
            }

            return new ArticleWorkHandlerResult(
                ArticleWorkOutcome.RetentionRejected,
                retained.Kind.ToString(),
                Article: null,
                Fqdn: null,
                VatpPort: null,
                ArticleId: null);
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
