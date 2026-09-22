using VectorNNTP.NNTPD.Networking.Proxy;

namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// An article accepted into the ingestion pipeline (before spool persistence).
/// </summary>
/// <remarks>
/// Owned by the ingestion queue until a spool writer successfully persists or discards it.
/// Payload is the complete NNTP article after multiline dot-unstuffing (headers + body),
/// without the terminating <c>.</c> line.
/// </remarks>
public sealed class InboundArticle
{
    /// <summary>Initializes a new instance of the <see cref="InboundArticle"/> class.</summary>
    public InboundArticle(
        string messageId,
        ReadOnlyMemory<byte> payload,
        ConnectionClientIdentity clientIdentity,
        DateTimeOffset receivedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(clientIdentity);

        MessageId = messageId;
        Payload = payload;
        ClientIdentity = clientIdentity;
        ReceivedAtUtc = receivedAtUtc;
    }

    /// <summary>Gets the message-id supplied with the transfer command (e.g. TAKETHIS).</summary>
    public string MessageId { get; }

    /// <summary>Gets the complete article bytes after dot-unstuffing.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Gets the effective client identity at acceptance time.</summary>
    public ConnectionClientIdentity ClientIdentity { get; }

    /// <summary>Gets the UTC timestamp when the article was accepted into the queue.</summary>
    public DateTimeOffset ReceivedAtUtc { get; }
}
