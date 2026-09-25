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

    /// <summary>
    /// POST. <see cref="InboundArticle.Payload"/> is NNTP wire format after
    /// destuff/validate/normalize/restuff (dot-stuffing preserved, terminator omitted),
    /// matching the IHAVE queue contract. Workers destuff exactly once.
    /// </summary>
    Post = 2,
}
