using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.Acme;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Core;
using VectorNNTP.BackFiller.Hosting.Systemd;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Hosting;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Networking.Certificates;

namespace VectorNNTP.BackFiller.Hosting;

/// <summary>
/// Extension methods for registering VectorNNTP.BackFiller hosting services.
/// </summary>
public static class BackFillerServiceCollectionExtensions
{
    /// <summary>
    /// Adds BackFiller host services: configuration, validation, system time, and RabbitMQ connection ownership.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    /// <remarks>
    /// Registers Common <see cref="RabbitMqService"/> via <see cref="RabbitMqServiceHostedAdapter"/>
    /// as an early <see cref="IHostedService"/>. Startup fails if the initial broker
    /// connection cannot be established. Registers <see cref="ProviderAccountConfigurationService"/>
    /// after the connection owner, then <see cref="NntpProviderRegistry"/>,
    /// then <see cref="BackFillerApplicationHostedService"/> (starts
    /// <see cref="CloudflareDnsReconciliationApplicationService"/>, then
    /// <see cref="AcmeCertificateApplicationService"/>, then
    /// <see cref="CacheListenerService"/>), then
    /// <see cref="ArticleRetentionSweepService"/>, then
    /// <see cref="ArticleWorkResponsePublisher"/>,
    /// then <see cref="ArticleWorkConsumerService"/>. Article Work consume
    /// is reconciled from usable NNTP capacity. Before consumers start for a usable
    /// backbone, BackFiller declares that backbone's quorum <c>backfiller.*</c> topology.
    /// RabbitMQ is not registered into BackFiller <see cref="VectorNNTP.BackFiller.Core.ApplicationServiceManager"/>.
    /// </remarks>
    public static HostApplicationBuilder AddBackFillerHosting(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<ILocalIpAddressAssignee, NetworkInterfaceLocalIpAddressAssignee>();
        builder.Services.TryAddSingleton<IPhysicalMemoryProvider, OsPhysicalMemoryProvider>();
        builder.Services.TryAddSingleton(TimeProvider.System);

        builder.Services.TryAddSingleton<IBackFillerStartupJournal, BackFillerStartupJournal>();

        builder.Services
            .AddOptions<RabbitMqOptions>()
            .BindConfiguration(RabbitMqOptions.SectionName)
            .PostConfigure(static options => options.Management ??= new RabbitMqManagementOptions())
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<RabbitMqOptions>, RabbitMqOptionsValidator>();

        builder.Services
            .AddOptions<BackFillerOptions>()
            .BindConfiguration(BackFillerOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<BackFillerOptions>, BackFillerOptionsValidator>();

        builder.Services
            .AddOptions<AcmeCloudflareOptions>()
            .Configure<IOptions<BackFillerOptions>, IConfiguration>(
                static (acme, backfiller, configuration) =>
                {
                    BackFillerAcmeCloudflareOptionsAdapter.Apply(acme, backfiller.Value, configuration);
                })
            // DataAnnotations on AcmeCloudflareOptions are enforced by the registered
            // IValidateOptions implementations (AcmeCloudflareOptionsValidator /
            // TlsOnlyAcmeCloudflareOptionsValidator). Do not call ValidateDataAnnotations():
            // that extension uses reflection and is incompatible with Native AOT.
            .ValidateOnStart()
            .PostConfigure<IBackFillerStartupJournal>(
                static (acme, journal) =>
            {
                AcmeCloudflareOptionsValidator.NormalizeBindAddresses(acme);
                acme.AcmeStateDir = ApplicationLocalPath.ResolveApplicationLocalPath(
                    acme.AcmeStateDir,
                    AppContext.BaseDirectory);
                journal.Record(BackFillerStartupStages.Configuration);
            });
        builder.Services.AddSingleton<IValidateOptions<AcmeCloudflareOptions>, AcmeCloudflareOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<AcmeCloudflareOptions>, TlsOnlyAcmeCloudflareOptionsValidator>();
        builder.Services.AddAcmeCloudflareInfrastructure();
        builder.Services.AddNntpDbOptions();

        builder.Services.AddSingleton(static provider =>
        {
            var options = provider.GetRequiredService<IOptions<BackFillerOptions>>().Value;
            var nntpDb = provider.GetRequiredService<IOptions<NntpDbOptions>>().Value;
            var acme = provider.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
            var rabbitMq = provider.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
            return BackFillerRuntimeOptionsFactory.Create(
                options,
                nntpDb,
                acme,
                rabbitMq,
                AppContext.BaseDirectory);
        });

        builder.Services.AddOptions<HostOptions>()
            .PostConfigure<BackFillerRuntimeOptions>(static (host, runtime) =>
            {
                host.ShutdownTimeout = runtime.Shutdown.GracePeriod;
            });

        // Register systemd lifecycle notifier before other hosted services so it is
        // constructed early and observes ApplicationStarted/Stopping for READY/STOPPING.
        builder.Services.AddSingleton<BackFillerApplicationHealth>();
        builder.Services.AddSingleton<IApplicationHealth>(static provider =>
            provider.GetRequiredService<BackFillerApplicationHealth>());
        builder.Services.TryAddSingleton<ISystemdNotifyBridge, SystemdNotifyBridge>();
        builder.Services.TryAddSingleton<ISystemdRuntime, SystemdRuntime>();
        builder.Services.AddSingleton<SystemdLifecycleNotifier>();
        builder.Services.AddHostedService(static provider =>
            provider.GetRequiredService<SystemdLifecycleNotifier>());
        builder.Services.AddHostedService<SystemdWatchdogService>();

        builder.Services.TryAddSingleton<IRabbitMqConnectionNameProvider>(static sp =>
            new DelegateRabbitMqConnectionNameProvider(() =>
                RabbitMqRuntimeOptions.GetDefaultConnectionName(
                    ApplicationJsonConfiguration.EntryAssemblyName,
                    sp.GetRequiredService<IOptions<BackFillerOptions>>().Value.Fqdn)));
        builder.Services.AddRabbitMqInfrastructure();
        builder.Services.AddSingleton<IHostedService>(static sp =>
            new RabbitMqServiceHostedAdapter(sp.GetRequiredService<RabbitMqService>()));

        builder.Services.TryAddSingleton<IProviderAccountSource, MySqlProviderAccountSource>();
        builder.Services.AddSingleton<ProviderConfigurationCatalog>();
        builder.Services.TryAddSingleton<IBackFillerProviderCatalog>(static provider =>
            provider.GetRequiredService<ProviderConfigurationCatalog>());
        builder.Services.AddSingleton(static provider => new ProviderAccountConfigurationService(
            provider.GetRequiredService<IProviderAccountSource>(),
            provider.GetRequiredService<ProviderConfigurationCatalog>(),
            provider.GetRequiredService<NntpProviderRegistry>(),
            provider.GetRequiredService<BackFillerRuntimeOptions>(),
            provider.GetRequiredService<ILogger<ProviderAccountConfigurationService>>()));
        builder.Services.TryAddSingleton<INntpTransportFactory, TcpNntpTransportFactory>();
        builder.Services.AddSingleton<BackboneUsableCapacityState>();
        builder.Services.AddSingleton<IBackboneUsableCapacityProvider>(static provider =>
            provider.GetRequiredService<BackboneUsableCapacityState>());
        builder.Services.AddSingleton<IBackboneUsableCapacityStateWriter>(static provider =>
            provider.GetRequiredService<BackboneUsableCapacityState>());
        builder.Services.AddSingleton(static provider => new NntpProviderRegistry(
            provider.GetRequiredService<IBackFillerProviderCatalog>(),
            provider.GetRequiredService<INntpTransportFactory>(),
            provider.GetRequiredService<BackFillerRuntimeOptions>(),
            provider.GetRequiredService<ILogger<NntpProviderRegistry>>(),
            provider.GetRequiredService<BackboneUsableCapacityState>(),
            provider));
        builder.Services.AddSingleton<IHostedService>(static provider =>
            provider.GetRequiredService<ProviderAccountConfigurationService>());
        builder.Services.AddSingleton<IHostedService>(static provider =>
            provider.GetRequiredService<NntpProviderRegistry>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, CloudflareDnsReconciliationApplicationService>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, AcmeCertificateApplicationService>());
        builder.Services.TryAddSingleton<INntpArticleRetriever, NntpArticleRetriever>();
        builder.Services.AddSingleton(static provider => new ArticleRetentionAuthority(
            provider.GetRequiredService<BackFillerRuntimeOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ArticleRetentionAuthority>>()));
        builder.Services.AddSingleton<IArticleRetentionAuthority>(static provider =>
            provider.GetRequiredService<ArticleRetentionAuthority>());
        builder.Services.AddSingleton(static provider => new ArticleRetentionSweepService(
            provider.GetRequiredService<IArticleRetentionAuthority>(),
            provider.GetRequiredService<ILogger<ArticleRetentionSweepService>>()));
        builder.Services.AddSingleton(static provider => new CacheListenerService(
            provider.GetRequiredService<BackFillerRuntimeOptions>(),
            provider.GetRequiredService<ITlsCertificateContextProvider>(),
            provider.GetRequiredService<IArticleRetentionAuthority>(),
            provider.GetRequiredService<IAcmeCertificateReadiness>(),
            provider.GetRequiredService<IBackFillerStartupJournal>(),
            provider.GetRequiredService<ILogger<CacheListenerService>>()));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, CacheListenerService>(static provider =>
                provider.GetRequiredService<CacheListenerService>()));
        builder.Services.AddSingleton<VectorNNTP.BackFiller.Core.ApplicationServiceManager>();
        builder.Services.AddSingleton<IHostedService, BackFillerApplicationHostedService>();
        builder.Services.AddSingleton<IHostedService>(static provider =>
            provider.GetRequiredService<ArticleRetentionSweepService>());
        builder.Services.TryAddSingleton(static provider =>
            new NntpArticleParser(provider.GetRequiredService<BackFillerRuntimeOptions>().Fqdn));
        builder.Services.TryAddSingleton<IArticleWorkHandler, ProviderArticleWorkHandler>();
        builder.Services.AddSingleton(static provider => new ArticleWorkResponsePublisher(
            provider.GetRequiredService<IRabbitMqService>(),
            provider.GetRequiredService<BackFillerRuntimeOptions>(),
            provider.GetRequiredService<ILogger<ArticleWorkResponsePublisher>>()));
        builder.Services.AddSingleton<IArticleWorkResponsePublisher>(static provider =>
            provider.GetRequiredService<ArticleWorkResponsePublisher>());
        builder.Services.AddSingleton<IHostedService>(static provider =>
            provider.GetRequiredService<ArticleWorkResponsePublisher>());
        builder.Services.AddSingleton(static provider => new ArticleWorkConsumerService(
            provider.GetRequiredService<IRabbitMqService>(),
            provider.GetRequiredService<BackFillerRuntimeOptions>(),
            provider.GetRequiredService<IArticleWorkHandler>(),
            provider.GetRequiredService<IArticleWorkResponsePublisher>(),
            provider.GetRequiredService<ILogger<ArticleWorkConsumerService>>(),
            provider.GetRequiredService<IBackFillerProviderCatalog>(),
            provider.GetRequiredService<IBackboneUsableCapacityProvider>()));
        builder.Services.AddSingleton<IArticleWorkConsumerReconciliation>(static provider =>
            provider.GetRequiredService<ArticleWorkConsumerService>());
        builder.Services.AddSingleton<IHostedService>(static provider =>
            provider.GetRequiredService<ArticleWorkConsumerService>());

        return builder;
    }

    /// <summary>
    /// Registers Windows Service and systemd lifetime support and keeps console formatting on Serilog.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    public static HostApplicationBuilder ConfigureBackFillerPlatformHosting(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Configuration.AddVectorEnvironmentVariables();

        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = ApplicationJsonConfiguration.EntryAssemblyName;
        });

        builder.Services.AddSystemd();
        RemoveObsoleteMicrosoftConsoleFormatterConfiguration(builder.Services);

        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });

        return builder;
    }

    /// <summary>
    /// Removes Microsoft console formatter options that <c>AddSystemd()</c> may register.
    /// </summary>
    /// <param name="services">The service collection to inspect.</param>
    public static void RemoveObsoleteMicrosoftConsoleFormatterConfiguration(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (IsMicrosoftConsoleLoggerOptionsConfiguration(services[i]))
            {
                services.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Returns whether a service descriptor configures Microsoft console logger options.
    /// </summary>
    /// <param name="descriptor">The descriptor to inspect.</param>
    /// <returns><see langword="true"/> when the descriptor configures <see cref="ConsoleLoggerOptions"/>.</returns>
    public static bool IsMicrosoftConsoleLoggerOptionsConfiguration(ServiceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var serviceType = descriptor.ServiceType;
        if (!serviceType.IsGenericType)
        {
            return false;
        }

        var definition = serviceType.GetGenericTypeDefinition();
        if (definition != typeof(IConfigureOptions<>)
            && definition != typeof(IPostConfigureOptions<>)
            && definition != typeof(IValidateOptions<>))
        {
            return false;
        }

        return serviceType.GenericTypeArguments[0] == typeof(ConsoleLoggerOptions);
    }
}
