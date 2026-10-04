using VectorNNTP.Common.NntpDb;

namespace VectorNNTP.Common.Tests.NntpDb
{
    public sealed class NntpSharedConfigurationReaderTests
    {
        [Fact]
        public void Validate_accepts_one_positive_byte_count_and_path_site_name()
        {
            var configuration = NntpSharedConfigurationReader.Validate(
            [
                new NntpSharedConfigurationCandidate(6 * 1024 * 1024, "news.example", null),
            ]);

            Assert.Equal(6 * 1024 * 1024, configuration.MaxArticleBytes);
            Assert.Equal("news.example", configuration.SiteName);
            Assert.Null(configuration.PrometheusUrl);
        }

        [Fact]
        public void Validate_stores_prometheus_url_without_interpreting_it()
        {
            var configuration = NntpSharedConfigurationReader.Validate(
            [
                new NntpSharedConfigurationCandidate(1, "news.example", "not a uri"),
            ]);

            Assert.Equal("not a uri", configuration.PrometheusUrl);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Validate_rejects_a_missing_or_duplicated_row(int extra)
        {
            var rows = new List<NntpSharedConfigurationCandidate>();
            if (extra == 1)
            {
                rows.Add(new NntpSharedConfigurationCandidate(1, "news.example", null));
                rows.Add(new NntpSharedConfigurationCandidate(2, "other.example", null));
            }
            else if (extra == 2)
            {
                rows.Add(new NntpSharedConfigurationCandidate(1, "news.example", null));
                rows.Add(new NntpSharedConfigurationCandidate(2, "other.example", null));
                rows.Add(new NntpSharedConfigurationCandidate(3, "third.example", null));
            }

            var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(rows));
            Assert.Contains("nntpsharedconfig", ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Validate_rejects_a_non_positive_size(long size)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
            [
                new NntpSharedConfigurationCandidate(size, "news.example", null),
            ]));
            Assert.Contains("maxartsize", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_rejects_a_size_that_does_not_fit_in_int()
        {
            Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
            [
                new NntpSharedConfigurationCandidate((long)int.MaxValue + 1, "news.example", null),
            ]));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("news example")]
        [InlineData("news!example")]
        [InlineData("news.example.invalid.because.this.name.is.longer.than.fifty.characters")]
        public void Validate_rejects_an_invalid_site_name(string? siteName)
        {
            Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
            [
                new NntpSharedConfigurationCandidate(1, siteName, null),
            ]));
        }
    }
}
