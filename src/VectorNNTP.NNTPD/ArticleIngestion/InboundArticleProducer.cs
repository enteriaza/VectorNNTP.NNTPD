namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>Identifies which ingest command produced an <see cref="InboundArticle"/>.</summary>
public enum InboundArticleProducer
{
    /// <summary>
    /// TAKETHIS. Ingress builds <see cref="VectorNNTP.Common.Articles.ArticleRecord"/> before admission;
    /// <see cref="InboundArticle.Payload"/> aliases canonical ArtData.
    /// </summary>
    TakeThis = 0,

    /// <summary>
    /// IHAVE. <see cref="InboundArticle.Payload"/> is NNTP wire format (dot-stuffing
    /// preserved, terminator omitted). Workers destuff exactly once.
    /// </summary>
    IHave = 1,

    /// <summary>
    /// POST. Ingress builds <see cref="VectorNNTP.Common.Articles.ArticleRecord"/> after existing POST
    /// receive/validate/normalize and before admission.
    /// <see cref="InboundArticle.Payload"/> aliases canonical ArtData.
    /// </summary>
    Post = 2,
}
