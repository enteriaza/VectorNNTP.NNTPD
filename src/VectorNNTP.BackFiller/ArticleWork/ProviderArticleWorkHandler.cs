using System.Text;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.Common.NntpDb;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Articles.Processing;

namespace VectorNNTP.BackFiller.ArticleWork
{
    /// <summary>
    /// Retrieves an ARTICLE, builds a CanonicalV1 <see cref="ArticleRecord"/>, then retains it for
    /// VATP OPEN by RequestId. Article Work Success publishes this BackFiller's FQDN, VATP port, and the
    /// lowercase hexadecimal <see cref="ArticleId"/>. Those fields are routing metadata, not a transfer protocol.
    /// </summary>
    /// <remarks>
    /// Does not publish the RPC response or settle the delivery.
    /// The <c>Last*</c> properties are test observations written by <see cref="HandleAsync"/> with no synchronization.
    /// </remarks>
    internal sealed class ProviderArticleWorkHandler : IArticleWorkHandler
    {
        /// <summary>NNTP ARTICLE retriever used for admitted work.</summary>
        private readonly INntpArticleRetriever _retriever;

        /// <summary>Canonical retention authority that admits or rejects the built record.</summary>
        private readonly IArticleRetentionAuthority _retention;

        /// <summary>Parser used to build the CanonicalV1 record from the retrieved article bytes.</summary>
        private readonly NntpArticleParser _parser;

        /// <summary>Published shared configuration. Null in tests that do not load <c>nntpsharedconfig</c>.</summary>
        private readonly INntpSharedConfigurationCatalogue? _sharedConfiguration;

        /// <summary>Initializes the handler with the test-host Path identity <c>backfiller.test</c>.</summary>
        /// <param name="retriever">NNTP ARTICLE retriever.</param>
        /// <param name="retention">Canonical retention authority.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="retriever"/> or <paramref name="retention"/> is null.</exception>
        public ProviderArticleWorkHandler(INntpArticleRetriever retriever, IArticleRetentionAuthority retention)
            : this(retriever, retention, new NntpArticleParser("backfiller.test"))
        {
        }

        /// <summary>Initializes the handler.</summary>
        /// <param name="retriever">NNTP ARTICLE retriever.</param>
        /// <param name="retention">Canonical retention authority.</param>
        /// <param name="parser">CanonicalV1 parser, including the Path host identity it stamps onto accepted articles.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="retriever"/>, <paramref name="retention"/>, or <paramref name="parser"/> is null.
        /// </exception>
        public ProviderArticleWorkHandler(
            INntpArticleRetriever retriever,
            IArticleRetentionAuthority retention,
            NntpArticleParser parser)
            : this(retriever, retention, parser, sharedConfiguration: null)
        {
        }

        /// <summary>Initializes the handler with an optional shared-configuration catalogue.</summary>
        /// <param name="retriever">NNTP ARTICLE retriever.</param>
        /// <param name="retention">Canonical retention authority.</param>
        /// <param name="parser">CanonicalV1 parser. Its local identity remains the application hop.</param>
        /// <param name="sharedConfiguration">Published <c>nntpsharedconfig</c>. Captured once per article.</param>
        public ProviderArticleWorkHandler(
            INntpArticleRetriever retriever,
            IArticleRetentionAuthority retention,
            NntpArticleParser parser,
            INntpSharedConfigurationCatalogue? sharedConfiguration)
        {
            ArgumentNullException.ThrowIfNull(retriever);
            ArgumentNullException.ThrowIfNull(retention);
            ArgumentNullException.ThrowIfNull(parser);
            _retriever = retriever;
            _retention = retention;
            _parser = parser;
            _sharedConfiguration = sharedConfiguration;
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

        /// <summary>
        /// Retrieves the requested article, accepts a CanonicalV1 record, and retains it for VATP OPEN.
        /// </summary>
        /// <param name="item">Admitted work.</param>
        /// <param name="cancellationToken">
        /// Cancels retrieval. When it is already cancelled, or retrieval throws
        /// <see cref="OperationCanceledException"/> because it is cancelled, the outcome is
        /// <see cref="ArticleWorkOutcome.Cancelled"/> and retention is not attempted.
        /// </param>
        /// <returns>
        /// <see cref="ArticleWorkOutcome.Success"/> with FQDN, VATP port, and article id when retention reports the article available.
        /// <see cref="ArticleRetrievalKind.ArticleNotFound"/> and <see cref="ArticleRetrievalKind.InvalidArticle"/> stay terminal.
        /// <see cref="ArticleRetrievalKind.AuthenticationFailure"/> and every other non-retrieved kind become
        /// <see cref="ArticleWorkOutcome.ProviderFailure"/>. Parse failure and Message-ID mismatch become
        /// <see cref="ArticleWorkOutcome.InvalidArticle"/>. A retrieved body that cannot be transferred or is not an
        /// owned contiguous buffer becomes <see cref="ArticleWorkOutcome.RetentionRejected"/>.
        /// Success routing fields are null unless retention made the article available.
        /// <see cref="ArticleWorkHandlerResult.Article"/> is always null. The production success path does not allocate
        /// a <see cref="RetrievedArticle"/>; the canonical array is the one produced by
        /// <see cref="ArticleRecordFactory.TryCreate(NntpArticleParser, ReadOnlyMemory{byte}, ArticlePathMode)"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="item"/> is null.</exception>
        /// <remarks>
        /// <see cref="ArticleRecordFactory.TryCreate(NntpArticleParser, ReadOnlyMemory{byte}, ArticlePathMode)"/> runs
        /// inside the retriever callback, before that call returns. The callback memory is not used after it returns.
        /// An <see cref="OperationCanceledException"/> whose token is not <paramref name="cancellationToken"/> propagates.
        /// Other exceptions from retrieval, parsing, or retention also propagate. The caller maps those to
        /// <see cref="ArticleWorkOutcome.UnexpectedFailure"/>.
        /// When no shared-configuration catalogue was supplied, retrieval is called with a zero article limit so the
        /// session's <c>NntpSessionOptions.MaxArticleBytes</c> remains in force. A supplied catalogue passes that
        /// snapshot's <c>MaxArticleBytes</c>.
        /// </remarks>
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
            ArticleRecordCreateResult created = default;
            var consumed = false;
            var maxArticleBytes = 0;
            byte[]? siteNameUtf8 = null;
            if (_sharedConfiguration is not null)
            {
                var shared = _sharedConfiguration.Current;
                maxArticleBytes = shared.MaxArticleBytes;
                siteNameUtf8 = Encoding.UTF8.GetBytes(shared.SiteName);
            }

            try
            {
                retrieval = await _retriever.RetrieveAsync(
                        item,
                        maxArticleBytes,
                        memory =>
                        {
                            created = siteNameUtf8 is null
                                ? ArticleRecordFactory.TryCreate(_parser, memory, ArticlePathMode.Traverse)
                                : ArticleRecordFactory.TryCreate(
                                    _parser,
                                    memory,
                                    ArticlePathMode.Traverse,
                                    maxArticleBytes,
                                    siteNameUtf8);
                            consumed = true;
                            return created;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
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

                if (!consumed)
                {
                    LastRetentionKind = ArticleRetentionKind.InvalidPayload;
                    return new ArticleWorkHandlerResult(
                        ArticleWorkOutcome.RetentionRejected,
                        "Retrieved article payload could not be transferred into retention.");
                }

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

        /// <summary>Maps a parse-failure code to a terminal invalid-article result.</summary>
        /// <param name="failureCode">Parser failure code. The result error text is its name.</param>
        /// <returns>
        /// <see cref="ArticleWorkOutcome.InvalidArticle"/> with no success routing fields.
        /// </returns>
        private static ArticleWorkHandlerResult MapParseFailure(NntpArticleParseFailureCode failureCode)
        {
            return new ArticleWorkHandlerResult(
                ArticleWorkOutcome.InvalidArticle,
                failureCode.ToString());
        }

        /// <summary>Maps a non-retrieved retrieval result to an Article Work outcome.</summary>
        /// <param name="retrieval">Retrieval that is not <see cref="ArticleRetrievalKind.ArticleRetrieved"/>.</param>
        /// <returns>
        /// Article-not-found and invalid-article stay terminal and copy <see cref="ArticleRetrievalResult.Reason"/>.
        /// Cancelled carries a null error. Authentication failure and every other kind become
        /// <see cref="ArticleWorkOutcome.ProviderFailure"/> with that reason.
        /// </returns>
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
                _ => new ArticleWorkHandlerResult(ArticleWorkOutcome.ProviderFailure, retrieval.Reason),
            };
        }
    }
}
