using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.Acme;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Hosting.Systemd;
using VectorNNTP.BackFiller.Logging;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.Common.Acme;
using VectorNNTP.Common.Cloudflare;
using VectorNNTP.Common.Core;
using VectorNNTP.Common.Networking.Certificates;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;

using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.BackFiller.Tests.Hosting
{
    public sealed class BackFillerHostCompositionTests
    {
        [Fact]
        public void AddBackFillerHosting_registers_rabbitmq_then_article_work_consumers()
        {
            using var host = CreateHost(replaceAcme: false);

            Assert.Same(TimeProvider.System, host.Services.GetRequiredService<TimeProvider>());
            var hosted = host.Services.GetServices<IHostedService>()
                .Where(static service => service.GetType().Assembly == typeof(BackFillerServiceCollectionExtensions).Assembly)
                .ToArray();
            Assert.Equal(9, hosted.Length);
            Assert.IsType<SystemdLifecycleNotifier>(hosted[0]);
            Assert.IsType<SystemdWatchdogService>(hosted[1]);
            Assert.IsType<RabbitMqServiceHostedAdapter>(hosted[2]);
            Assert.Same(host.Services.GetRequiredService<ProviderAccountConfigurationService>(), hosted[3]);
            Assert.Same(host.Services.GetRequiredService<NntpProviderRegistry>(), hosted[4]);
            Assert.IsType<BackFillerApplicationHostedService>(hosted[5]);
            Assert.Same(host.Services.GetRequiredService<ArticleRetentionSweepService>(), hosted[6]);
            Assert.Same(host.Services.GetRequiredService<ArticleWorkResponsePublisher>(), hosted[7]);
            Assert.Same(host.Services.GetRequiredService<ArticleWorkConsumerService>(), hosted[8]);
            Assert.Same(
                host.Services.GetRequiredService<BackFillerApplicationHealth>(),
                host.Services.GetRequiredService<IApplicationHealth>());
            Assert.NotNull(host.Services.GetRequiredService<ISystemdNotifyBridge>());
            Assert.NotNull(host.Services.GetRequiredService<ISystemdRuntime>());
            Assert.DoesNotContain(
                hosted,
                static service => service.GetType().Name == "AcmeCertificateHostedService");
            Assert.DoesNotContain(
                hosted,
                static service => service.GetType().Name == "CloudflareDnsReconciliationHostedService");
            var application = host.Services.GetServices<IApplicationService>().ToArray();
            Assert.Equal(3, application.Length);
            Assert.IsType<CloudflareDnsReconciliationApplicationService>(application[0]);
            Assert.Equal(
                typeof(CloudflareDnsReconciliationService).Assembly,
                application[0].GetType().Assembly);
            Assert.IsType<AcmeCertificateApplicationService>(application[1]);
            Assert.Same(host.Services.GetRequiredService<CacheListenerService>(), application[2]);
            Assert.Same(
                host.Services.GetRequiredService<ArticleRetentionAuthority>(),
                host.Services.GetRequiredService<IArticleRetentionAuthority>());
            Assert.Same(
                host.Services.GetRequiredService<ProviderConfigurationCatalog>(),
                host.Services.GetRequiredService<IBackFillerProviderCatalog>());
            Assert.IsType<FakeProviderAccountSource>(host.Services.GetRequiredService<IProviderAccountSource>());
            Assert.IsType<ProviderArticleWorkHandler>(host.Services.GetRequiredService<IArticleWorkHandler>());
            Assert.Same(
                host.Services.GetRequiredService<ArticleWorkResponsePublisher>(),
                host.Services.GetRequiredService<IArticleWorkResponsePublisher>());
            var publisher = host.Services.GetRequiredService<IAcmeCertificatePublisher>();
            Assert.Same(host.Services.GetRequiredService<TlsCertificateContextProvider>(), publisher);
            Assert.Same(host.Services.GetRequiredService<ITlsCertificateContextProvider>(), publisher);
            Assert.NotNull(host.Services.GetRequiredService<CloudflareDnsReconciliationService>());
        }

        [Fact]
        public void AddBackFillerHosting_registers_a_single_runtime_options_snapshot()
        {
            using var host = CreateHost();

            var first = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            var second = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            Assert.Same(first, second);
            Assert.Equal("backfiller01.usenet.ninja", first.Fqdn);
            Assert.Equal("127.0.0.1", first.NntpDb.Server);
            Assert.Equal("nntp", first.NntpDb.Database);
            var nntpDb = host.Services.GetRequiredService<IOptions<NntpDbOptions>>().Value;
            Assert.Equal(first.NntpDb.ConnectionString, nntpDb.ConnectionString);
            Assert.Contains("Database=nntp", nntpDb.ConnectionString, StringComparison.Ordinal);
            Assert.Equal(TimeSpan.FromSeconds(60), first.AccountRefreshInterval);
            Assert.Equal(TimeSpan.FromSeconds(45), first.Shutdown.GracePeriod);
            Assert.Equal(
                first.Shutdown.GracePeriod,
                host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
        }

        [Fact]
        public async Task Host_starts_rabbitmq_and_article_work_consumers_without_a_placeholder_background_service()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            using var host = CreateHost(factory);

            await host.StartAsync();
            try
            {
                Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
                var rabbit = host.Services.GetRequiredService<IRabbitMqService>();
                Assert.True(rabbit.IsReady);
                Assert.Equal(1, rabbit.ConnectionGeneration);
                var consumer = host.Services.GetRequiredService<ArticleWorkConsumerService>();
                Assert.Empty(consumer.Sessions);
                Assert.Empty(factory.LastConnection!.Channels);
                Assert.Single(factory.LastConnection.PublishChannels);
                Assert.Equal(1, factory.ConnectCount);
                Assert.Contains(
                    host.Services.GetServices<IHostedService>(),
                    static service => service is ArticleRetentionSweepService);
                Assert.DoesNotContain(
                    host.Services.GetServices<IHostedService>(),
                    static service => service.GetType().Name == "PlaceholderBackgroundService");
            }
            finally
            {
                await host.StopAsync();
            }

            Assert.False(host.Services.GetRequiredService<IRabbitMqService>().IsReady);
            Assert.All(factory.LastConnection!.Channels, static channel => Assert.Equal(1, channel.DisposeCount));
            Assert.All(factory.LastConnection.PublishChannels, static channel => Assert.Equal(1, channel.DisposeCount));
        }

        [Fact]
        public void AddBackFillerHosting_registers_rabbitmq_without_connecting()
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            using var host = CreateHost(factory);

            Assert.Equal(0, factory.ConnectCount);
            Assert.NotNull(host.Services.GetRequiredService<IRabbitMqService>());
            Assert.Same(
                host.Services.GetRequiredService<RabbitMqService>(),
                host.Services.GetRequiredService<IRabbitMqService>());
            Assert.Equal(0, factory.ConnectCount);
        }

        [Fact]
        public void AddBackFillerHosting_registers_os_physical_memory_provider()
        {
            var builder = Host.CreateApplicationBuilder([]);
            builder.AddBackFillerHosting();

            var descriptor = Assert.Single(
                builder.Services,
                static service => service.ServiceType == typeof(IPhysicalMemoryProvider));
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.Equal(typeof(OsPhysicalMemoryProvider), descriptor.ImplementationType);
        }

        [Fact]
        public void AddBackFillerHosting_defaults_to_the_mysql_account_source()
        {
            using var host = CreateHost(injectAccountSource: false);

            Assert.IsType<MySqlProviderAccountSource>(host.Services.GetRequiredService<IProviderAccountSource>());
            Assert.IsType<ProviderConfigurationCatalog>(host.Services.GetRequiredService<IBackFillerProviderCatalog>());
        }

        [Fact]
        public void BackFiller_assembly_does_not_reference_NNTPD()
        {
            var referenced = typeof(BackFillerServiceCollectionExtensions).Assembly
                .GetReferencedAssemblies()
                .Select(static name => name.Name);

            Assert.DoesNotContain("VectorNNTP.NNTPD", referenced);
        }

        private static IHost CreateHost(
            FakeBackFillerRabbitMqConnectionFactory? factory = null,
            bool injectAccountSource = true,
            bool replaceAcme = true)
        {
            var builder = Host.CreateApplicationBuilder([]);
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            var tlsPort = GetFreePort();
            pairs["BackFiller:BindPortTls"] = tlsPort.ToString();
            pairs["BackFiller:BindAddress:0"] = "*";
            pairs["BackFiller:Logging:File:LogDir"] = Directory.CreateTempSubdirectory("bf-host-logs-").FullName;
            builder.Configuration.AddInMemoryCollection(pairs);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<VectorNNTP.Common.Cloudflare.ICloudflareDnsReconciler>(new NoOpCloudflareDnsReconciler());
            builder.Services.AddSingleton<IPhysicalMemoryProvider>(new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
            builder.Services.AddSingleton<IRabbitMqConnectionFactory>(
                factory ?? new FakeBackFillerRabbitMqConnectionFactory());
            if (injectAccountSource)
            {
                builder.Services.AddSingleton<IProviderAccountSource>(new FakeProviderAccountSource());
            }

            builder.ConfigureBackFillerLogging();
            builder.ConfigureBackFillerPlatformHosting();
            builder.AddBackFillerHosting();
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
}
