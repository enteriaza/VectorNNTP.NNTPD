using VectorNNTP.Common.Articles.YEnc;

namespace VectorNNTP.Common.Tests.Articles.YEnc
{
    public sealed class YEncCrc32Tests
    {
        [Fact]
        public void Polynomial_is_ieee_crc32_not_castagnoli()
        {
            Assert.Equal(0xEDB88320u, YEncCrc32.Polynomial);
            Assert.NotEqual(0x82F63B78u, YEncCrc32.Polynomial);
        }

        [Fact]
        public void Compute_matches_the_standard_ieee_vector()
        {
            Assert.Equal(0xCBF43926u, YEncCrc32.Compute("123456789"u8));
        }

        [Fact]
        public void Update_then_finalize_matches_compute()
        {
            var first = "abc"u8;
            var second = "def"u8;
            var crc = YEncCrc32.Update(YEncCrc32.InitialAccumulator, first);
            crc = YEncCrc32.Update(crc, second);
            Assert.Equal(YEncCrc32.Compute("abcdef"u8), YEncCrc32.Finalize(crc));
        }

        [Fact]
        public void Empty_input_has_the_ieee_empty_crc()
        {
            Assert.Equal(0u, YEncCrc32.Compute([]));
        }
    }
}
