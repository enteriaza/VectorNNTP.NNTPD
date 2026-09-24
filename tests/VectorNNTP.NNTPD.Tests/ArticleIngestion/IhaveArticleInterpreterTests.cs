using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Session.Framing;

namespace VectorNNTP.NNTPD.Tests.ArticleIngestion;

public sealed class IhaveArticleInterpreterTests
{
    [Fact]
    public void DestuffToArticle_UnstuffsOnce_AndMatchesCanonical()
    {
        var stuffed = "Subject: d\r\n\r\n..foo\r\n"u8.ToArray();
        var article = IhaveArticleInterpreter.DestuffToArticle(stuffed, 64 * 1024);
        Assert.Equal("Subject: d\r\n", Encoding.ASCII.GetString(article.Headers.Span));
        Assert.Equal(".foo\r\n", Encoding.ASCII.GetString(article.Body.Span));
        Assert.Equal("Subject: d\r\n\r\n.foo\r\n"u8.ToArray(), article.Payload.ToArray());
        Assert.Equal(article.Payload.Length, article.Size);
    }

    [Fact]
    public void DestuffToArticle_DoesNotDestuffTwice()
    {
        var stuffed = "Subject: d\r\n\r\n..foo\r\n"u8.ToArray();
        var once = IhaveArticleInterpreter.DestuffToArticle(stuffed, 64 * 1024);
        var twice = IhaveArticleInterpreter.DestuffToArticle(once.Payload.Span, 64 * 1024);
        Assert.Equal(".foo\r\n", Encoding.ASCII.GetString(once.Body.Span));
        Assert.Equal("foo\r\n", Encoding.ASCII.GetString(twice.Body.Span));
    }

    [Fact]
    public void DestuffToArticle_Yenc_IsNotDecoded()
    {
        var wire = "Subject: y\r\n\r\n=ybegin line=128 size=4 name=a.bin\r\n=ypart begin=1 end=4\r\n)ab=\r\n=yend size=4\r\n"u8;
        var article = IhaveArticleInterpreter.DestuffToArticle(wire, 64 * 1024);
        Assert.True(article.Type.HasFlag(ArticleType.YEncoded));
        Assert.Contains("=ybegin", Encoding.ASCII.GetString(article.Body.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void Interpret_IHave_ReplacesPayloadWithDestuffed()
    {
        var inbound = new InboundArticle(
            "<i@example.com>",
            "Subject: d\r\n\r\n..foo\r\n"u8.ToArray(),
            ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)),
            DateTimeOffset.UtcNow,
            structured: null,
            InboundArticleProducer.IHave);

        var interpreted = IhaveArticleInterpreter.Interpret(inbound, 64 * 1024);
        Assert.Equal(InboundArticleProducer.IHave, interpreted.Producer);
        Assert.NotNull(interpreted.Structured);
        Assert.Equal(".foo\r\n", Encoding.ASCII.GetString(interpreted.Structured!.Value.Body.Span));
        Assert.Equal(interpreted.Structured.Value.Payload.ToArray(), interpreted.Payload.ToArray());
        Assert.Equal("..foo\r\n", Encoding.ASCII.GetString(inbound.Payload.Span[^7..]));
    }

    [Fact]
    public void Interpret_TakeThis_IsUnchanged()
    {
        var payload = "Subject: d\r\n\r\n..foo\r\n"u8.ToArray();
        var inbound = new InboundArticle(
            "<t@example.com>",
            payload,
            ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)),
            DateTimeOffset.UtcNow);

        var interpreted = IhaveArticleInterpreter.Interpret(inbound, 64 * 1024);
        Assert.Same(inbound, interpreted);
        Assert.Equal(payload, interpreted.Payload.ToArray());
        Assert.Null(interpreted.Structured);
    }

    [Fact]
    public void DestuffThenRestuff_PreservesDotStuffingOnOutbound()
    {
        var stuffed = "Subject: d\r\n\r\n..foo\r\n"u8.ToArray();
        var article = IhaveArticleInterpreter.DestuffToArticle(stuffed, 64 * 1024);
        var wire = ArticleWireReconstructor.RestuffArticle(article.Payload.Span);
        Assert.Equal("Subject: d\r\n\r\n..foo\r\n.\r\n"u8.ToArray(), wire);
    }

    [Fact]
    public async Task SpoolWriter_DestuffsIhaveOnce_AndLeavesTakeThisUntouched()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var captured = new List<InboundArticle>();
        var writer = new IncomingSpoolWriterService(
            queue,
            new CapturingPersister(captured),
            Options.Create(new NntpdOptions { ArticleIngestion = new ArticleIngestionOptions() }),
            NullLogger<IncomingSpoolWriterService>.Instance);
        await writer.StartAsync(CancellationToken.None);

        var identity = ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119));
        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await queue.EnqueueAsync(
                new InboundArticle(
                    "<ihave@example.com>",
                    "Subject: d\r\n\r\n..foo\r\n"u8.ToArray(),
                    identity,
                    DateTimeOffset.UtcNow,
                    structured: null,
                    InboundArticleProducer.IHave),
                CancellationToken.None));
        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await queue.EnqueueAsync(
                new InboundArticle(
                    "<takethis@example.com>",
                    "Subject: d\r\n\r\n..foo\r\n"u8.ToArray(),
                    identity,
                    DateTimeOffset.UtcNow),
                CancellationToken.None));

        queue.Complete();
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(2, captured.Count);
        var ihave = captured.Single(static a => a.MessageId == "<ihave@example.com>");
        var takeThis = captured.Single(static a => a.MessageId == "<takethis@example.com>");
        Assert.Equal(".foo\r\n", Encoding.ASCII.GetString(ihave.Structured!.Value.Body.Span));
        Assert.Equal("Subject: d\r\n\r\n.foo\r\n", Encoding.ASCII.GetString(ihave.Payload.Span));
        Assert.Null(takeThis.Structured);
        Assert.Equal("Subject: d\r\n\r\n..foo\r\n", Encoding.ASCII.GetString(takeThis.Payload.Span));
    }

    private sealed class CapturingPersister(List<InboundArticle> captured) : IIncomingArticlePersister
    {
        public Task PersistAsync(InboundArticle article, CancellationToken cancellationToken)
        {
            captured.Add(article);
            return Task.CompletedTask;
        }
    }
}
