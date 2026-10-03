using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.NNTPD.Configuration;

using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.Configuration
{
    /// <summary>
    /// Proves the former <c>ValidateDataAnnotations()</c> rules on
    /// <see cref="AcmeCloudflareOptions"/> still fail startup via the registered
    /// <see cref="IValidateOptions{TOptions}"/> validators (AOT-safe path).
    /// </summary>
    public sealed class AcmeCloudflareDataAnnotationsEquivalenceTests
    {
        [Fact]
        public void Host_validate_on_start_succeeds_for_valid_configuration()
        {
            using var host = CreateHost(BackFillerTestOptions.CreateValidConfigurationPairs());
            var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
            Assert.False(string.IsNullOrWhiteSpace(acme.CloudFlareApiKey));
            Assert.False(string.IsNullOrWhiteSpace(acme.CloudFlareZoneId));
            Assert.InRange(acme.AcmeRenewalThresholdDays, 1, 90);
            Assert.InRange(acme.BindPortTls, 1, 65535);
        }

        [Fact]
        public void Host_validate_on_start_fails_when_cloudflare_api_key_is_missing()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["CloudFlareApiKey"] = "";

            var ex = Assert.Throws<OptionsValidationException>(() =>
            {
                using var host = CreateHost(pairs);
            });
            Assert.Contains(AcmeCloudflareOptions.CloudFlareApiKeyConfigurationKey, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Host_validate_on_start_fails_when_cloudflare_zone_id_is_missing()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:CloudFlareZoneId"] = " ";

            var ex = Assert.Throws<OptionsValidationException>(() =>
            {
                using var host = CreateHost(pairs);
            });
            Assert.Contains(AcmeCloudflareOptions.CloudFlareZoneIdConfigurationKey, ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(91)]
        public void Host_validate_on_start_fails_when_acme_renewal_threshold_days_out_of_range(int days)
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:AcmeRenewalThresholdDays"] = days.ToString();

            var ex = Assert.Throws<OptionsValidationException>(() =>
            {
                using var host = CreateHost(pairs);
            });
            Assert.Contains(nameof(AcmeCloudflareOptions.AcmeRenewalThresholdDays), ex.Message, StringComparison.Ordinal);
            Assert.Contains("1–90", ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(65536)]
        public void Host_validate_on_start_fails_when_bind_port_tls_outside_data_annotation_range(int port)
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["BackFiller:BindPortTls"] = port.ToString();

            var ex = Assert.Throws<OptionsValidationException>(() =>
            {
                using var host = CreateHost(pairs);
            });
            Assert.Contains(nameof(AcmeCloudflareOptions.BindPortTls), ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Explicit_validator_rejects_each_former_data_annotation_failure()
        {
            var assignee = new FakeLocalIpAddressAssignee(assignAll: true);
            var validator = new AcmeCloudflareOptionsValidator(assignee);

            var missingKey = BackFillerTestOptions.CreateValidAcme();
            missingKey.CloudFlareApiKey = string.Empty;
            Assert.True(validator.Validate(null, missingKey).Failed);

            var missingZone = BackFillerTestOptions.CreateValidAcme();
            missingZone.CloudFlareZoneId = string.Empty;
            Assert.True(validator.Validate(null, missingZone).Failed);

            var badRenewal = BackFillerTestOptions.CreateValidAcme();
            badRenewal.AcmeRenewalThresholdDays = 0;
            Assert.True(validator.Validate(null, badRenewal).Failed);

            var badPort = BackFillerTestOptions.CreateValidAcme();
            badPort.BindPortTls = 65536;
            Assert.True(validator.Validate(null, badPort).Failed);

            var valid = BackFillerTestOptions.CreateValidAcme();
            Assert.True(validator.Validate(null, valid).Succeeded);
            Assert.True(new TlsOnlyAcmeCloudflareOptionsValidator().Validate(null, valid).Succeeded);
        }

        private static IHost CreateHost(Dictionary<string, string?> pairs)
        {
            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
            {
                ApplicationName = "VectorNNTP.BackFiller.Tests",
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Development,
            });
            builder.Configuration.AddInMemoryCollection(pairs);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<VectorNNTP.NNTPD.Cloudflare.ICloudflareDnsReconciler>(
                new NoOpCloudflareDnsReconciler());
            builder.Services.AddSingleton<IPhysicalMemoryProvider>(
                new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
            builder.Services.AddSingleton<VectorNNTP.Common.Messaging.RabbitMq.IRabbitMqConnectionFactory>(
                new FakeBackFillerRabbitMqConnectionFactory());
            builder.Services.AddSingleton<VectorNNTP.BackFiller.Accounts.IProviderAccountSource>(
                new FakeProviderAccountSource());
            builder.AddBackFillerHosting();
            return builder.Build();
        }
    }
}
