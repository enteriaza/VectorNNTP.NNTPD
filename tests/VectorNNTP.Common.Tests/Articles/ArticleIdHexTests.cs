using VectorNNTP.Common.Articles;

namespace VectorNNTP.Common.Tests.Articles
{
    public sealed class ArticleIdHexTests
    {
        [Fact]
        public void ToLowerHexString_and_TryParseLowerHex_round_trip()
        {
            var id = ArticleId.FromMessageId("<hex-roundtrip@example.test>"u8);
            var hex = id.ToLowerHexString();
            Assert.Equal(ArticleId.HexLength, hex.Length);
            Assert.Equal(hex, hex.ToLowerInvariant());
            Assert.True(ArticleId.TryParseLowerHex(hex, out var parsed));
            Assert.Equal(id, parsed);
        }

        [Theory]
        [InlineData("ABC")]
        [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
        public void TryParseLowerHex_rejects_invalid(string hex)
        {
            Assert.False(ArticleId.TryParseLowerHex(hex, out _));
        }
    }
}
