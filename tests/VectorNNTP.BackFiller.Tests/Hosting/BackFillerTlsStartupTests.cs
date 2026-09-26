using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Tests.Hosting;

public sealed class BackFillerTlsStartupTests
{
    [Fact]
    public void BindPortTls_zero_fails_tls_only_validation()
    {
        var options = BackFillerTestOptions.CreateValidAcme();
        options.BindPortTls = 0;
        var result = new TlsOnlyAcmeCloudflareOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("TLS-only", result.Failures!.Single(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void BindPortTls_out_of_range_fails_tls_only_validation(int port)
    {
        var options = BackFillerTestOptions.CreateValidAcme();
        options.BindPortTls = port;
        var result = new TlsOnlyAcmeCloudflareOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void BindPortTls_valid_is_the_only_listener_port()
    {
        var identity = BackFillerTestOptions.CreateValid();
        var acme = BackFillerTestOptions.CreateValidAcme(identity);
        acme.BindPort = 119;
        acme.BindPortTls = 5630;
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            identity,
            BackFillerTestOptions.CreateValidConnectionStrings(),
            acme);

        Assert.Equal(5630, runtime.BindPort);
        Assert.DoesNotContain(119, runtime.BindAddressTokens.Select(_ => runtime.BindPort).Distinct().Except([5630]));
    }

    [Fact]
    public void Runtime_factory_does_not_fall_back_to_BindPort_when_tls_is_disabled()
    {
        var identity = BackFillerTestOptions.CreateValid();
        var acme = BackFillerTestOptions.CreateValidAcme(identity);
        acme.BindPort = 1190;
        acme.BindPortTls = 0;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BackFillerRuntimeOptionsFactory.Create(
                identity,
                BackFillerTestOptions.CreateValidConnectionStrings(),
                acme));
        Assert.Contains("TLS-only", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BindPortTls", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listener_does_not_start_when_acme_is_not_ready()
    {
        var runtime = CreateRuntime(GetFreePort());
        var readiness = new AcmeCertificateReadiness();
        var journal = new BackFillerStartupJournal();
        await using var service = new CacheListenerService(
            runtime,
            new StaticCacheListenerCertificateSource(),
            new ArticleRetentionAuthority(runtime, TimeProvider.System, NullLogger<ArticleRetentionAuthority>.Instance),
            readiness,
            journal,
            NullLogger<CacheListenerService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("ACME certificate is not ready", ex.Message, StringComparison.Ordinal);
        Assert.Equal(CacheListenerState.Stopped, service.State);
        Assert.DoesNotContain(BackFillerStartupStages.ListenerStarted, journal.Stages);
        Assert.Empty(service.LocalEndPoints);
    }

    [Fact]
    public async Task Listener_does_not_start_when_certificate_is_missing()
    {
        var runtime = CreateRuntime(GetFreePort());
        var readiness = new AcmeCertificateReadiness();
        readiness.MarkReady();
        var journal = new BackFillerStartupJournal();
        await using var service = new CacheListenerService(
            runtime,
            new MissingCertificateSource(),
            new ArticleRetentionAuthority(runtime, TimeProvider.System, NullLogger<ArticleRetentionAuthority>.Instance),
            readiness,
            journal,
            NullLogger<CacheListenerService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("no TLS certificate", ex.Message, StringComparison.Ordinal);
        Assert.Equal(CacheListenerState.Stopped, service.State);
        Assert.DoesNotContain(BackFillerStartupStages.ListenerStarted, journal.Stages);
    }

    [Fact]
    public async Task Valid_certificate_starts_tls_listener_on_BindPortTls_only()
    {
        var port = GetFreePort();
        var runtime = CreateRuntime(port);
        var readiness = new AcmeCertificateReadiness();
        readiness.MarkReady();
        var journal = new BackFillerStartupJournal();
        await using var service = new CacheListenerService(
            runtime,
            new StaticCacheListenerCertificateSource(),
            new ArticleRetentionAuthority(runtime, TimeProvider.System, NullLogger<ArticleRetentionAuthority>.Instance),
            readiness,
            journal,
            NullLogger<CacheListenerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(CacheListenerState.Running, service.State);
            Assert.Contains(BackFillerStartupStages.ListenerStarted, journal.Stages);
            Assert.All(service.LocalEndPoints, endpoint =>
            {
                var ip = Assert.IsType<IPEndPoint>(endpoint);
                Assert.Equal(port, ip.Port);
            });
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hosted_startup_records_acme_ready_before_listener()
    {
        var builder = Host.CreateApplicationBuilder([]);
        var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
        var port = GetFreePort();
        pairs["BindPortTls"] = port.ToString();
        pairs["BindPort"] = "119";
        pairs["BindAddress:0"] = "*";
        builder.Configuration.AddInMemoryCollection(pairs);
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<VectorNNTP.NNTPD.Cloudflare.ICloudflareDnsReconciler>(
            new NoOpCloudflareDnsReconciler());
        builder.Services.AddSingleton<IPhysicalMemoryProvider>(
            new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
        builder.Services.AddSingleton<IBackFillerRabbitMqConnectionFactory>(
            new FakeBackFillerRabbitMqConnectionFactory());
        builder.Services.AddSingleton<ICacheListenerCertificateSource>(new StaticCacheListenerCertificateSource());
        builder.Services.AddSingleton<VectorNNTP.BackFiller.Accounts.IProviderAccountSource>(
            new FakeProviderAccountSource());
        builder.AddBackFillerHosting();
        ReplaceAcme(builder.Services);

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var journal = host.Services.GetRequiredService<IBackFillerStartupJournal>();
            var stages = journal.Stages.ToList();
            Assert.Contains(BackFillerStartupStages.Configuration, stages);
            Assert.Contains(BackFillerStartupStages.BindResolution, stages);
            Assert.Contains(BackFillerStartupStages.CloudflareReconciled, stages);
            Assert.Contains(BackFillerStartupStages.AcmeCertificateReady, stages);
            Assert.Contains(BackFillerStartupStages.ListenerStarted, stages);

            var acmeIndex = stages.IndexOf(BackFillerStartupStages.AcmeCertificateReady);
            var listenerIndex = stages.IndexOf(BackFillerStartupStages.ListenerStarted);
            Assert.InRange(acmeIndex, 0, listenerIndex - 1);

            var listener = host.Services.GetRequiredService<CacheListenerService>();
            Assert.Equal(CacheListenerState.Running, listener.State);
            Assert.All(listener.LocalEndPoints, endpoint =>
            {
                var ip = Assert.IsType<IPEndPoint>(endpoint);
                Assert.Equal(port, ip.Port);
            });
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public void Certificate_identities_are_only_the_backfiller_fqdn()
    {
        var acme = BackFillerTestOptions.CreateValidAcme();
        acme.IncludeNewsHostnameInCertificate = false;
        var names = CertificateIdentities.ForFqdn(acme.Fqdn, acme.IncludeNewsHostnameInCertificate);
        Assert.Equal(["backfiller01.usenet.ninja"], names);
        Assert.DoesNotContain("news.usenet.ninja", names);
        Assert.DoesNotContain("*.usenet.ninja", names);
    }

    private static BackFillerRuntimeOptions CreateRuntime(int tlsPort)
    {
        var identity = BackFillerTestOptions.CreateValid();
        identity.BindAddress = ["127.0.0.1"];
        var acme = BackFillerTestOptions.CreateValidAcme(identity);
        acme.BindAddress = ["127.0.0.1"];
        acme.BindPortTls = tlsPort;
        return BackFillerRuntimeOptionsFactory.Create(
            identity,
            BackFillerTestOptions.CreateValidConnectionStrings(),
            acme);
    }

    private static void ReplaceAcme(IServiceCollection services)
    {
        for (var i = 0; i < services.Count; i++)
        {
            if (services[i].ImplementationType == typeof(AcmeCertificateHostedService))
            {
                services[i] = ServiceDescriptor.Singleton<IHostedService, ImmediateAcmeReadyHostedService>();
            }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class MissingCertificateSource : ICacheListenerCertificateSource
    {
        public bool TryGetCurrent(out CacheListenerCertificateMaterial material)
        {
            material = null!;
            return false;
        }
    }
}
