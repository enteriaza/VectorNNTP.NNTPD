using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.Acme;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Core;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Hosting;
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
    /// Registers <see cref="BackFillerRabbitMqService"/> as the sole RabbitMQ connection
    /// owner and as an <see cref="IHostedService"/>. Startup fails if the initial broker
    /// connection cannot be established. Registers <see cref="ProviderAccountConfigurationService"/>
    /// after the connection owner, then <see cref="NntpProviderRegistry"/>,
    /// then <see cref="BackFillerApplicationHostedService"/> (starts
    /// <see cref="CloudflareDnsReconciliationApplicationService"/>, then
    /// <see cref="AcmeCertificateApplicationService"/>, then
    /// <see cref="CacheListenerService"/>), then
    /// <see cref="ArticleRetentionSweepService"/>, then
    /// <see cref="ArticleWorkResponsePublisher"/>,
    /// then <see cref="ArticleWorkConsumerService"/>. Article Work consume
    /// is reconciled from usable NNTP capacity, not from the topology name list.
    /// </remarks>
    public static HostApplicationBuilder AddBackFillerHosting(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<ILocalIpAddressAssignee, NetworkInterfaceLocalIpAddressAssignee>();
        builder.Services.TryAddSingleton<IPhysicalMemoryProvider, OsPhysicalMemoryProvider>();
        builder.Services.TryAddSingleton(TimeProvider.System);

        builder.Services.TryAddSingleton<IBackFillerStartupJournal, BackFillerStartupJournal>();

        builder.Services
            .AddOptions<BackFillerOptions>()
            .BindConfiguration(BackFillerOptions.SectionName)
            .Configure<IConfiguration>(static (options, configuration) =>
            {
                var rabbit = configuration.GetSection("RabbitMQ").Get<BackFillerRabbitMqOptions>();
                if (rabbit is not null)
                {
                    options.RabbitMQ = rabbit;
                }
            })
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<BackFillerOptions>, BackFillerOptionsValidator>();

        builder.Services
            .AddOptions<AcmeCloudflareOptions>()
            .Configure<IOptions<BackFillerOptions>, IConfiguration>(
                static (acme, backfiller, configuration) =>
                {
                    BackFillerAcmeCloudflareOptionsAdapter.Apply(acme, backfiller.Value, configuration);
                })
            .ValidateDataAnnotations()
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
            return BackFillerRuntimeOptionsFactory.Create(
                options,
                nntpDb,
                acme,
                AppContext.BaseDirectory);
        });

        builder.Services.AddOptions<HostOptions>()
            .PostConfigure<BackFillerRuntimeOptions>(static (host, runtime) =>
            {
                host.ShutdownTimeout = runtime.Shutdown.GracePeriod;
            });

        builder.Services.TryAddSingleton<IBackFillerRabbitMqConnectionFactory, BackFillerRabbitMqClientConnectionFactory>();
        builder.Services.AddSingleton(static provider => new BackFillerRabbitMqService(
            provider.GetRequiredService<IBackFillerRabbitMqConnectionFactory>(),
            provider.GetRequiredService<BackFillerRuntimeOptions>(),
            provider.GetRequiredService<ILogger<BackFillerRabbitMqService>>()));
        builder.Services.AddSingleton<IBackFillerRabbitMqService>(static provider =>
            provider.GetRequiredService<BackFillerRabbitMqService>());
        builder.Services.AddSingleton<IHostedService>(static provider =>
            provider.GetRequiredService<BackFillerRabbitMqService>());

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
        builder.Services.AddSingleton<ApplicationServiceManager>();
        builder.Services.AddSingleton<IHostedService, BackFillerApplicationHostedService>();
        builder.Services.AddSingleton<IHostedService>(static provider =>
            provider.GetRequiredService<ArticleRetentionSweepService>());
        builder.Services.TryAddSingleton(static provider =>
            new NntpArticleParser(provider.GetRequiredService<BackFillerRuntimeOptions>().Fqdn));
        builder.Services.TryAddSingleton<IArticleWorkHandler, ProviderArticleWorkHandler>();
        builder.Services.AddSingleton(static provider => new ArticleWorkResponsePublisher(
            provider.GetRequiredService<IBackFillerRabbitMqService>(),
            provider.GetRequiredService<BackFillerRuntimeOptions>(),
            provider.GetRequiredService<ILogger<ArticleWorkResponsePublisher>>()));
        builder.Services.AddSingleton<IArticleWorkResponsePublisher>(static provider =>
            provider.GetRequiredService<ArticleWorkResponsePublisher>());
        builder.Services.AddSingleton<IHostedService>(static provider =>
            provider.GetRequiredService<ArticleWorkResponsePublisher>());
        builder.Services.AddSingleton(static provider => new ArticleWorkConsumerService(
            provider.GetRequiredService<IBackFillerRabbitMqService>(),
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
            options.ServiceName = "VectorNNTP.BackFiller";
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
