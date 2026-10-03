using VectorNNTP.Common.Articles.Checksum;
using VectorNNTP.Common.Articles.YEnc;

namespace VectorNNTP.Common.Tests.Articles.Checksum
{
    public sealed class IeeeCrc32Tests
    {
        [Fact]
        public void Polynomial_is_ieee_crc32_not_castagnoli()
        {
            Assert.Equal(0xEDB88320u, IeeeCrc32.Polynomial);
            Assert.NotEqual(0x82F63B78u, IeeeCrc32.Polynomial);
        }

        [Fact]
        public void Compute_matches_the_standard_ieee_vector()
        {
            Assert.Equal(0xCBF43926u, IeeeCrc32.Compute("123456789"u8));
        }

        [Fact]
        public void Compute_is_not_crc32c()
        {
            Assert.NotEqual(0xE3069283u, IeeeCrc32.Compute("123456789"u8));
        }

        [Fact]
        public void Changed_byte_changes_crc()
        {
            var original = IeeeCrc32.Compute("article-bytes"u8);
            var changed = IeeeCrc32.Compute("article-byteS"u8);
            Assert.NotEqual(original, changed);
        }

        [Fact]
        public void Same_bytes_produce_same_crc()
        {
            var first = IeeeCrc32.Compute("canonical"u8);
            var second = IeeeCrc32.Compute("canonical"u8);
            Assert.Equal(first, second);
        }

        [Fact]
        public void YEncCrc32_still_matches_ieee_primitive()
        {
            Assert.Equal(IeeeCrc32.Compute("123456789"u8), YEncCrc32.Compute("123456789"u8));
            Assert.Equal(IeeeCrc32.Polynomial, YEncCrc32.Polynomial);
        }
    }
}
