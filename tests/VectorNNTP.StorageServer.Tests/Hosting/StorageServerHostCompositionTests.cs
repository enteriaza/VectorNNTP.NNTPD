using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.StorageServer.Acme;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Hosting;
using VectorNNTP.StorageServer.Hosting.Systemd;
using VectorNNTP.StorageServer.Listener;
using VectorNNTP.StorageServer.Logging;
using VectorNNTP.StorageServer.Tests.Fixtures;
using VectorNNTP.StorageServer.Tests.TestDoubles;

namespace VectorNNTP.StorageServer.Tests.Hosting;

public sealed class StorageServerHostCompositionTests
{
    [Fact]
    public void AddStorageServerHosting_registers_expected_service_graph()
    {
        using var host = CreateHost(replaceAcme: false, replaceRabbitMq: true);

        var hosted = host.Services.GetServices<IHostedService>()
            .Where(static service => service.GetType().Assembly == typeof(StorageServerServiceCollectionExtensions).Assembly)
            .ToArray();
        Assert.Equal(3, hosted.Length);
        Assert.IsType<SystemdLifecycleNotifier>(hosted[0]);
        Assert.IsType<SystemdWatchdogService>(hosted[1]);
        Assert.IsType<StorageServerHostedService>(hosted[2]);

        Assert.NotNull(host.Services.GetRequiredService<IApplicationHealth>());
        Assert.NotNull(host.Services.GetRequiredService<ISystemdNotifyBridge>());
        Assert.NotNull(host.Services.GetRequiredService<ISystemdRuntime>());
        Assert.NotNull(host.Services.GetRequiredService<ApplicationLifecycle>());
        Assert.NotNull(host.Services.GetRequiredService<ApplicationServiceManager>());
        Assert.NotNull(host.Services.GetRequiredService<StorageServerHostShutdown>());
        Assert.NotNull(host.Services.GetRequiredService<StorageServerRuntimeOptions>());
        Assert.NotNull(host.Services.GetRequiredService<IRabbitMqService>());
        Assert.Same(
            host.Services.GetRequiredService<RabbitMqService>(),
            host.Services.GetRequiredService<IRabbitMqService>());

        var application = host.Services.GetServices<IApplicationService>().ToArray();
        Assert.Equal(7, application.Length);
        Assert.IsType<VectorNNTP.StorageServer.Storage.StorageEngineApplicationService>(application[0]);
        Assert.IsType<CloudflareDnsReconciliationApplicationService>(application[1]);
        Assert.IsType<RabbitMqService>(application[2]);
        Assert.IsType<VectorNNTP.StorageServer.Storage.StorageServerAdvertisementPublisherService>(application[3]);
        Assert.IsType<VectorNNTP.StorageServer.Storage.StorageArticleLookupConsumerService>(application[4]);
        Assert.IsType<AcmeCertificateApplicationService>(application[5]);
        Assert.Same(host.Services.GetRequiredService<StorageVatpListenerService>(), application[6]);
        Assert.IsType<NullStorageArticleOpenBoundary>(host.Services.GetRequiredService<IStorageArticleOpenBoundary>());
        Assert.NotNull(host.Services.GetRequiredService<VectorNNTP.StorageServer.Storage.IStorageCapacityReader>());
        Assert.NotNull(host.Services.GetRequiredService<VectorNNTP.StorageServer.Storage.IStorageArticlePresence>());
        Assert.NotNull(host.Services.GetRequiredService<VectorNNTP.StorageServer.Storage.StorageServerAdvertisementPublisherService>());
        Assert.NotNull(host.Services.GetRequiredService<VectorNNTP.StorageServer.Storage.StorageEngineApplicationService>());
        Assert.False(host.Services.GetRequiredService<VectorNNTP.StorageServer.Storage.StorageEngineApplicationService>().IsReady);
    }

    [Fact]
    public void AddRabbitMqInfrastructure_does_not_connect_during_registration()
    {
        var factory = new FakeStorageServerRabbitMqConnectionFactory();
        var builder = Host.CreateApplicationBuilder([]);
        var pairs = StorageServerTestOptions.CreateValidConfigurationPairs();
        pairs["StorageServer:BindPortTls"] = GetFreePort().ToString();
        pairs["StorageServer:BindAddress:0"] = "*";
        builder.Configuration.AddInMemoryCollection(pairs);
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<ICloudflareDnsReconciler>(new NoOpCloudflareDnsReconciler());
        builder.ConfigureStorageServerLogging();
        builder.ConfigureStorageServerPlatformHosting();
        builder.AddStorageServerHosting();
        builder.Services.AddSingleton<IRabbitMqConnectionFactory>(factory);

        using var host = builder.Build();

        Assert.NotNull(host.Services.GetRequiredService<IRabbitMqService>());
        Assert.NotNull(host.Services.GetRequiredService<RabbitMqService>());
        Assert.Equal(0, factory.ConnectCount);
    }

    [Fact]
    public void Runtime_options_snapshot_is_singleton_with_cache_fqdn()
    {
        using var host = CreateHost();
        var first = host.Services.GetRequiredService<StorageServerRuntimeOptions>();
        var second = host.Services.GetRequiredService<StorageServerRuntimeOptions>();
        Assert.Same(first, second);
        Assert.Equal("cache01.usenet.ninja", first.Fqdn);
        Assert.Equal(0, first.BindPort);
        Assert.Equal(
            first.GracefulShutdownTimeout,
            host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    [Fact]
    public async Task Host_starts_application_services_in_order_when_acme_and_rabbitmq_are_stubbed()
    {
        using var host = CreateHost(replaceAcme: true, replaceRabbitMq: true);
        await host.StartAsync();
        try
        {
            Assert.Equal(
                ApplicationState.Running,
                host.Services.GetRequiredService<ApplicationLifecycle>().State);
            var application = host.Services.GetServices<IApplicationService>().ToArray();
            Assert.IsType<VectorNNTP.StorageServer.Storage.StorageEngineApplicationService>(application[0]);
            Assert.IsType<CloudflareDnsReconciliationApplicationService>(application[1]);
            Assert.IsType<RabbitMqService>(application[2]);
            Assert.IsType<VectorNNTP.StorageServer.Storage.StorageServerAdvertisementPublisherService>(application[3]);
            Assert.IsType<VectorNNTP.StorageServer.Storage.StorageArticleLookupConsumerService>(application[4]);
            Assert.IsType<ImmediateAcmeReadyApplicationService>(application[5]);
            var listener = host.Services.GetRequiredService<StorageVatpListenerService>();
            Assert.Same(listener, application[6]);
            Assert.Equal(StorageVatpListenerState.Running, listener.State);
            Assert.True(host.Services.GetRequiredService<IRabbitMqService>().IsReady);

            var storageService = host.Services.GetRequiredService<VectorNNTP.StorageServer.Storage.StorageEngineApplicationService>();
            Assert.True(storageService.IsReady);
            var engine = host.Services.GetRequiredService<VectorNNTP.StorageServer.Storage.Engine.Durable.FileArticleStorageEngine>();
            Assert.Same(storageService.Engine, engine);
            Assert.Same(
                storageService,
                host.Services.GetServices<IApplicationService>().OfType<VectorNNTP.StorageServer.Storage.StorageEngineApplicationService>().Single());
            Assert.DoesNotContain(
                typeof(StorageServerServiceCollectionExtensions).Assembly.GetTypes(),
                static t => t.Name.Contains("MaintenanceService", StringComparison.Ordinal)
                            && typeof(IApplicationService).IsAssignableFrom(t));
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.Equal(
            ApplicationState.Stopped,
            host.Services.GetRequiredService<ApplicationLifecycle>().State);
    }

    [Fact]
    public void Common_assembly_contains_shared_cache_fleet_contract_not_nntpd_topology_types()
    {
        var common = typeof(RabbitMqService).Assembly;
        Assert.Null(common.GetType("VectorNNTP.NNTPD.RabbitMq.OverviewDbTopology"));
        Assert.Null(common.GetType("VectorNNTP.NNTPD.RabbitMq.BackfillArticleRetrievalTopology"));
        Assert.Null(common.GetType("VectorNNTP.Common.Messaging.RabbitMq.OverviewDbTopology"));
        Assert.NotNull(common.GetType("VectorNNTP.Common.Messaging.Cache.CacheFleetTopology"));
        Assert.DoesNotContain(
            common.GetManifestResourceNames(),
            static name => name.Contains("backfiller.storage", StringComparison.OrdinalIgnoreCase));

        var topologyNames = common.GetTypes()
            .SelectMany(static type => type.GetFields(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Instance))
            .Where(static field => field.FieldType == typeof(string) && field.IsLiteral)
            .Select(static field => field.GetRawConstantValue() as string)
            .Where(static value => !string.IsNullOrEmpty(value))
            .ToArray();
        Assert.Contains(topologyNames, static value => value == "cache.requests");
        Assert.Contains(topologyNames, static value => value == "cache.broadcast");
        Assert.DoesNotContain(topologyNames, static value => value == "backfiller.storage");
    }

    [Fact]
    public void StorageServer_assembly_does_not_reference_NNTPD()
    {
        var referenced = typeof(StorageServerServiceCollectionExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(static name => name.Name);

        Assert.DoesNotContain("VectorNNTP.NNTPD", referenced);
    }

    private static IHost CreateHost(bool replaceAcme = true, bool replaceRabbitMq = true)
    {
        var builder = Host.CreateApplicationBuilder([]);
        var pairs = StorageServerTestOptions.CreateValidConfigurationPairs();
        var tlsPort = GetFreePort();
        pairs["StorageServer:BindPortTls"] = tlsPort.ToString();
        pairs["StorageServer:BindAddress:0"] = "*";
        builder.Configuration.AddInMemoryCollection(pairs);
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<ICloudflareDnsReconciler>(new NoOpCloudflareDnsReconciler());
        builder.ConfigureStorageServerLogging();
        builder.ConfigureStorageServerPlatformHosting();
        builder.AddStorageServerHosting();
        if (replaceRabbitMq)
        {
            builder.Services.AddSingleton<IRabbitMqConnectionFactory, FakeStorageServerRabbitMqConnectionFactory>();
        }

        if (replaceAcme)
        {
            ReplaceAcmeApplicationServiceWithImmediateReady(builder.Services);
        }

        return builder.Build();
    }

    private static void ReplaceAcmeApplicationServiceWithImmediateReady(IServiceCollection services)
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

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
