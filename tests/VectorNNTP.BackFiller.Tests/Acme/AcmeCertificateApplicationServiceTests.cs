using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Acme;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.BackFiller.Tests.Acme
{
    public sealed class AcmeCertificateApplicationServiceTests
    {
        [Fact]
        public void Adapter_implements_application_service_and_forwards_identity()
        {
            using var harness = CreateTlsDisabledHarness();
            Assert.IsAssignableFrom<IApplicationService>(harness.Adapter);
            Assert.Equal("AcmeCertificate", harness.Adapter.Name);
            Assert.Null(harness.Adapter.Execution);
        }

        [Fact]
        public async Task StartAsync_without_readiness_fails_fast_and_does_not_journal()
        {
            using var harness = CreateTlsDisabledHarness();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => harness.Adapter.StartAsync(CancellationToken.None));
            Assert.Contains("usable certificate", ex.Message, StringComparison.Ordinal);
            Assert.Empty(harness.Journal.Stages);
            Assert.False(harness.Readiness.IsReady);
            Assert.Null(harness.Adapter.Execution);
        }

        [Fact]
        public async Task StartAsync_with_existing_certificate_journals_ready_and_keeps_renewal_execution()
        {
            using var harness = CreateReusableCertificateHarness();

            await harness.Adapter.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(harness.Readiness.IsReady);
                Assert.Equal([BackFillerStartupStages.AcmeCertificateReady], harness.Journal.Stages);
                Assert.Same(harness.Inner.Execution, harness.Adapter.Execution);
                Assert.NotNull(harness.Adapter.Execution);
                Assert.False(harness.Adapter.Execution.IsCompleted);
            }
            finally
            {
                await harness.Adapter.StopAsync(CancellationToken.None);
                await harness.Adapter.DisposeAsync();
            }

            Assert.True(harness.Adapter.Execution?.IsCompleted);
        }

        [Fact]
        public async Task StopAsync_and_DisposeAsync_delegate_to_the_common_service()
        {
            using var harness = CreateReusableCertificateHarness();
            await harness.Adapter.StartAsync(CancellationToken.None);
            var execution = harness.Adapter.Execution;
            Assert.NotNull(execution);

            await harness.Adapter.StopAsync(CancellationToken.None);
            Assert.True(execution.IsCompleted);

            await harness.Adapter.DisposeAsync();
            await harness.Adapter.DisposeAsync();
        }

        private static AdapterHarness CreateTlsDisabledHarness()
        {
            var options = BackFillerTestOptions.CreateValidAcme();
            options.BindPortTls = 0;
            var readiness = new AcmeCertificateReadiness();
            var journal = new BackFillerStartupJournal();
            var inner = new AcmeCertificateService(
                Options.Create<AcmeCloudflareOptions>(options),
                new UnusedAcmeFactory(options),
                new NoOpPublisher(),
                readiness,
                NullLogger<AcmeCertificateService>.Instance);
            return new AdapterHarness(
                new AcmeCertificateApplicationService(inner, readiness, journal),
                inner,
                readiness,
                journal);
        }

        private static AdapterHarness CreateReusableCertificateHarness()
        {
            var dir = new TempAcmeDir();
            var options = BackFillerTestOptions.CreateValidAcme();
            options.AcmeStateDir = dir.Path;
            options.Fqdn = "backfiller01.usenet.ninja";
            options.IncludeNewsHostnameInCertificate = false;
            var domains = CertificateIdentities.ForFqdn(options.Fqdn, includeNewsHostname: false);
            var store = new CertificateStore(
                dir.Path,
                options.Fqdn,
                options.AcmeCertificatePassword,
                domains,
                TimeSpan.FromDays(options.AcmeRenewalThresholdDays));
            store.Save(CreateMaterial(domains, options.AcmeCertificatePassword, DateTimeOffset.UtcNow.AddDays(90)));

            var manager = new CertificateManager(
                options.Fqdn,
                dir.Path,
                options.AcmeDirectoryUrl,
                store,
                new ThrowingIssuer(),
                options.AcmeCertificatePassword,
                TimeSpan.FromDays(options.AcmeRenewalThresholdDays),
                NullLogger<CertificateManager>.Instance,
                includeNewsHostname: false);
            var factory = new InjectedManagerFactory(manager, Options.Create<AcmeCloudflareOptions>(options));
            var readiness = new AcmeCertificateReadiness();
            var journal = new BackFillerStartupJournal();
            var inner = new AcmeCertificateService(
                Options.Create<AcmeCloudflareOptions>(options),
                factory,
                new MarkingPublisher(readiness),
                readiness,
                NullLogger<AcmeCertificateService>.Instance);
            var adapter = new AcmeCertificateApplicationService(inner, readiness, journal);
            return new AdapterHarness(adapter, inner, readiness, journal, dir);
        }

        private static CertificateMaterial CreateMaterial(
            IReadOnlyList<string> dnsNames,
            string password,
            DateTimeOffset notAfter)
        {
            var before = DateTimeOffset.UtcNow.AddDays(-1);
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=" + dnsNames[0],
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var sanBuilder = new SubjectAlternativeNameBuilder();
            foreach (var name in dnsNames)
            {
                sanBuilder.AddDnsName(name);
            }

            request.CertificateExtensions.Add(sanBuilder.Build());
            using var cert = request.CreateSelfSigned(before.UtcDateTime, notAfter.UtcDateTime);
            var pfx = PfxCrypto.ExportPfx(cert, intermediateCertificates: [], password);
            return new CertificateMaterial(
                pfx,
                dnsNames.Select(static name => name.ToLowerInvariant()).OrderBy(static name => name).ToArray(),
                before,
                notAfter);
        }

        private sealed class AdapterHarness : IDisposable
        {
            private readonly TempAcmeDir? _dir;

            public AdapterHarness(
                AcmeCertificateApplicationService adapter,
                AcmeCertificateService inner,
                IAcmeCertificateReadiness readiness,
                IBackFillerStartupJournal journal,
                TempAcmeDir? dir = null)
            {
                Adapter = adapter;
                Inner = inner;
                Readiness = readiness;
                Journal = journal;
                _dir = dir;
            }

            public AcmeCertificateApplicationService Adapter { get; }

            public AcmeCertificateService Inner { get; }

            public IAcmeCertificateReadiness Readiness { get; }

            public IBackFillerStartupJournal Journal { get; }

            public void Dispose() => _dir?.Dispose();
        }

        private sealed class TempAcmeDir : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "vectornntp-bf-acme-" + Guid.NewGuid().ToString("N"));

            public TempAcmeDir() => Directory.CreateDirectory(Path);

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Path))
                    {
                        Directory.Delete(Path, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // Best-effort test cleanup.
                }
            }
        }

        private sealed class UnusedAcmeFactory : AcmeComponentFactory
        {
            public UnusedAcmeFactory(AcmeCloudflareOptions options)
                : base(
                    Options.Create(options),
                    new UnusedCloudflareClient(),
                    new UnusedHttpClientFactory(),
                    NullLoggerFactory.Instance)
            {
            }
        }

        private sealed class InjectedManagerFactory : AcmeComponentFactory
        {
            private readonly CertificateManager _manager;

            public InjectedManagerFactory(CertificateManager manager, IOptions<AcmeCloudflareOptions> options)
                : base(
                    options,
                    new UnusedCloudflareClient(),
                    new UnusedHttpClientFactory(),
                    NullLoggerFactory.Instance)
            {
                _manager = manager;
            }

            public override CertificateManager? GetOrCreateManager() => _manager;
        }

        private sealed class UnusedCloudflareClient : ICloudflareDnsClient
        {
            public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
                string zoneId,
                string fqdn,
                string type,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");

            public Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
                string zoneId,
                string fqdn,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");

            public Task<CloudflareDnsRecord> CreateRecordAsync(
                string zoneId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");

            public Task<CloudflareDnsRecord> UpdateRecordAsync(
                string zoneId,
                string recordId,
                CloudflareDnsRecordWriteRequest request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");

            public Task DeleteRecordAsync(string zoneId, string recordId, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Cloudflare should not be used in this test.");
        }

        private sealed class UnusedHttpClientFactory : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new();
        }

        private sealed class ThrowingIssuer : ICertificateIssuer
        {
            public Task<CertificateMaterial> IssueAsync(IReadOnlyList<string> domains, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Issuance should not run when a usable certificate exists.");
        }

        private sealed class NoOpPublisher : IAcmeCertificatePublisher
        {
            public void PublishFromPfx(ReadOnlySpan<byte> pfxBytes, string password)
            {
            }
        }

        private sealed class MarkingPublisher : IAcmeCertificatePublisher
        {
            private readonly IAcmeCertificateReadiness _readiness;

            public MarkingPublisher(IAcmeCertificateReadiness readiness)
            {
                _readiness = readiness;
            }

            public void PublishFromPfx(ReadOnlySpan<byte> pfxBytes, string password)
            {
                _readiness.MarkReady();
            }
        }
    }
}
