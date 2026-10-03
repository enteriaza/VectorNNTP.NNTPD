using VectorNNTP.Common.Articles;

namespace VectorNNTP.Common.Tests.Articles
{
    public sealed class ArticleTypeCapabilitiesTests
    {
        [Fact]
        public void All_Equals65535_AndUsesOnlyBits0Through15()
        {
            uint combined = 0;
            foreach (ArticleType value in Enum.GetValues<ArticleType>())
            {
                if (value == ArticleType.None)
                {
                    continue;
                }

                var bits = (uint)value;
                Assert.NotEqual(0u, bits);
                Assert.True(bits <= 1u << 15, value + " uses a bit above 15.");
                combined |= bits;
            }

            Assert.Equal(65535u, combined);
            Assert.Equal(ArticleTypeCapabilities.AllValue, combined);
            Assert.Equal(ArticleTypeCapabilities.All, (ArticleType)combined);
            Assert.Equal(ArticleType.None, ArticleTypeCapabilities.All & ~((ArticleType)65535));
        }
    }
}
