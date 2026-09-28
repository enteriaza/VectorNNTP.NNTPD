using System.Net;
using System.Text;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Networking.Proxy;

namespace VectorNNTP.NNTPD.Tests.Fixtures;

/// <summary>
/// Destuffed article text that Common <c>ArticleRecordFactory</c> accepts.
/// </summary>
internal static class CanonicalArticleText
{
    /// <summary>Date header value accepted by <c>NewsDateParser</c>.</summary>
    public const string Date = "Fri, 23 Aug 2024 07:30:10 +0000";

    /// <summary>Builds destuffed headers + body without an NNTP terminator.</summary>
    /// <param name="messageId">Message-ID value including angle brackets.</param>
    /// <param name="body">Destuffed body, typically CRLF-terminated.</param>
    /// <param name="newsgroups">Newsgroups header value.</param>
    /// <returns>Destuffed article text.</returns>
    public static string Destuffed(
        string messageId,
        string body = "body\r\n",
        string newsgroups = "alt.test")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(newsgroups);
        return
            "Path: peer.example\r\n" +
            "Date: " + Date + "\r\n" +
            "Message-ID: " + messageId + "\r\n" +
            "Newsgroups: " + newsgroups + "\r\n" +
            "From: user@example.test\r\n" +
            "Subject: ingress-test\r\n" +
            "\r\n" +
            body;
    }

    /// <summary>NNTP stuffed wire for <see cref="Destuffed"/> without the terminator.</summary>
    public static string Stuffed(
        string messageId,
        string destuffedBody = "body\r\n",
        string newsgroups = "alt.test") =>
        Destuffed(messageId, destuffedBody, newsgroups)
            .Replace("\r\n.", "\r\n..", StringComparison.Ordinal);

    /// <summary>Builds a CanonicalV1 queue item for tests.</summary>
    public static InboundArticle CreateQueued(
        string messageId,
        InboundArticleProducer producer,
        string body = "body\r\n",
        string newsgroups = "alt.test")
    {
        var created = ArticleRecordIngress.TryCreateFromDestuffed(
            new NntpArticleParser("nntpd01.usenet.ninja"),
            Encoding.ASCII.GetBytes(Destuffed(messageId, body, newsgroups)));
        if (!created.IsAccepted)
        {
            throw new InvalidOperationException("Test article must be CanonicalV1.");
        }

        return ArticleRecordIngress.CreateQueued(
            messageId,
            created.Record,
            ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)),
            DateTimeOffset.UtcNow,
            producer);
    }
}
