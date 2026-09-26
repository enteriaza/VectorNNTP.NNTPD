using VectorNNTP.BackFiller.Retention;

namespace VectorNNTP.BackFiller.Tests.Retention;

public sealed class CacheArticleUriTests
{
    [Fact]
    public void Uri_uses_canonical_fqdn_bind_port_and_lowercase_md5()
    {
        var identity = ArticleIdentity.FromExactMessageId("<12345@example.invalid>");
        var uri = CacheArticleUri.Create("backfiller01.usenet.ninja", 1190, identity);
        Assert.Equal("cache://backfiller01.usenet.ninja:1190/30edc94157aa16fe644a45a1f1ffe160", uri);
        Assert.DoesNotContain("%", uri, StringComparison.Ordinal);
        Assert.StartsWith("cache://", uri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Invalid_bind_port_is_rejected(int port)
    {
        var identity = ArticleIdentity.FromExactMessageId("<abc@example.invalid>");
        Assert.Throws<ArgumentOutOfRangeException>(() => CacheArticleUri.Create("host.example", port, identity));
    }
}
