using System.Text;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.BackFiller.Tests.Retention;

public sealed class CacheArticleUriTests
{
    [Fact]
    public void Uri_uses_canonical_fqdn_bind_port_and_lowercase_article_id()
    {
        var artId = ArticleId.FromMessageId(Encoding.ASCII.GetBytes("<12345@example.invalid>"));
        var uri = CacheArticleUri.Create("backfiller01.usenet.ninja", 1190, artId);
        Assert.Equal(
            "cache://backfiller01.usenet.ninja:1190/dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14",
            uri);
        Assert.EndsWith('/' + artId.ToLowerHexString(), uri, StringComparison.Ordinal);
        Assert.DoesNotContain("%", uri, StringComparison.Ordinal);
        Assert.StartsWith("cache://", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Distinct_article_ids_produce_distinct_uri_paths()
    {
        var one = ArticleId.FromMessageId("<one@example.invalid>"u8);
        var two = ArticleId.FromMessageId("<two@example.invalid>"u8);
        var uriOne = CacheArticleUri.Create("host.example.test", 119, one);
        var uriTwo = CacheArticleUri.Create("host.example.test", 119, two);
        Assert.NotEqual(uriOne, uriTwo);
        Assert.EndsWith('/' + one.ToLowerHexString(), uriOne, StringComparison.Ordinal);
        Assert.EndsWith('/' + two.ToLowerHexString(), uriTwo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Invalid_bind_port_is_rejected(int port)
    {
        var artId = ArticleId.FromMessageId("<abc@example.invalid>"u8);
        Assert.Throws<ArgumentOutOfRangeException>(() => CacheArticleUri.Create("host.example", port, artId));
    }
}
