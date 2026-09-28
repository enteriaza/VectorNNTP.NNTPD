using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Session.Framing;
using VectorNNTP.NNTPD.Tests.Fixtures;

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
    public void DestuffThenRestuff_PreservesDotStuffingOnOutbound()
    {
        var stuffed = "Subject: d\r\n\r\n..foo\r\n"u8.ToArray();
        var article = IhaveArticleInterpreter.DestuffToArticle(stuffed, 64 * 1024);
        var wire = ArticleWireReconstructor.RestuffArticle(article.Payload.Span);
        Assert.Equal("Subject: d\r\n\r\n..foo\r\n.\r\n"u8.ToArray(), wire);
    }

    [Fact]
    public async Task SpoolWriter_ConsumesCanonicalRecordsWithoutReparse()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var captured = new List<InboundArticle>();
        var writer = new IncomingSpoolWriterService(
            queue,
            new CapturingPersister(captured),
            Options.Create(new NntpdOptions { ArticleIngestion = new ArticleIngestionOptions() }),
            NullLogger<IncomingSpoolWriterService>.Instance);
        await writer.StartAsync(CancellationToken.None);

        var ihave = CanonicalArticleText.CreateQueued("<ihave@example.com>", InboundArticleProducer.IHave, ".foo\r\n");
        var takeThis = CanonicalArticleText.CreateQueued("<takethis@example.com>", InboundArticleProducer.TakeThis, ".foo\r\n");
        var post = CanonicalArticleText.CreateQueued("<post@example.com>", InboundArticleProducer.Post, ".foo\r\n");
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(ihave, CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(takeThis, CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(post, CancellationToken.None));

        queue.Complete();
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(3, captured.Count);
        Assert.All(captured, static a => Assert.Equal(ArticleParseStatus.CanonicalV1, a.Record.ParseStatus));
        Assert.True(captured.Single(static a => a.MessageId == "<ihave@example.com>").Payload.Equals(ihave.Payload));
        Assert.True(captured.Single(static a => a.MessageId == "<takethis@example.com>").Payload.Equals(takeThis.Payload));
        Assert.True(captured.Single(static a => a.MessageId == "<post@example.com>").Payload.Equals(post.Payload));
    }

    [Fact]
    public async Task SpoolWriter_CanonicalV1Record_IsNotReparsedOrCopied()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var captured = new List<InboundArticle>();
        var writer = new IncomingSpoolWriterService(
            queue,
            new CapturingPersister(captured),
            Options.Create(new NntpdOptions { ArticleIngestion = new ArticleIngestionOptions() }),
            NullLogger<IncomingSpoolWriterService>.Instance);
        await writer.StartAsync(CancellationToken.None);

        var created = ArticleRecordIngress.TryCreateFromDestuffed(
            new NntpArticleParser("nntpd01.usenet.ninja"),
            Encoding.ASCII.GetBytes(CanonicalArticleText.Destuffed("<q@example.com>", "one\r\n")));
        Assert.True(created.IsAccepted);
        var inbound = ArticleRecordIngress.CreateQueued(
            "<q@example.com>",
            created.Record,
            ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)),
            DateTimeOffset.UtcNow,
            InboundArticleProducer.TakeThis);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(inbound, CancellationToken.None));

        queue.Complete();
        await writer.StopAsync(CancellationToken.None);

        var persisted = Assert.Single(captured);
        Assert.Equal(ArticleParseStatus.CanonicalV1, persisted.Record.ParseStatus);
        Assert.True(persisted.Record.ArtData.Equals(created.Record.ArtData));
        Assert.True(persisted.Payload.Equals(persisted.Record.ArtData));
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
