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
            Assert.Equal(NntpSharedConfigurationColumns.PlaceholderAcmeAccount, configuration.AcmeAccount);
            Assert.Equal(NntpSharedConfigurationColumns.PlaceholderAcmeCertificatePassword, configuration.AcmeCertificatePassword);
            Assert.Equal(NntpSharedConfigurationColumns.PlaceholderCloudFlareApiKey, configuration.CloudFlareApiKey);
            Assert.Equal(NntpSharedConfigurationColumns.PlaceholderRabbitMqPassword, configuration.RabbitMqPassword);
            Assert.Equal(NntpSharedConfigurationColumns.PlaceholderRabbitMqUsername, configuration.RabbitMqUsername);
        }

        [Fact]
        public void Validate_loads_all_twelve_columns()
        {
            Assert.Equal(
                "SELECT maxartsize, sitename, prometheusurl, acmedirectoryurl, acmerenewalthresholddays, cloudflarezoneid, dnssuffix, acmeaccount, acmecertpass, cloudflareapikey, rabbitmqpassword, rabbitmqusername FROM nntpsharedconfig LIMIT 2",
                NntpSharedConfigurationReader.SelectSql);

            var configuration = NntpSharedConfigurationReader.Validate(
            [
                new NntpSharedConfigurationCandidate(
                    4096,
                    "news.example",
                    "http://prom.example",
                    " http://acme.example.test/directory ",
                    9,
                    " zone ",
                    "USENET.Ninja.",
                    " ops@example.test ",
                    "pfx-secret-placeholder",
                    "cf-key-placeholder",
                    "rmq-pass-placeholder",
                    " rmq-user "),
            ]);

            Assert.Equal(4096, configuration.MaxArticleBytes);
            Assert.Equal("news.example", configuration.SiteName);
            Assert.Equal("http://prom.example", configuration.PrometheusUrl);
            Assert.Equal("http://acme.example.test/directory", configuration.AcmeDirectoryUrl);
            Assert.Equal(9, configuration.AcmeRenewalThresholdDays);
            Assert.Equal("zone", configuration.CloudFlareZoneId);
            Assert.Equal("usenet.ninja", configuration.DnsSuffix);
            Assert.Equal("ops@example.test", configuration.AcmeAccount);
            Assert.Equal("pfx-secret-placeholder", configuration.AcmeCertificatePassword);
            Assert.Equal("cf-key-placeholder", configuration.CloudFlareApiKey);
            Assert.Equal("rmq-pass-placeholder", configuration.RabbitMqPassword);
            Assert.Equal("rmq-user", configuration.RabbitMqUsername);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("not-an-email")]
        public void Validate_rejects_an_invalid_acme_account_without_echoing_it(string? account)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
            [
                Row(1, "news.example", account: account),
            ]));
            Assert.Contains("acmeaccount", ex.Message, StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(account))
            {
                Assert.DoesNotContain(account, ex.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Validate_accepts_an_acme_account_at_the_schema_limit_and_rejects_one_past_it()
        {
            var accepted = new string('a', 32) + "@example.test";
            Assert.Equal(45, accepted.Length);
            var configuration = NntpSharedConfigurationReader.Validate(
            [
                Row(1, "news.example", account: accepted),
            ]);
            Assert.Equal(accepted, configuration.AcmeAccount);

            var rejected = new string('a', 33) + "@example.test";
            var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
            [
                Row(1, "news.example", account: rejected),
            ]));
            Assert.Contains("acmeaccount", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(rejected, ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("acmecertpass", 32)]
        [InlineData("cloudflareapikey", 45)]
        [InlineData("rabbitmqpassword", 45)]
        public void Validate_rejects_empty_and_overlong_opaque_credentials_without_echoing_them(
            string column,
            int maximumLength)
        {
            foreach (var value in new string?[] { null, "", " ", new string('k', maximumLength + 1) })
            {
                var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
                [
                    CredentialRow(column, value),
                ]));
                Assert.Contains(column, ex.Message, StringComparison.Ordinal);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
                }
            }

            var accepted = new string('k', maximumLength);
            var configuration = NntpSharedConfigurationReader.Validate(
            [
                CredentialRow(column, accepted),
            ]);
            Assert.Equal(accepted, Credential(configuration, column));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public void Validate_rejects_an_empty_rabbitmq_username_without_echoing_it(string? username)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
            [
                Row(1, "news.example", username: username),
            ]));
            Assert.Contains("rabbitmqusername", ex.Message, StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(username))
            {
                Assert.DoesNotContain(username, ex.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Validate_trims_the_rabbitmq_username_and_rejects_one_past_the_schema_limit()
        {
            var configuration = NntpSharedConfigurationReader.Validate(
            [
                Row(1, "news.example", username: " rmq-user "),
            ]);
            Assert.Equal("rmq-user", configuration.RabbitMqUsername);

            var rejected = new string('u', 46);
            var ex = Assert.Throws<InvalidOperationException>(() => NntpSharedConfigurationReader.Validate(
            [
                Row(1, "news.example", username: rejected),
            ]));
            Assert.Contains("rabbitmqusername", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(rejected, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_keeps_opaque_credential_whitespace()
        {
            var configuration = NntpSharedConfigurationReader.Validate(
            [
                Row(1, "news.example", certificatePassword: " pfx "),
            ]);
            Assert.Equal(" pfx ", configuration.AcmeCertificatePassword);
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
            string? suffix = "usenet.ninja",
            string? account = NntpSharedConfigurationColumns.PlaceholderAcmeAccount,
            string? certificatePassword = NntpSharedConfigurationColumns.PlaceholderAcmeCertificatePassword,
            string? apiKey = NntpSharedConfigurationColumns.PlaceholderCloudFlareApiKey,
            string? rabbitPassword = NntpSharedConfigurationColumns.PlaceholderRabbitMqPassword,
            string? username = NntpSharedConfigurationColumns.PlaceholderRabbitMqUsername) =>
            new(size, site, prometheus, directory, days, zone, suffix, account, certificatePassword, apiKey, rabbitPassword, username);

        private static NntpSharedConfigurationCandidate CredentialRow(string column, string? value) =>
            column switch
            {
                "acmecertpass" => Row(1, "news.example", certificatePassword: value),
                "cloudflareapikey" => Row(1, "news.example", apiKey: value),
                "rabbitmqpassword" => Row(1, "news.example", rabbitPassword: value),
                _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unexpected credential column."),
            };

        private static string Credential(NntpSharedConfiguration configuration, string column) =>
            column switch
            {
                "acmecertpass" => configuration.AcmeCertificatePassword,
                "cloudflareapikey" => configuration.CloudFlareApiKey,
                "rabbitmqpassword" => configuration.RabbitMqPassword,
                _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unexpected credential column."),
            };
    }
}
