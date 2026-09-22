namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>Outcome of attempting to accept an article into the ingestion queue.</summary>
public enum ArticleEnqueueResult
{
    /// <summary>Article was accepted into the bounded ingestion queue (239 path).</summary>
    Accepted = 0,

    /// <summary>
    /// Queue is unavailable (completed/shutting down or otherwise not accepting).
    /// Maps to RFC 4644 temporary failure (<c>400</c> + close) for TAKETHIS.
    /// </summary>
    Unavailable = 1,
}
