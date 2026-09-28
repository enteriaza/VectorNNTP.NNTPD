using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.Common.Tests.Transport.ArticleTransfer;

internal static class VatpTestArticles
{
    internal const string LocalFqdn = "vatp01.usenet.ninja";

    internal static (ArticleRecord Record, NntpArticleHeaderName SelectedDate, byte[] ArtData) CreateCanonical(
        string messageId = "<vatp@example.test>",
        string body = "line1\r\nline2\r\n")
    {
        var parser = new NntpArticleParser(LocalFqdn);
        var destuffed = BuildDestuffed(messageId, body);
        var parse = parser.Parse(destuffed);
        Assert.True(parse.IsAccepted, parse.FailureCode.ToString());
        var created = ArticleRecordFactory.TryCreate(parser, destuffed);
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        var artData = created.Record.ArtData.ToArray();
        return (created.Record, parse.SelectedDateHeaderName, artData);
    }

    internal static byte[] BuildDestuffed(string messageId, string body)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: vatp\r\n");
        _ = builder.Append("\r\n").Append(body);
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    internal static byte[] Concat(params ReadOnlyMemory<byte>[] parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var buffer = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.Span.CopyTo(buffer.AsSpan(offset));
            offset += part.Length;
        }

        return buffer;
    }

    internal static ReadOnlySequence<byte> Fragment(byte[] frame, int chunkSize)
    {
        if (chunkSize <= 0 || chunkSize >= frame.Length)
        {
            return new ReadOnlySequence<byte>(frame);
        }

        BufferSegment? first = null;
        BufferSegment? previous = null;
        for (var offset = 0; offset < frame.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, frame.Length - offset);
            var segment = new BufferSegment(frame.AsMemory(offset, length));
            if (first is null)
            {
                first = segment;
            }
            else
            {
                previous!.Append(segment);
            }

            previous = segment;
        }

        return new ReadOnlySequence<byte>(first!, 0, previous!, previous!.Memory.Length);
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public void Append(BufferSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}
