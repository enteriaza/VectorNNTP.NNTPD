using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Acme;
using VectorNNTP.Common.Cloudflare;
using VectorNNTP.Common.Networking;
using VectorNNTP.Common.Networking.Certificates;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.Common.Tests.Hosting
{
    public sealed class SharedInfrastructureRegistrationTests
    {
        [Fact]
        public void Tls_certificate_provider_is_the_shared_acme_publisher()
        {
            var provider = new TlsCertificateContextProvider(NullLogger<TlsCertificateContextProvider>.Instance);
            Assert.IsAssignableFrom<IAcmeCertificatePublisher>(provider);
            Assert.IsAssignableFrom<ITlsCertificateContextProvider>(provider);
            Assert.False(provider.IsAvailable);
        }

        [Fact]
        public void Bind_address_validation_accepts_wildcard_and_rejects_unassigned()
        {
            var options = new AcmeCloudflareOptions
            {
                BindAddress = ["*"],
            };
            var failures = new List<string>();
            AcmeCloudflareOptionsValidator.CollectBindAddressFailures(
                options,
                new AssigningLocalIpAddressAssignee(assignAll: true),
                failures);
            Assert.Empty(failures);

            options.BindAddress = ["198.51.100.10"];
            AcmeCloudflareOptionsValidator.CollectBindAddressFailures(
                options,
                new AssigningLocalIpAddressAssignee(assignAll: false),
                failures);
            Assert.Contains(failures, static f => f.Contains("198.51.100.10", StringComparison.Ordinal));
            Assert.Contains(failures, static f => f.Contains("not assigned", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("::1")]
        [InlineData("0.0.0.0")]
        [InlineData("::")]
        public void Bind_address_validation_accepts_assigned_literals_and_family_wildcards(string token)
        {
            var options = new AcmeCloudflareOptions
            {
                BindAddress = [token],
            };
            var failures = new List<string>();
            AcmeCloudflareOptionsValidator.CollectBindAddressFailures(
                options,
                new AssigningLocalIpAddressAssignee(assignAll: true),
                failures);
            Assert.Empty(failures);
        }

        [Fact]
        public void Bind_address_validation_rejects_invalid_tokens()
        {
            var options = new AcmeCloudflareOptions
            {
                BindAddress = ["not-an-address"],
            };
            var failures = new List<string>();
            AcmeCloudflareOptionsValidator.CollectBindAddressFailures(
                options,
                new AssigningLocalIpAddressAssignee(assignAll: true),
                failures);
            Assert.Contains(failures, static f => f.Contains("not a valid IPv4 or IPv6 address", StringComparison.Ordinal));
        }

        [Fact]
        public void Bind_address_validation_accepts_multiple_assigned_addresses_and_normalizes_empty_to_wildcard()
        {
            var options = new AcmeCloudflareOptions
            {
                BindAddress = ["127.0.0.1", "::1"],
            };
            var failures = new List<string>();
            AcmeCloudflareOptionsValidator.CollectBindAddressFailures(
                options,
                new AssigningLocalIpAddressAssignee(assignAll: true),
                failures);
            Assert.Empty(failures);

            options.BindAddress = [];
            AcmeCloudflareOptionsValidator.NormalizeBindAddresses(options);
            Assert.Equal(["*"], options.BindAddress);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(119)]
        [InlineData(563)]
        [InlineData(65535)]
        public void Tls_only_validator_accepts_usable_ports(int port)
        {
            var options = new AcmeCloudflareOptions { BindPortTls = port };
            Assert.True(new TlsOnlyAcmeCloudflareOptionsValidator().Validate(null, options).Succeeded);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(65536)]
        public void Tls_only_validator_rejects_disabled_or_invalid_ports(int port)
        {
            var options = new AcmeCloudflareOptions { BindPortTls = port };
            var result = new TlsOnlyAcmeCloudflareOptionsValidator().Validate(null, options);
            Assert.True(result.Failed);
            Assert.Contains(result.Failures!, static f => f.Contains("TLS-only", StringComparison.Ordinal));
        }

        [Fact]
        public void Shared_bind_validator_is_the_same_method_nntpd_and_backfiller_use()
        {
            Assert.NotNull(typeof(AcmeCloudflareOptionsValidator).GetMethod(nameof(AcmeCloudflareOptionsValidator.CollectBindAddressFailures)));
            Assert.Equal(
                typeof(AcmeCloudflareOptionsValidator).Assembly,
                typeof(CloudflareDnsReconciliationService).Assembly);
            Assert.Equal(
                typeof(AcmeCloudflareOptionsValidator).Assembly,
                typeof(TlsCertificateContextProvider).Assembly);
            Assert.Equal(
                typeof(CloudflareDnsReconciliationService).Assembly,
                typeof(CloudflareDnsReconciliationApplicationService).Assembly);
            Assert.Equal(
                "VectorNNTP.Common.Cloudflare",
                typeof(CloudflareDnsReconciliationApplicationService).Namespace);
        }

        [Fact]
        public void Dns_application_service_adapter_delegates_name_and_execution()
        {
            var options = new AcmeCloudflareOptions
            {
                Fqdn = "backfiller01.usenet.ninja",
                CloudFlareZoneId = "0123456789abcdef0123456789abcdef",
                BindAddress = ["198.18.0.10"],
                CloudFlareApiKey = "unit-test-key",
            };
            var inner = new CloudflareDnsReconciliationService(
                Options.Create(options),
                new BindAddressResolver(
                    new AssigningLocalIpAddressAssignee(assignAll: true),
                    NullLogger<BindAddressResolver>.Instance),
                new NoOpCloudflareDnsReconciler(),
                NullLogger<CloudflareDnsReconciliationService>.Instance);
            var adapter = new CloudflareDnsReconciliationApplicationService(inner);

            Assert.Equal(inner.Name, adapter.Name);
            Assert.Equal("CloudflareDnsReconciliation", adapter.Name);
            Assert.Null(adapter.Execution);
        }

        private sealed class AssigningLocalIpAddressAssignee(bool assignAll) : ILocalIpAddressAssignee
        {
            public bool IsLocallyAssigned(System.Net.IPAddress address) => assignAll;

            public IReadOnlyList<System.Net.IPAddress> GetAssignedUnicastAddresses() =>
                assignAll
                    ? [System.Net.IPAddress.Parse("198.18.0.10"), System.Net.IPAddress.Parse("2001:db8::10")]
                    : [];
        }

        private sealed class NoOpCloudflareDnsReconciler : ICloudflareDnsReconciler
        {
            public Task ReconcileAsync(
                string zoneId,
                string fqdn,
                ResolvedBindAddresses desired,
                CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task RemoveAllRecordsForFqdnAsync(
                string zoneId,
                string fqdn,
                CancellationToken cancellationToken,
                TimeSpan? operationTimeout = null) =>
                Task.CompletedTask;
        }
    }
}
