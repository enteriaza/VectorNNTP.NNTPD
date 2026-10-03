using VectorNNTP.Common.Articles;

namespace VectorNNTP.Common.Messaging.Cache
{
    /// <summary>
    /// NNTPD → StorageServer fleet article-presence lookup request ("who has article X?").
    /// </summary>
    /// <param name="Version">Wire protocol version. Current is <c>1</c>.</param>
    /// <param name="RequestId">Logical lookup identity shared across all StorageServer deliveries.</param>
    /// <param name="ArticleId">Canonical article identity to look up.</param>
    internal sealed record StorageArticleLookupRequest(
        int Version,
        Guid RequestId,
        ArticleId ArticleId);

    /// <summary>
    /// StorageServer → NNTPD positive article-presence response ("I have it").
    /// </summary>
    /// <param name="Version">Wire protocol version. Current is <c>1</c>.</param>
    /// <param name="RequestId">Logical lookup identity from the request.</param>
    /// <param name="ServerId">Responding StorageServer numeric identity.</param>
    /// <param name="Fqdn">Responding StorageServer FQDN.</param>
    /// <param name="ArticleId">Article identity confirmed present.</param>
    /// <param name="VatpPort">TLS VATP listen port used for subsequent retrieval.</param>
    internal sealed record StorageArticleLookupResponse(
        int Version,
        Guid RequestId,
        int ServerId,
        string Fqdn,
        ArticleId ArticleId,
        int VatpPort);
}
