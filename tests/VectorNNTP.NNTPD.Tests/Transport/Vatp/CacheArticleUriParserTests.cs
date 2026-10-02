using VectorNNTP.Common.Articles;
using VectorNNTP.NNTPD.Transport.Vatp;

namespace VectorNNTP.NNTPD.Tests.Transport.Vatp;

public sealed class CacheArticleUriParserTests
{
    private const string ArticleIdHex = "dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14";

    [Fact]
    public void TryParse_accepts_canonical_uri_with_64_char_article_id()
    {
        var uri = $"vatp://backfiller01.usenet.ninja:119/{ArticleIdHex}";
        Assert.True(CacheArticleUriParser.TryParse(uri, out var parsed, out _));
        Assert.Equal("backfiller01.usenet.ninja", parsed.Host);
        Assert.Equal(119, parsed.Port);
        Assert.Equal(ArticleIdHex, parsed.ArticleIdHex);
        Assert.True(ArticleId.TryParseLowerHex(parsed.ArticleIdHex, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.test:1/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("vatp://nodots:119/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("vatp://backfiller01.usenet.ninja:119/30edc94157aa16fe644a45a1f1ffe160")] // legacy 32-char MD5
    [InlineData("vatp://backfiller01.usenet.ninja:119/DCAB316BA0E91C6ABBAD8D5759BFF207932DBE9168C88954C6DD9240B4A6DA14")] // uppercase
    public void TryParse_rejects_invalid(string uri)
    {
        Assert.False(CacheArticleUriParser.TryParse(uri, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
