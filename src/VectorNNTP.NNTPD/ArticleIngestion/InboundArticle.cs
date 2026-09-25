using VectorNNTP.NNTPD.Networking.Proxy;

namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// An article accepted into the ingestion pipeline (before spool persistence).
/// </summary>
/// <remarks>
/// Owned by the ingestion queue until a spool writer successfully persists or discards it.
/// <para>
/// IHAVE <see cref="Payload"/> is complete NNTP wire-format article bytes: leading-dot
/// stuffing is preserved; the terminating <c>CRLF . CRLF</c> is not included. Downstream
/// <see cref="IhaveArticleInterpreter"/> destuffs exactly once.
/// </para>
/// <para>
/// STREAM TAKETHIS supplies framed wire bytes (no destuff). MODE READER multiline
/// fallback destuffs per RFC 3977 §3.1.1. TAKETHIS enqueue is unchanged.
/// </para>
/// <para>
/// POST <see cref="Payload"/> uses the same stuffed-wire, terminator-omitted contract
/// as IHAVE after server-owned header normalization.
/// </para>
/// </remarks>
public sealed class InboundArticle
{
    /// <summary>Initializes a new instance of the <see cref="InboundArticle"/> class.</summary>
    public InboundArticle(
        string messageId,
        ReadOnlyMemory<byte> payload,
        ConnectionClientIdentity clientIdentity,
        DateTimeOffset receivedAtUtc,
        Article? structured = null,
        InboundArticleProducer producer = InboundArticleProducer.TakeThis)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(clientIdentity);

        MessageId = messageId;
        Payload = payload;
        ClientIdentity = clientIdentity;
        ReceivedAtUtc = receivedAtUtc;
        Structured = structured;
        Producer = producer;
    }

    /// <summary>Gets the message-id supplied with the transfer command (e.g. TAKETHIS or IHAVE).</summary>
    public string MessageId { get; }

    /// <summary>Gets the complete article bytes (see type remarks for IHAVE vs TAKETHIS).</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Gets the effective client identity at acceptance time.</summary>
    public ConnectionClientIdentity ClientIdentity { get; }

    /// <summary>Gets the UTC timestamp when the article was accepted into the queue.</summary>
    public DateTimeOffset ReceivedAtUtc { get; }

    /// <summary>
    /// Gets the destuffed <see cref="Article"/> after IHAVE worker interpretation;
    /// <see langword="null"/> on the IHAVE receive/queue path and for TAKETHIS.
    /// </summary>
    public Article? Structured { get; }

    /// <summary>Gets which command produced this item.</summary>
    public InboundArticleProducer Producer { get; }
}
