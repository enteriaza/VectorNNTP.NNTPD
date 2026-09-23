using System.Text;

namespace VectorNNTP.NNTPD.Bench.Tests;

public sealed class TakeThisArticleAndMessageIdTests
{
    [Fact]
    public void Article_DefaultSize_MatchesPythonWireContract()
    {
        var article = TakeThisArticlePayload.Build();
        Assert.Equal(768054, article.Length);
        Assert.Equal(767930, TakeThisArticlePayload.BodyBytes(article));
        var text = Encoding.ASCII.GetString(article);
        Assert.StartsWith("From: benchmark@vectornntp.local\r\n", text, StringComparison.Ordinal);
        Assert.True(article.AsSpan().EndsWith("\r\n.\r\n"u8));
        Assert.Contains("Message-ID: <" + TakeThisArticlePayload.StaticMessageId + ">", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Article_BodyLines_AreCrLfAligned_AndNeverDotStart()
    {
        var article = TakeThisArticlePayload.Build();
        var text = Encoding.ASCII.GetString(article);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(headerEnd > 0);
        var body = text[(headerEnd + 4)..];
        Assert.EndsWith("\r\n.\r\n", body, StringComparison.Ordinal);
        var lines = body[..^5].Split("\r\n", StringSplitOptions.None);
        Assert.All(lines[..^1], static line =>
        {
            Assert.Equal(80, line.Length);
            Assert.False(line.StartsWith('.'));
        });
    }

    [Fact]
    public void Article_RequestedSize_IsLineAligned()
    {
        var article = TakeThisArticlePayload.Build(1024);
        var body = TakeThisArticlePayload.BodyBytes(article);
        Assert.Equal(0, body % 82);
        Assert.True(body <= 1024);
        Assert.True(body >= 82);
    }

    [Fact]
    public void MessageIds_AreUnique_AcrossConnectionsAndSequences()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var connection = 0; connection < 10; connection++)
        {
            var buffer = new TakeThisCommandBuffer(connection);
            for (var sequence = 1; sequence <= 200; sequence++)
            {
                buffer.SetSequence(sequence);
                Assert.True(seen.Add(buffer.CurrentMessageId()), buffer.CurrentMessageId());
                Assert.Equal(
                    TakeThisCommandBuffer.FormatMessageId(connection, sequence),
                    buffer.CurrentMessageId());
            }
        }

        Assert.Equal(2000, seen.Count);
    }

    [Fact]
    public void CommandBuffer_IsFixedLength_AndOnlyDigitsChange()
    {
        var buffer = new TakeThisCommandBuffer(3);
        var first = buffer.Buffer.ToArray();
        buffer.SetSequence(42);
        var second = buffer.Buffer.ToArray();
        Assert.Equal(first.Length, second.Length);
        Assert.Equal(
            Encoding.ASCII.GetBytes("TAKETHIS <bench-03-000000000042@vectornntp.local>\r\n"),
            second);
        Assert.Equal("<bench-03-000000000042@vectornntp.local>", buffer.CurrentMessageId());
    }
}
