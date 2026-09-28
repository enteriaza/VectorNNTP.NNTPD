namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Domain-neutral INN <c>news</c> log event already decided by ingress processing.
/// </summary>
/// <remarks>
/// This type does not classify junk or invent rejection reasons. The writer
/// serializes the fields supplied here. An empty <see cref="Feed"/> is written
/// as INN's unavailable token <c>?</c>. An empty <see cref="Sites"/> omits the
/// site list (outbound routing is not implemented). Timestamp is the news-log
/// event time, not the article Date header. <see cref="ResponseCode"/> may be
/// stored for <see cref="NewsLogDisposition.Rejected"/> but is not rendered.
/// <see cref="Reason"/> is the already-decided operator-facing text for
/// <see cref="NewsLogDisposition.Rejected"/> and <see cref="NewsLogDisposition.Junk"/>,
/// including any responsible newsgroup names supplied by the decision site.
/// </remarks>
public readonly struct NewsLogEvent
{
    /// <summary>Initializes a news-log event.</summary>
    /// <param name="disposition">Already-decided <c>+</c>, <c>j</c>, <c>-</c>, or <c>m</c>.</param>
    /// <param name="messageId">Article Message-ID bytes, including angle brackets when present.</param>
    /// <param name="feed">Authoritative inbound feed identity, or empty to emit <c>?</c>.</param>
    /// <param name="sites">Future outbound site list; empty omits SITE.</param>
    /// <param name="timestamp">
    /// Event time. <see cref="DateTimeOffset.MinValue"/> lets the writer stamp
    /// processing time.
    /// </param>
    /// <param name="responseCode">NNTP response code for <c>-</c>; retained on the event, not rendered.</param>
    /// <param name="reason">Already-decided operator-facing reason bytes for <c>-</c> or <c>j</c>.</param>
    public NewsLogEvent(
        NewsLogDisposition disposition,
        ReadOnlyMemory<byte> messageId,
        ReadOnlyMemory<byte> feed = default,
        ReadOnlyMemory<byte> sites = default,
        DateTimeOffset timestamp = default,
        int responseCode = 0,
        ReadOnlyMemory<byte> reason = default)
    {
        Disposition = disposition;
        MessageId = messageId;
        Feed = feed;
        Sites = sites;
        Timestamp = timestamp;
        ResponseCode = responseCode;
        Reason = reason;
    }

    /// <summary>Gets the news-log event timestamp (processing time, not article Date).</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Gets the already-decided disposition.</summary>
    public NewsLogDisposition Disposition { get; }

    /// <summary>Gets the feed/source identity when actually available.</summary>
    public ReadOnlyMemory<byte> Feed { get; }

    /// <summary>Gets the article Message-ID value bytes.</summary>
    public ReadOnlyMemory<byte> MessageId { get; }

    /// <summary>Gets optional future site/destination tokens; empty omits them.</summary>
    public ReadOnlyMemory<byte> Sites { get; }

    /// <summary>Gets the NNTP response code carried for a rejection; 0 means none. Not rendered.</summary>
    public int ResponseCode { get; }

    /// <summary>Gets the already-decided rejection or junk reason; empty omits it.</summary>
    public ReadOnlyMemory<byte> Reason { get; }
}
