namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>Identifies which ingest command produced an <see cref="InboundArticle"/>.</summary>
public enum InboundArticleProducer
{
    /// <summary>TAKETHIS (STREAM wire or MODE READER destuff). Unchanged.</summary>
    TakeThis = 0,

    /// <summary>
    /// IHAVE. <see cref="InboundArticle.Payload"/> is NNTP wire format (dot-stuffing
    /// preserved, terminator omitted). Workers destuff exactly once.
    /// </summary>
    IHave = 1,
}
