using System.Buffers;
using VectorNNTP.NNTPD.Session.Framing;

namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Downstream IHAVE interpretation: destuff wire bytes once and build <see cref="Article"/>.
/// </summary>
/// <remarks>
/// The IHAVE and POST receive paths queue stuffed wire (terminator omitted). This type is the
/// single destuff point for those producers. TAKETHIS is not interpreted here.
/// </remarks>
public static class IhaveArticleInterpreter
{
    /// <summary>
    /// Destuffs <paramref name="inbound"/> once when the producer queued stuffed wire
    /// (<see cref="InboundArticleProducer.IHave"/> or <see cref="InboundArticleProducer.Post"/>)
    /// and returns a new item whose <see cref="InboundArticle.Payload"/> is the destuffed
    /// complete article (headers + blank line + body). TAKETHIS items are returned unchanged.
    /// </summary>
    public static InboundArticle Interpret(InboundArticle inbound, int maxArticleBytes)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);
        if (inbound.Producer is not (InboundArticleProducer.IHave or InboundArticleProducer.Post))
        {
            return inbound;
        }

        var article = DestuffToArticle(inbound.Payload.Span, maxArticleBytes);
        return new InboundArticle(
            inbound.MessageId,
            article.Payload,
            inbound.ClientIdentity,
            inbound.ReceivedAtUtc,
            article,
            inbound.Producer);
    }

    /// <summary>
    /// Destuffs stuffed IHAVE wire (no terminator) into the same <see cref="Article"/>
    /// the previous receive-path destuffer produced.
    /// </summary>
    public static Article DestuffToArticle(ReadOnlySpan<byte> stuffedWire, int maxArticleBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);
        var headers = new ArrayBufferWriter<byte>(1024);
        var body = new ArrayBufferWriter<byte>(1024);
        var type = ArticleType.None;
        var exceeded = false;
        var inHeaders = true;
        var offset = 0;
        while (offset < stuffedWire.Length && !exceeded)
        {
            var remaining = stuffedWire[offset..];
            var crlf = remaining.IndexOf(NntpDelimiterSearch.Crlf);
            if (crlf < 0)
            {
                break;
            }

            var line = remaining[..crlf];
            var destuffed = DestuffLine(line);
            if (inHeaders)
            {
                if (destuffed.IsEmpty)
                {
                    inHeaders = false;
                }
                else
                {
                    NntpArticleDestuffer.AppendUnstuffedLine(headers, line, maxArticleBytes, ref exceeded);
                    ArticleTypeClassifier.ObserveLine(destuffed, inHeader: true, ref type);
                }
            }
            else
            {
                NntpArticleDestuffer.AppendUnstuffedLine(body, line, maxArticleBytes, ref exceeded);
                ArticleTypeClassifier.ObserveLine(destuffed, inHeader: false, ref type);
            }

            offset += crlf + 2;
        }

        var headerBytes = headers.WrittenCount == 0
            ? ReadOnlyMemory<byte>.Empty
            : headers.WrittenMemory.ToArray();
        var bodyBytes = body.WrittenCount == 0
            ? ReadOnlyMemory<byte>.Empty
            : body.WrittenMemory.ToArray();
        return new Article(headerBytes, bodyBytes, type);
    }

    private static ReadOnlySpan<byte> DestuffLine(ReadOnlySpan<byte> stuffedLine) =>
        stuffedLine.Length > 0 && stuffedLine[0] == (byte)'.' ? stuffedLine[1..] : stuffedLine;
}
