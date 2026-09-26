using VectorNNTP.BackFiller.ArticleWork;

namespace VectorNNTP.BackFiller.Nntp;

/// <summary>Retrieves one article through a backbone-scoped session lease.</summary>
public interface INntpArticleRetriever
{
    /// <summary>Acquires a provider session, issues ARTICLE, and releases or retires the lease.</summary>
    Task<ArticleRetrievalResult> RetrieveAsync(ArticleWorkItem item, CancellationToken cancellationToken);
}

/// <summary>Default retriever. Does not expose pooled session ownership to Article Work.</summary>
public sealed class NntpArticleRetriever : INntpArticleRetriever
{
    private readonly NntpProviderRegistry _registry;
    private readonly ILogger<NntpArticleRetriever> _logger;

    /// <summary>Initializes the retriever.</summary>
    public NntpArticleRetriever(NntpProviderRegistry registry, ILogger<NntpArticleRetriever> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ArticleRetrievalResult> RetrieveAsync(ArticleWorkItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
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
                .DownloadArticleAsync(item.Request.MessageId, cancellationToken)
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
