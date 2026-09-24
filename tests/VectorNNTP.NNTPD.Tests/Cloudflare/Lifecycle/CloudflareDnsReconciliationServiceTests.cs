using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Hosting;
using VectorNNTP.NNTPD.Logging;
using VectorNNTP.NNTPD.Networking;
using VectorNNTP.NNTPD.Networking.Listeners;

namespace VectorNNTP.NNTPD.Tests.Cloudflare.Lifecycle;

[Collection(SerilogCollection.Name)]
public sealed class CloudflareDnsReconciliationServiceTests
{
    [Fact]
    public async Task StartAsync_WithNoEligibleAddresses_Fails()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["127.0.0.1"];

        var service = CreateService(
            options,
            new FakeLocalIpAddressAssignee(IPAddress.Loopback),
            new FakeCloudflareDnsClient());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(CancellationToken.None));

        Assert.Contains("No eligible IP addresses", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WhenReconcileFails_PropagatesFailure()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];

        var client = new FakeCloudflareDnsClient
        {
            CreateException = new CloudflareDnsException("boom"),
        };

        var service = CreateService(
            options,
            new FakeLocalIpAddressAssignee(TestHostFactory.TestIpv4),
            client);

        await Assert.ThrowsAsync<CloudflareDnsException>(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Lifecycle_DnsReconciliationCompletesBeforeRunning()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.BindAddress = ["*"];

        var client = new FakeCloudflareDnsClient();
        var dnsService = CreateService(
            options,
            new FakeLocalIpAddressAssignee(TestHostFactory.TestIpv4, TestHostFactory.TestIpv6),
            client);

        var order = new List<string>();
        var other = new FakeApplicationService("other")
        {
            SharedStartOrder = order,
        };

        var trackingDns = new TrackingApplicationService(dnsService, order, "dns");

        await using var lifecycle = TestHostFactory.CreateLifecycle([trackingDns, other], options);
        await lifecycle.StartAsync(CancellationToken.None);

        Assert.Equal(ApplicationState.Running, lifecycle.State);
        Assert.Equal(["dns", "other"], order);
        Assert.True(client.CreateCallCount >= 2);
        await lifecycle.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Lifecycle_DnsFailure_PreventsRunning()
    {
        var options = TestHostFactory.CreateValidOptions();
        var client = new FakeCloudflareDnsClient
        {
            ListException = new CloudflareDnsException("list failed"),
        };

        var dnsService = CreateService(
            options,
            new FakeLocalIpAddressAssignee(TestHostFactory.TestIpv4),
            client);

        var other = new FakeApplicationService("other");
        await using var lifecycle = TestHostFactory.CreateLifecycle([dnsService, other], options);

        await Assert.ThrowsAsync<CloudflareDnsException>(() => lifecycle.StartAsync(CancellationToken.None));
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        Assert.Equal(0, other.StartCount);
    }

    [Fact]
    public async Task Host_RegistersDnsReconciliation_BeforeOtherServices()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpdHosting(includePlaceholderService: false);

        var started = new List<string>();
        builder.Services.AddSingleton<IApplicationService>(
            new FakeApplicationService(
                "app",
                onStart: _ =>
                {
                    started.Add("app");
                    return Task.CompletedTask;
                }));

        using var host = builder.Build();
        var services = host.Services.GetServices<IApplicationService>().ToArray();
        Assert.Equal(typeof(CloudflareDnsReconciliationService), services[0].GetType());
        Assert.Equal(typeof(VectorNNTP.NNTPD.Redis.RedisService), services[1].GetType());
        Assert.Equal(typeof(VectorNNTP.NNTPD.History.HistoryWriteService), services[2].GetType());
        Assert.Equal(typeof(VectorNNTP.NNTPD.History.HistoryMaintenanceService), services[3].GetType());
        Assert.Equal(typeof(IncomingSpoolWriterService), services[4].GetType());
        Assert.Equal(typeof(VectorNNTP.NNTPD.Transit.TransitDnsRefreshService), services[5].GetType());
        Assert.Equal(typeof(NntpPlainListenerService), services[6].GetType());
        Assert.Equal(typeof(AcmeCertificateService), services[7].GetType());
        Assert.Equal(typeof(NntpTlsListenerService), services[8].GetType());

        await host.StartAsync();
        Assert.Equal(ApplicationState.Running, host.Services.GetRequiredService<ApplicationLifecycle>().State);
        Assert.Equal(["app"], started);
        await host.StopAsync();
    }

    [Fact]
    public async Task StartAsync_Cancellation_Propagates()
    {
        var options = TestHostFactory.CreateValidOptions();
        var client = new FakeCloudflareDnsClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = CreateService(
            options,
            new FakeLocalIpAddressAssignee(TestHostFactory.TestIpv4),
            client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(cts.Token));
    }

    private static CloudflareDnsReconciliationService CreateService(
        NntpdOptions options,
        ILocalIpAddressAssignee assignee,
        ICloudflareDnsClient client)
    {
        var resolver = new BindAddressResolver(assignee, NullLogger<BindAddressResolver>.Instance);
        var reconciler = new CloudflareDnsReconciler(client, Options.Create(TestHostFactory.CreateValidOptions()), NullLogger<CloudflareDnsReconciler>.Instance);
        return new CloudflareDnsReconciliationService(
            Options.Create(options),
            resolver,
            reconciler,
            NullLogger<CloudflareDnsReconciliationService>.Instance);
    }

    private sealed class TrackingApplicationService : IApplicationService
    {
        private readonly IApplicationService _inner;
        private readonly List<string> _order;
        private readonly string _label;

        public TrackingApplicationService(IApplicationService inner, List<string> order, string label)
        {
            _inner = inner;
            _order = order;
            _label = label;
        }

        public string Name => _inner.Name;

        public Task? Execution => _inner.Execution;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
            _order.Add(_label);
        }

        public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);
    }
}
