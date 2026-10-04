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
                Row(6 * 1024 * 1024, "news.example"),
            ]);

            Assert.Equal(6 * 1024 * 1024, configuration.MaxArticleBytes);
            Assert.Equal("news.example", configuration.SiteName);
            Assert.Null(configuration.PrometheusUrl);
            Assert.Equal("https://acme-v02.api.letsencrypt.org/directory", configuration.AcmeDirectoryUrl);
            Assert.Equal(14, configuration.AcmeRenewalThresholdDays);
            Assert.Equal("0123456789abcdef0123456789abcdef", configuration.CloudFlareZoneId);
            Assert.Equal("usenet.ninja", configuration.DnsSuffix);
        }

        [Fact]
        public void Validate_stores_prometheus_url_without_interpreting_it()
        {
            var configuration = NntpSharedConfigurationReader.Validate(
            [
                Row(1, "news.example", "not a uri"),
            ]);

            Assert.Equal("not a uri", configuration.PrometheusUrl);
        }

        [Fact]
        public void Validate_accepts_http_and_canonicalizes_the_dns_suffix()
        {
            var configuration = NntpSharedConfigurationReader.Validate(
            [
                Row(
                    1,
                    "news.example",
                    directory: " http://acme.example.test/directory ",
                    days: 1,
                    zone: " zone ",
                    suffix: "USENET.Ninja."),
            ]);

            Assert.Equal("http://acme.example.test/directory", configuration.AcmeDirectoryUrl);
            Assert.Equal(1, configuration.AcmeRenewalThresholdDays);
            Assert.Equal("zone", configuration.CloudFlareZoneId);
            Assert.Equal("usenet.ninja", configuration.DnsSuffix);
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
                rows.Add(Row(1, "news.example"));
                rows.Add(Row(2, "other.example"));
            }
            else if (extra == 2)
            {
                rows.Add(Row(1, "news.example"));
                rows.Add(Row(2, "other.example"));
                rows.Add(Row(3, "third.example"));
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
                Row(size, "news.example"),
            ]));
            Assert.Contains("maxartsize", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_rejects_a_size_that_does_not_fit_in_int()
        {
            Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
            [
                Row((long)int.MaxValue + 1, "news.example"),
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
                Row(1, siteName),
            ]));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("acme.example.test/directory")]
        [InlineData("ftp://acme.example.test/directory")]
        public void Validate_rejects_an_invalid_acme_directory(string? directory)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
            [
                Row(1, "news.example", directory: directory),
            ]));
            Assert.Contains("acmedirectoryurl", ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Validate_rejects_a_non_positive_renewal_threshold(long days)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
            [
                Row(1, "news.example", days: days),
            ]));
            Assert.Contains("acmerenewalthresholddays", ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public void Validate_rejects_an_empty_zone_id(string? zone)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
            [
                Row(1, "news.example", zone: zone),
            ]));
            Assert.Contains("cloudflarezoneid", ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("ninja")]
        [InlineData("not a suffix")]
        public void Validate_rejects_an_invalid_dns_suffix(string? suffix)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
            [
                Row(1, "news.example", suffix: suffix),
            ]));
            Assert.Contains("dnssuffix", ex.Message, StringComparison.Ordinal);
        }

        private static NntpSharedConfigurationCandidate Row(
            long size,
            string? site,
            string? prometheus = null,
            string? directory = "https://acme-v02.api.letsencrypt.org/directory",
            long days = 14,
            string? zone = "0123456789abcdef0123456789abcdef",
            string? suffix = "usenet.ninja") =>
            new(size, site, prometheus, directory, days, zone, suffix);
    }
}
