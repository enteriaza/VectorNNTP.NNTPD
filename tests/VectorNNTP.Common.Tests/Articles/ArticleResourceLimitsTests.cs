using VectorNNTP.Common.Articles;

namespace VectorNNTP.Common.Tests.Articles;

public sealed class ArticleResourceLimitsTests
{
    [Fact]
    public void Hard_limits_match_the_old_article_contract()
    {
        Assert.Equal(5 * 1024 * 1024, ArticleResourceLimits.MaxArticleBytes);
        Assert.Equal(1024, ArticleResourceLimits.MaxArticleLineCharacters);
        Assert.Equal(ArticleResourceLimits.MaxArticleLineCharacters, ArticleResourceLimits.MaxArticleLineBytes);
    }
}
