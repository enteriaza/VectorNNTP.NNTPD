using System.Text;

namespace VectorNNTP.NNTPD.Bench.Tests;

public sealed class TakeThisWireSendTests
{
    [Fact]
    public void Bind_CommandPrecedesArticle_NoExtraBytes()
    {
        var command = new TakeThisCommandBuffer(0);
        command.SetSequence(1);
        var article = TakeThisArticlePayload.Build(256);
        var buffers = TakeThisWireSend.CreateBuffers(command.Segment, article);

        Assert.Equal(2, buffers.Length);
        Assert.Equal(command.Length + article.Length, TakeThisWireSend.TotalBytes(buffers));

        var wire = TakeThisWireSend.Concatenate(buffers);
        Assert.Equal(command.Length + article.Length, wire.Length);
        Assert.True(wire.AsSpan(0, command.Length).SequenceEqual(command.Buffer.Span));
        Assert.True(wire.AsSpan(command.Length).SequenceEqual(article));
        Assert.True(wire.AsSpan().StartsWith("TAKETHIS <"u8));
        Assert.True(wire.AsSpan().EndsWith("\r\n.\r\n"u8));
        Assert.False(HasExtraCrlfBetweenCommandAndArticle(command.Buffer.Span, article, wire));
    }

    [Fact]
    public void Bind_ReusesArticleArray_AndCommandStorage()
    {
        var command = new TakeThisCommandBuffer(2);
        command.SetSequence(7);
        var article = TakeThisArticlePayload.Build(256);
        var buffers = TakeThisWireSend.CreateBuffers(command.Segment, article);

        Assert.Same(article, buffers[1].Array);
        Assert.Equal(0, buffers[1].Offset);
        Assert.Equal(article.Length, buffers[1].Count);
        Assert.True(command.Segment.Array is not null);
        Assert.Same(command.Segment.Array, buffers[0].Array);
    }

    [Fact]
    public void UniqueMessageIds_AppearInCommandSegmentOnly()
    {
        var command = new TakeThisCommandBuffer(1);
        var article = TakeThisArticlePayload.Build(256);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var sequence = 1; sequence <= 50; sequence++)
        {
            command.SetSequence(sequence);
            var buffers = TakeThisWireSend.CreateBuffers(command.Segment, article);
            var wire = TakeThisWireSend.Concatenate(buffers);
            var commandText = Encoding.ASCII.GetString(wire, 0, command.Length);
            var expected = $"TAKETHIS {TakeThisCommandBuffer.FormatMessageId(1, sequence)}\r\n";
            Assert.Equal(expected, commandText);
            Assert.True(seen.Add(commandText));
            Assert.True(wire.AsSpan(command.Length).SequenceEqual(article));
        }
    }

    [Fact]
    public void Advance_PartialCommand_ThenRest()
    {
        var command = new TakeThisCommandBuffer(0);
        command.SetSequence(3);
        var article = "ARTICLE"u8.ToArray();
        var buffers = TakeThisWireSend.CreateBuffers(command.Segment, article);
        var original = TakeThisWireSend.Concatenate(buffers);

        var remaining = TakeThisWireSend.Advance(buffers, 10);
        Assert.Equal(original.Length - 10, remaining);
        Assert.Equal(original[10..], TakeThisWireSend.Concatenate(buffers));

        remaining = TakeThisWireSend.Advance(buffers, remaining);
        Assert.Equal(0, remaining);
        Assert.Equal(0, TakeThisWireSend.TotalBytes(buffers));
    }

    private static bool HasExtraCrlfBetweenCommandAndArticle(
        ReadOnlySpan<byte> command,
        ReadOnlySpan<byte> article,
        ReadOnlySpan<byte> wire)
    {
        if (!command.EndsWith("\r\n"u8))
        {
            return true;
        }

        return wire.Length != command.Length + article.Length
               || !wire[command.Length..].StartsWith(article);
    }
}
