using System.Buffers;
using System.Text;
using VectorNNTP.NNTPD.MultilineFramerBench.ContinuousStream;
using VectorNNTP.NNTPD.MultilineFramerBench.Prototype;

namespace VectorNNTP.NNTPD.Tests.Session.Framing;

public sealed class ContinuousTakethisParserTests
{
    [Fact]
    public void ParsesMultipleTakethis_Contiguous()
    {
        var wire = ContinuousStreamFactory.BuildFixedSizeStream(articleBytes: 128, articleCount: 5);
        var sink = new ContinuousParseSink();
        var result = ContinuousTakethisParser.ParseAll(new ReadOnlySequence<byte>(wire), sink);
        Assert.True(result.IsComplete);
        Assert.Equal(5, result.Articles);
        Assert.Equal(5 * 128, result.ArticleBytes);
    }

    [Fact]
    public void ParsesAcrossSmallSegments()
    {
        var wire = ContinuousStreamFactory.BuildFixedSizeStream(256, 3);
        var sink = new ContinuousParseSink();
        var result = ContinuousTakethisParser.ParseAll(SegmentedSequenceFactory.Create(wire, 3), sink);
        Assert.True(result.IsComplete);
        Assert.Equal(3, result.Articles);
    }

    [Fact]
    public void DotStuffedAndTrailingPeriod_NotTerminator()
    {
        var body = Encoding.ASCII.GetBytes("Hello.\r\n..foo\r\n...\r\n");
        var ms = new MemoryStream();
        ms.Write("TAKETHIS <a@b>\r\n"u8);
        ms.Write(body);
        ms.Write(".\r\n"u8);
        ms.Write("TAKETHIS <c@d>\r\n"u8);
        ms.Write("x\r\n"u8);
        ms.Write(".\r\n"u8);
        var sink = new ContinuousParseSink();
        var result = ContinuousTakethisParser.ParseAll(new ReadOnlySequence<byte>(ms.ToArray()), sink);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Articles);
        Assert.Equal(body.Length + 3, result.ArticleBytes); // second payload "x\r\n" = 3
    }

    [Fact]
    public void CheckAndQuit_EscapeToControlPlane()
    {
        var ms = new MemoryStream();
        ms.Write("CHECK <x@y>\r\n"u8);
        ms.Write("TAKETHIS <a@b>\r\n"u8);
        ms.Write("hi\r\n"u8);
        ms.Write(".\r\n"u8);
        ms.Write("QUIT\r\n"u8);
        var sink = new ContinuousParseSink();
        var result = ContinuousTakethisParser.ParseAll(new ReadOnlySequence<byte>(ms.ToArray()), sink);
        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Articles);
        Assert.Equal(1, result.Checks);
        Assert.Equal(1, result.Quits);
        Assert.Equal(2, result.ControlEscapes);
    }

    [Fact]
    public void EmptyArticle()
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <e@e>\r\n.\r\n");
        var sink = new ContinuousParseSink();
        var result = ContinuousTakethisParser.ParseAll(new ReadOnlySequence<byte>(wire), sink);
        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Articles);
        Assert.Equal(0, result.ArticleBytes);
    }

    [Fact]
    public void IncompleteArticle_ReturnsIncomplete()
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <e@e>\r\nno-terminator");
        var sink = new ContinuousParseSink();
        var result = ContinuousTakethisParser.ParseAll(new ReadOnlySequence<byte>(wire), sink);
        Assert.False(result.IsComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BulkAndSimdAgree_OnMultiArticleStream(bool simd)
    {
        var wire = ContinuousStreamFactory.BuildFixedSizeStream(512, 8);
        var sink = new ContinuousParseSink();
        var result = ContinuousTakethisParser.ParseAll(
            SegmentedSequenceFactory.Create(wire, 17),
            sink,
            new ContinuousParseOptions { UseSimdArticleScan = simd });
        Assert.True(result.IsComplete);
        Assert.Equal(8, result.Articles);
        Assert.Equal(8 * 512, result.ArticleBytes);
    }

    [Fact]
    public void InnWire7_SingleArticleStream()
    {
        var article = InnArticleCorpus.ReadAllBytes("wire-7");
        var ms = new MemoryStream();
        ms.Write("TAKETHIS <wire-7@inn>\r\n"u8);
        ms.Write(article);
        var sink = new ContinuousParseSink();
        var result = ContinuousTakethisParser.ParseAll(SegmentedSequenceFactory.Create(ms.ToArray(), 5), sink);
        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Articles);
        Assert.Equal(InnArticleCorpus.ExpectedWirePayload(article).Length, result.ArticleBytes);
    }
}
