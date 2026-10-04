using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.BackFiller.Nntp
{
    /// <summary>Retrieves one article through a backbone-scoped session lease.</summary>
    internal interface INntpArticleRetriever
    {
        /// <summary>Retrieves the article identified by <paramref name="item"/>.</summary>
        /// <param name="item">Work item whose request backbone and message id select the provider and ARTICLE argument.</param>
        /// <param name="maxArticleBytes">Captured article-size boundary for this retrieval.</param>
        /// <param name="consumePayload">
        /// Invoked synchronously with the destuffed payload before this method returns a retrieved article.
        /// The memory is valid only until the delegate returns. The delegate must not store it and must not await.
        /// Production calls it while the session still holds its busy lock. It is not called when no payload is retrieved.
        /// </param>
        /// <param name="cancellationToken">Cancellation signal for acquisition and download.</param>
        /// <returns>The retrieval outcome for <paramref name="item"/>.</returns>
        Task<ArticleRetrievalResult> RetrieveAsync(
            ArticleWorkItem item,
            int maxArticleBytes,
            Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult> consumePayload,
            CancellationToken cancellationToken);
    }

    /// <summary>Default retriever. Does not expose pooled session ownership to Article Work.</summary>
    internal sealed class NntpArticleRetriever : INntpArticleRetriever
    {
        /// <summary>Registry whose pools supply backbone-scoped sessions.</summary>
        private readonly NntpProviderRegistry _registry;

        /// <summary>Logger passed to <see cref="NntpLogMessages.RetrievalFailed"/>.</summary>
        private readonly ILogger<NntpArticleRetriever> _logger;

        /// <summary>Stores the registry and logger used for each retrieval.</summary>
        /// <param name="registry">Pool registry. Lookups use <see cref="ArticleWorkRequest.Backbone"/>.</param>
        /// <param name="logger">Logger for non-success download results.</param>
        /// <exception cref="ArgumentNullException"><paramref name="registry"/> or <paramref name="logger"/> is null.</exception>
        public NntpArticleRetriever(NntpProviderRegistry registry, ILogger<NntpArticleRetriever> logger)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(logger);
            _registry = registry;
            _logger = logger;
        }

        /// <summary>
        /// Acquires a session for the work-item backbone, downloads its message id, and disposes the lease.
        /// </summary>
        /// <param name="item">Work item. <see cref="ArticleWorkItem.Request"/> supplies the backbone and message id.</param>
        /// <param name="maxArticleBytes">Captured article-size boundary passed to the download.</param>
        /// <param name="consumePayload">
        /// Passed to <see cref="NntpProviderSession.DownloadArticleAsync"/> and invoked there while the session busy lock is held.
        /// </param>
        /// <param name="cancellationToken">
        /// When already canceled, acquisition is skipped. Otherwise it is passed to pool acquisition and download.
        /// </param>
        /// <returns>
        /// <see cref="ArticleRetrievalKind.Cancelled"/> when <paramref name="cancellationToken"/> is already canceled,
        /// or when acquisition throws <see cref="OperationCanceledException"/> because that token is canceled.
        /// <see cref="ArticleRetrievalKind.ProviderFailure"/> when <see cref="NntpProviderRegistry.TryGetPool"/> returns false.
        /// The connect exception's kind, status, and message when acquisition throws <see cref="NntpProviderConnectException"/>.
        /// Otherwise the <see cref="NntpProviderSession.DownloadArticleAsync"/> result.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="item"/> or <paramref name="consumePayload"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// The work-item backbone or message id is null or whitespace. Those checks are thrown by the pool lookup and download.
        /// </exception>
        /// <remarks>
        /// A download result with <see cref="ArticleRetrievalResult.SessionReusable"/> false is marked with
        /// <see cref="NntpSessionLease.Retire"/> before the lease is disposed.
        /// Kinds other than <see cref="ArticleRetrievalKind.ArticleRetrieved"/> from download are logged with
        /// <see cref="NntpLogMessages.RetrievalFailed"/>. Results returned before download are not.
        /// Other acquisition exceptions, including a disposed pool and cancellation that is not
        /// <paramref name="cancellationToken"/>, propagate.
        /// </remarks>
        public async Task<ArticleRetrievalResult> RetrieveAsync(
            ArticleWorkItem item,
            int maxArticleBytes,
            Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult> consumePayload,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(consumePayload);
            if (cancellationToken.IsCancellationRequested)
            {
                return ArticleRetrievalResult.Failed(
                    ArticleRetrievalKind.Cancelled,
                    null,
                    "Retrieval was cancelled before lease acquisition.",
                    sessionReusable: false);
            }

            if (!_registry.TryGetPool(item.Request.Backbone, out var pool))
            {
                return ArticleRetrievalResult.Failed(
                    ArticleRetrievalKind.ProviderFailure,
                    null,
                    "No provider is configured for the work-item backbone.",
                    sessionReusable: false);
            }

            NntpSessionLease lease;
            try
            {
                lease = await pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ArticleRetrievalResult.Failed(
                    ArticleRetrievalKind.Cancelled,
                    null,
                    "Lease acquisition was cancelled.",
                    sessionReusable: false);
            }
            catch (NntpProviderConnectException ex)
            {
                return ArticleRetrievalResult.Failed(ex.Kind, ex.StatusCode, ex.Message, sessionReusable: false);
            }

            await using (lease)
            {
                var result = await lease.Session
                    .DownloadArticleAsync(
                        item.Request.MessageId,
                        cancellationToken,
                        consumePayload,
                        maxArticleBytes)
                    .ConfigureAwait(false);
                if (!result.SessionReusable)
                {
                    lease.Retire();
                }

                if (result.Kind != ArticleRetrievalKind.ArticleRetrieved)
                {
                    NntpLogMessages.RetrievalFailed(
                        _logger,
                        item.Request.Backbone,
                        result.Kind,
                        result.StatusCode,
                        result.Reason);
                }

                return result;
            }
        }
    }
}
