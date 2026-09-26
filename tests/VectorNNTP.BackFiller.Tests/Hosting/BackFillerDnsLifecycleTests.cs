using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.Acme;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Core;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Networking;

namespace VectorNNTP.BackFiller.Tests.Hosting;

public sealed class BackFillerDnsLifecycleTests
{
    private const string UnrelatedFqdn = "nntpd01.usenet.ninja";

    [Fact]
    public void Adapter_is_the_shared_common_type()
    {
        Assert.Equal(
            typeof(CloudflareDnsReconciliationService).Assembly,
            typeof(CloudflareDnsReconciliationApplicationService).Assembly);
        Assert.Equal(
            "VectorNNTP.NNTPD.Cloudflare",
            typeof(CloudflareDnsReconciliationApplicationService).Namespace);
        Assert.Null(
            typeof(BackFillerServiceCollectionExtensions).Assembly
                .GetType("VectorNNTP.BackFiller.Hosting.CloudflareDnsReconciliationHostedService"));
    }

    [Fact]
    public async Task Dns_startup_failure_does_not_start_later_services_and_cleans_up()
    {
        var harness = CreateDnsHarness();
        harness.Reconciler.ReconcileException = new CloudflareDnsException("reconcile failed");
        var order = new List<string>();
        var dns = new OrderRecordingService(harness.Adapter, order);
        var later = new OrderRecordingService("AcmeCertificate", order);
        var manager = CreateManager(dns, later);

        await Assert.ThrowsAsync<CloudflareDnsException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(["start:CloudflareDnsReconciliation"], order);
        Assert.Equal(0, later.StartCount);
        Assert.Empty(manager.StartedServices);
        Assert.Equal([harness.Options.Fqdn], harness.Reconciler.ReconcileCalls.Select(static call => call.Fqdn));
        Assert.Equal([harness.Options.Fqdn], harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
        Assert.DoesNotContain(UnrelatedFqdn, harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
        Assert.Equal(1, harness.Resolver.ResolveCount);
    }

    [Fact]
    public async Task Dns_succeeds_then_acme_failure_rolls_back_exact_backfiller_fqdn()
    {
        var harness = CreateDnsHarness();
        var order = new List<string>();
        var dns = new OrderRecordingService(harness.Adapter, order);
        var acme = new OrderRecordingService(
            "AcmeCertificate",
            order,
            startException: new InvalidOperationException("acme failed"));
        var listener = new OrderRecordingService("CacheListener", order);
        var manager = CreateManager(dns, acme, listener);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(
            ["start:CloudflareDnsReconciliation", "start:AcmeCertificate", "stop:CloudflareDnsReconciliation"],
            order);
        Assert.Equal(0, listener.StartCount);
        Assert.Equal(1, harness.Resolver.ResolveCount);
        Assert.Equal([harness.Options.Fqdn], harness.Reconciler.ReconcileCalls.Select(static call => call.Fqdn));
        Assert.Equal([harness.Options.Fqdn], harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
        Assert.DoesNotContain(UnrelatedFqdn, harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
        Assert.Empty(manager.StartedServices);
    }

    [Fact]
    public async Task Dns_succeeds_then_listener_failure_rolls_back_exact_backfiller_fqdn()
    {
        var harness = CreateDnsHarness();
        var order = new List<string>();
        var dns = new OrderRecordingService(harness.Adapter, order);
        var acme = new OrderRecordingService("AcmeCertificate", order);
        var listener = new OrderRecordingService(
            "CacheListener",
            order,
            startException: new InvalidOperationException("listener failed"));
        var manager = CreateManager(dns, acme, listener);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));

        Assert.Equal(
            [
                "start:CloudflareDnsReconciliation",
                "start:AcmeCertificate",
                "start:CacheListener",
                "stop:AcmeCertificate",
                "stop:CloudflareDnsReconciliation",
            ],
            order);
        Assert.Equal([harness.Options.Fqdn], harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
        Assert.DoesNotContain(UnrelatedFqdn, harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
        Assert.Empty(manager.StartedServices);
    }

    [Fact]
    public async Task Normal_shutdown_stops_listener_then_acme_then_dns_and_removes_exact_fqdn()
    {
        var harness = CreateDnsHarness();
        var order = new List<string>();
        var dns = new OrderRecordingService(harness.Adapter, order);
        var acme = new OrderRecordingService("AcmeCertificate", order);
        var listener = new OrderRecordingService("CacheListener", order);
        var manager = CreateManager(dns, acme, listener);

        await manager.StartAsync(CancellationToken.None);
        Assert.Equal([dns, acme, listener], manager.StartedServices.ToArray());
        Assert.Equal(1, harness.Resolver.ResolveCount);
        Assert.Empty(harness.Reconciler.RemoveCalls);

        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(
            [
                "start:CloudflareDnsReconciliation",
                "start:AcmeCertificate",
                "start:CacheListener",
                "stop:CacheListener",
                "stop:AcmeCertificate",
                "stop:CloudflareDnsReconciliation",
            ],
            order);
        Assert.Equal([harness.Options.Fqdn], harness.Reconciler.ReconcileCalls.Select(static call => call.Fqdn));
        Assert.Equal([harness.Options.Fqdn], harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
        Assert.DoesNotContain(UnrelatedFqdn, harness.Reconciler.RemoveCalls.Select(static call => call.Fqdn));
        Assert.Empty(manager.StartedServices);
    }

    [Fact]
    public async Task Host_start_and_stop_uses_manager_dns_and_removes_only_the_backfiller_fqdn()
    {
        var reconciler = new RecordingCloudflareDnsReconciler();
        var builder = Host.CreateApplicationBuilder([]);
        var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        pairs["BackFiller:BindPortTls"] = port.ToString();
        pairs["BackFiller:BindAddress:0"] = "*";
        builder.Configuration.AddInMemoryCollection(pairs);
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<ICloudflareDnsReconciler>(reconciler);
        builder.Services.AddSingleton<IPhysicalMemoryProvider>(
            new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
        builder.Services.AddSingleton<IBackFillerRabbitMqConnectionFactory>(
            new FakeBackFillerRabbitMqConnectionFactory());
        builder.Services.AddSingleton<IProviderAccountSource>(new FakeProviderAccountSource());
        builder.AddBackFillerHosting();
        ReplaceAcme(builder.Services);

        using var host = builder.Build();
        var application = host.Services.GetServices<IApplicationService>().ToArray();
        Assert.IsType<CloudflareDnsReconciliationApplicationService>(application[0]);
        Assert.IsType<ImmediateAcmeReadyApplicationService>(application[1]);
        Assert.IsType<CacheListenerService>(application[2]);

        await host.StartAsync();
        try
        {
            Assert.Equal(["backfiller01.usenet.ninja"], reconciler.ReconcileCalls.Select(static call => call.Fqdn));
            Assert.Empty(reconciler.RemoveCalls);
            Assert.Equal(CacheListenerState.Running, host.Services.GetRequiredService<CacheListenerService>().State);
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.Equal(["backfiller01.usenet.ninja"], reconciler.RemoveCalls.Select(static call => call.Fqdn));
        Assert.DoesNotContain(UnrelatedFqdn, reconciler.RemoveCalls.Select(static call => call.Fqdn));
        Assert.DoesNotContain(
            host.Services.GetServices<IHostedService>(),
            static service => service.GetType().Name == "CloudflareDnsReconciliationHostedService");
    }

    private static ApplicationServiceManager CreateManager(params IApplicationService[] services)
    {
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            BackFillerTestOptions.CreateValid(),
            BackFillerTestOptions.CreateValidConnectionStrings());
        return new ApplicationServiceManager(
            services,
            runtime,
            NullLogger<ApplicationServiceManager>.Instance);
    }

    private static DnsHarness CreateDnsHarness()
    {
        var identity = BackFillerTestOptions.CreateValid();
        var options = BackFillerTestOptions.CreateValidAcme(identity);
        options.BindAddress = ["198.18.0.10"];
        var innerResolver = new BindAddressResolver(
            new FakeLocalIpAddressAssignee(assignAll: true),
            NullLogger<BindAddressResolver>.Instance);
        var resolver = new CountingBindAddressResolver(innerResolver);
        var reconciler = new RecordingCloudflareDnsReconciler();
        var inner = new CloudflareDnsReconciliationService(
            Options.Create(options),
            resolver,
            reconciler,
            NullLogger<CloudflareDnsReconciliationService>.Instance);
        return new DnsHarness(
            new CloudflareDnsReconciliationApplicationService(inner),
            reconciler,
            resolver,
            options);
    }

    private static void ReplaceAcme(IServiceCollection services)
    {
        for (var i = 0; i < services.Count; i++)
        {
            if (services[i].ServiceType == typeof(IApplicationService)
                && services[i].ImplementationType == typeof(AcmeCertificateApplicationService))
            {
                services[i] = ServiceDescriptor.Singleton<IApplicationService, ImmediateAcmeReadyApplicationService>();
            }
        }
    }

    private sealed record DnsHarness(
        CloudflareDnsReconciliationApplicationService Adapter,
        RecordingCloudflareDnsReconciler Reconciler,
        CountingBindAddressResolver Resolver,
        AcmeCloudflareOptions Options);

    private sealed class CountingBindAddressResolver(IBindAddressResolver inner) : IBindAddressResolver
    {
        private int _resolveCount;

        public int ResolveCount => Volatile.Read(ref _resolveCount);

        public ResolvedBindAddresses Resolve(AcmeCloudflareOptions options)
        {
            Interlocked.Increment(ref _resolveCount);
            return inner.Resolve(options);
        }
    }

    private sealed class OrderRecordingService : IApplicationService
    {
        private readonly IApplicationService? _inner;
        private readonly List<string> _order;
        private readonly Exception? _startException;

        public OrderRecordingService(IApplicationService inner, List<string> order)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(order);
            _inner = inner;
            _order = order;
            Name = inner.Name;
        }

        public OrderRecordingService(string name, List<string> order, Exception? startException = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(order);
            Name = name;
            _order = order;
            _startException = startException;
        }

        public string Name { get; }

        public Task? Execution => _inner?.Execution;

        public int StartCount { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            _order.Add("start:" + Name);
            if (_startException is not null)
            {
                throw _startException;
            }

            if (_inner is not null)
            {
                await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _order.Add("stop:" + Name);
            if (_inner is not null)
            {
                await _inner.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
