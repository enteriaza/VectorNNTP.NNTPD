using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.NNTPD.Bench.Tests;

public sealed class TakeThisArticleAndMessageIdTests
{
    [Fact]
    public void Article_ContainsFactoryRequiredHeaders_AndKeeps80ByteBody()
    {
        var article = TakeThisArticlePayload.Build();
        var text = Encoding.ASCII.GetString(article);
        Assert.Contains("Path: bench.vectornntp.local\r\n", text, StringComparison.Ordinal);
        Assert.Contains("From: benchmark@vectornntp.local\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Newsgroups: misc.test\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Subject: VectorNNTP benchmark\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Date: ", text, StringComparison.Ordinal);
        Assert.Contains("Message-ID: <" + TakeThisArticlePayload.StaticMessageId + ">", text, StringComparison.Ordinal);
        Assert.True(article.AsSpan().EndsWith("\r\n.\r\n"u8));
        Assert.Equal(767930, TakeThisArticlePayload.BodyBytes(article));
        Assert.InRange(article.Length, 768000, 768400);
    }

    [Fact]
    public void Article_IsAcceptedByArticleRecordFactory()
    {
        var article = TakeThisArticlePayload.Build();
        var destuffed = TakeThisArticlePayload.DestuffedWithoutTerminator(article);
        var created = ArticleRecordFactory.TryCreate(new NntpArticleParser("nntpd01.usenet.ninja"), destuffed);
        Assert.True(created.IsAccepted);
        Assert.Equal(ArticleParseStatus.CanonicalV1, created.Record.ParseStatus);
        Assert.Equal(created.Record.ArtData.Length, created.Record.ArtSize);
        Assert.True(created.Record.ArtSize > destuffed.Length);
        Assert.True(created.Record.ArtLines > 0);
        Assert.True(created.Record.Fields.MessageId.IsPresent);
        Assert.True(created.Record.Fields.Newsgroups.IsPresent);
        Assert.True(created.Record.Fields.Date.IsPresent);
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
    public void MessageIds_AreUnique_AcrossConnectionsSequencesAndInstances()
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
                    TakeThisCommandBuffer.FormatMessageId(connection, sequence, buffer.Instance),
                    buffer.CurrentMessageId());
                Assert.StartsWith("<t", buffer.CurrentMessageId(), StringComparison.Ordinal);
                Assert.DoesNotContain("bench-", buffer.CurrentMessageId(), StringComparison.Ordinal);
            }
        }

        Assert.Equal(2000, seen.Count);
    }

    [Fact]
    public void CommandBuffer_IsFixedLength_AndOnlyDigitsChange()
    {
        var buffer = new TakeThisCommandBuffer(3, instance: 42);
        var first = buffer.Buffer.ToArray();
        buffer.SetSequence(42);
        var second = buffer.Buffer.ToArray();
        Assert.Equal(first.Length, second.Length);
        Assert.Equal(
            Encoding.ASCII.GetBytes("TAKETHIS <t0000000042-03-000000000042@vectornntp.local>\r\n"),
            second);
        Assert.Equal("<t0000000042-03-000000000042@vectornntp.local>", buffer.CurrentMessageId());
    }

    [Fact]
    public void TwoBuffers_ReceiveDistinctInstances()
    {
        var left = new TakeThisCommandBuffer(0);
        var right = new TakeThisCommandBuffer(0);
        Assert.NotEqual(left.Instance, right.Instance);
        left.SetSequence(1);
        right.SetSequence(1);
        Assert.NotEqual(left.CurrentMessageId(), right.CurrentMessageId());
    }

    [Fact]
    public void WriteMessageId_PreservesWidth_AndDoesNotCopyArticleBody()
    {
        var article = TakeThisArticlePayload.Build(256);
        var copy = (byte[])article.Clone();
        var id = PostMessageIdBuffer.FormatMessageId(0, 7, 99);
        TakeThisArticlePayload.WriteMessageId(copy, id);
        Assert.Equal(article.Length, copy.Length);
        Assert.Contains(Encoding.ASCII.GetBytes(id), copy);
        Assert.Equal(
            article.AsSpan(TakeThisArticlePayload.MessageIdValueOffset(article) + id.Length).ToArray(),
            copy.AsSpan(TakeThisArticlePayload.MessageIdValueOffset(copy) + id.Length).ToArray());
    }
}
