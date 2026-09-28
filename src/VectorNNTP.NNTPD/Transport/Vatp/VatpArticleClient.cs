using VectorNNTP.Common.Articles;

namespace VectorNNTP.NNTPD.Transport.Vatp;

/// <summary>Pool-backed VATP article fetch orchestration.</summary>
internal sealed class VatpArticleClient : IVatpArticleClient
{
    private readonly VatpConnectionPool _pool;
    private readonly ILogger<VatpArticleClient> _logger;

    public VatpArticleClient(VatpConnectionPool pool, ILogger<VatpArticleClient> logger)
    {
        _pool = pool;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<VatpFetchResult> FetchArticleAsync(
        string cacheUri,
        Guid requestId,
        ArticleId articleId,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            return VatpFetchResult.ProtocolFailure("RequestId must not be empty.", requestId, articleId);
        }

        if (!CacheArticleUriParser.TryParse(cacheUri, out var endpoint, out var parseError))
        {
            return VatpFetchResult.ProtocolFailure(parseError, requestId, articleId);
        }

        _ = endpoint.Md5Hex;

        var (connection, acquireFailure) = await _pool.AcquireAsync(endpoint.Host, endpoint.Port, cancellationToken)
            .ConfigureAwait(false);
        if (acquireFailure is { } failure)
        {
            return VatpFetchResult.ConnectionFailure(failure.Error, requestId, articleId);
        }

        if (connection is null)
        {
            return VatpFetchResult.ConnectionFailure("Failed to acquire VATP connection.", requestId, articleId);
        }

        try
        {
            return await connection.FetchArticleAsync(requestId, articleId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (connection.IsDead)
            {
                _pool.RemoveDead(connection);
            }
            else
            {
                _pool.Release(connection);
            }
        }
    }
}
