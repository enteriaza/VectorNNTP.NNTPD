using VectorNNTP.Common.Articles;

namespace VectorNNTP.NNTPD.Transport.Vatp;

/// <summary>Fetches canonical articles from BackFiller cache listeners over VATP.</summary>
public interface IVatpArticleClient
{
    /// <summary>
    /// Fetches one article from the cache endpoint in <paramref name="cacheUri"/>.
    /// </summary>
    /// <param name="cacheUri">Success <c>vatp://</c> URI (host/port routing only).</param>
    /// <param name="requestId">Correlation id from ArticleWork.</param>
    /// <param name="articleId">Expected BLAKE3 ArticleId for OPEN binding.</param>
    /// <param name="cancellationToken">Cancellation; sends CANCEL when the connection is usable.</param>
    Task<VatpFetchResult> FetchArticleAsync(
        string cacheUri,
        Guid requestId,
        ArticleId articleId,
        CancellationToken cancellationToken);
}
