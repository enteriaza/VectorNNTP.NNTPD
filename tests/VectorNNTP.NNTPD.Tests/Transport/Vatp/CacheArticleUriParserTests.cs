using VectorNNTP.NNTPD.Transport.Vatp;

namespace VectorNNTP.NNTPD.Tests.Transport.Vatp;

public sealed class CacheArticleUriParserTests
{
    [Fact]
    public void TryParse_accepts_canonical_uri()
    {
        const string uri = "cache://backfiller01.usenet.ninja:119/30edc94157aa16fe644a45a1f1ffe160";
        Assert.True(CacheArticleUriParser.TryParse(uri, out var parsed, out _));
        Assert.Equal("backfiller01.usenet.ninja", parsed.Host);
        Assert.Equal(119, parsed.Port);
        Assert.Equal("30edc94157aa16fe644a45a1f1ffe160", parsed.Md5Hex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.test:1/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("cache://nodots:119/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void TryParse_rejects_invalid(string uri)
    {
        Assert.False(CacheArticleUriParser.TryParse(uri, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
