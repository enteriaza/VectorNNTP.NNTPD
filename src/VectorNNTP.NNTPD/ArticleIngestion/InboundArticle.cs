using VectorNNTP.Common.Articles;
using VectorNNTP.NNTPD.Networking.Proxy;

namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// An article accepted into the ingestion pipeline (before spool persistence).
/// </summary>
/// <remarks>
/// Owned by the ingestion queue until a spool writer successfully persists or discards it.
/// The queue abstraction remains <see cref="System.Threading.Channels.Channel{T}"/> of this type because IHAVE still
/// queues stuffed wire and workers need producer / client / command Message-ID metadata.
/// <para>
/// IHAVE <see cref="Payload"/> is complete NNTP wire-format article bytes: leading-dot
/// stuffing is preserved; the terminating <c>CRLF . CRLF</c> is not included. Downstream
/// <see cref="IhaveArticleInterpreter"/> destuffs exactly once. IHAVE does not set
/// <see cref="Record"/>.
/// </para>
/// <para>
/// TAKETHIS and POST construct a Common <see cref="ArticleRecord"/> before admission.
/// <see cref="Payload"/> then aliases <see cref="ArticleRecord.ArtData"/> (canonical
/// destuffed bytes after Date/Path materialize). Workers must not destuff or parse again.
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
        InboundArticleProducer producer = InboundArticleProducer.TakeThis,
        ArticleRecord record = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(clientIdentity);

        MessageId = messageId;
        Payload = payload;
        ClientIdentity = clientIdentity;
        ReceivedAtUtc = receivedAtUtc;
        Structured = structured;
        Producer = producer;
        Record = record;
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
    /// <see langword="null"/> on the IHAVE receive/queue path and when
    /// <see cref="Record"/> is already CanonicalV1.
    /// </summary>
    public Article? Structured { get; }

    /// <summary>Gets which command produced this item.</summary>
    public InboundArticleProducer Producer { get; }

    /// <summary>
    /// Gets the ingress <see cref="ArticleRecord"/> when TAKETHIS or POST parsed
    /// before queue insertion; default (<see cref="ArticleParseStatus.None"/>) for IHAVE.
    /// </summary>
    public ArticleRecord Record { get; }
}
