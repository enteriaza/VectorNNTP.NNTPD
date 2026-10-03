using VectorNNTP.Common.Articles.Processing;

namespace VectorNNTP.Common.Tests.Articles.Processing
{
    public sealed class NntpArticleIdentityTests
    {
        [Fact]
        public void MatchesRequest_WhenBytesEqualRequestedChars_ReturnsTrue()
        {
            Assert.True(NntpArticleIdentity.MatchesRequest("<abc@example.test>"u8, "<abc@example.test>"));
        }

        [Fact]
        public void MatchesRequest_WhenLengthDiffers_ReturnsFalse()
        {
            Assert.False(NntpArticleIdentity.MatchesRequest("<abc@example.test>"u8, "<abc@example.test>.extra"));
        }

        [Fact]
        public void MatchesRequest_WhenOneByteDiffers_ReturnsFalse()
        {
            Assert.False(NntpArticleIdentity.MatchesRequest("<abc@example.test>"u8, "<Abc@example.test>"));
        }
    }
}
