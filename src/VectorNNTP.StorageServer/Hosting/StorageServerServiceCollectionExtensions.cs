using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Hosting;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.Common.Acme;
using VectorNNTP.Common.Cloudflare;
using VectorNNTP.Common.Core;
using VectorNNTP.StorageServer.Acme;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Hosting.Systemd;
using VectorNNTP.StorageServer.Listener;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.StorageServer.Hosting;

/// <summary>
/// Extension methods for registering VectorNNTP.StorageServer hosting services.
/// </summary>
public static class StorageServerServiceCollectionExtensions
{
    /// <summary>
    /// Adds StorageServer host services: configuration, validation, ACME/Cloudflare, RabbitMQ connectivity, and lifecycle.
    /// </summary>
    /// <remarks>
    /// Application service start order:
    /// StorageEngine → StorageMaintenance → Cloudflare DNS → RabbitMQ (connectivity only) →
    /// ACME → VATP listener → AdvertisementPublisher → LookupConsumer.
    /// Shutdown is that order reversed, so presence and advertisement stop before the listener.
    /// Topology, queues, and consumers are not registered here.
    /// </remarks>
    public static HostApplicationBuilder AddStorageServerHosting(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<ILocalIpAddressAssignee, NetworkInterfaceLocalIpAddressAssignee>();
        builder.Services.TryAddSingleton(TimeProvider.System);

        builder.Services
            .AddOptions<StorageServerOptions>()
            .BindConfiguration(StorageServerOptions.SectionName)
            .PostConfigure(static options =>
            {
                if (options.BindAddress is null || options.BindAddress.Length == 0)
                {
                    options.BindAddress = ["*"];
                }
                else
                {
                    for (var i = 0; i < options.BindAddress.Length; i++)
                    {
                        options.BindAddress[i] = options.BindAddress[i]?.Trim() ?? string.Empty;
                    }
                }
            })
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<StorageServerOptions>, StorageServerOptionsValidator>();

        builder.Services
            .AddOptions<RabbitMqOptions>()
            .BindConfiguration(RabbitMqOptions.SectionName)
            .PostConfigure(static options => options.Management ??= new RabbitMqManagementOptions())
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<RabbitMqOptions>, RabbitMqOptionsValidator>();

        builder.Services
            .AddOptions<AcmeCloudflareOptions>()
            .Configure<IOptions<StorageServerOptions>, IConfiguration>(
                static (acme, storage, configuration) =>
                {
                    StorageServerAcmeCloudflareOptionsAdapter.Apply(acme, storage.Value, configuration);
                })
            .ValidateOnStart()
            .PostConfigure(static acme =>
            {
                AcmeCloudflareOptionsValidator.NormalizeBindAddresses(acme);
                acme.AcmeStateDir = ApplicationLocalPath.ResolveApplicationLocalPath(
                    acme.AcmeStateDir,
                    AppContext.BaseDirectory);
            });
        builder.Services.AddSingleton<IValidateOptions<AcmeCloudflareOptions>, AcmeCloudflareOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<AcmeCloudflareOptions>, TlsOnlyAcmeCloudflareOptionsValidator>();
        builder.Services.AddAcmeCloudflareInfrastructure();

        builder.Services.AddSingleton(static provider =>
        {
            var options = provider.GetRequiredService<IOptions<StorageServerOptions>>().Value;
            var acme = provider.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
            return StorageServerRuntimeOptionsFactory.Create(options, acme, AppContext.BaseDirectory);
        });

        builder.Services.AddOptions<HostOptions>()
            .PostConfigure<StorageServerRuntimeOptions>(static (host, runtime) =>
            {
                host.ShutdownTimeout = runtime.GracefulShutdownTimeout;
            });

        builder.Services.TryAddSingleton<IStorageArticleOpenBoundary, StorageArticleOpenBoundary>();

        builder.Services.TryAddSingleton<IRabbitMqConnectionNameProvider>(static sp =>
            new DelegateRabbitMqConnectionNameProvider(() =>
                RabbitMqRuntimeOptions.GetDefaultConnectionName(
                    ApplicationJsonConfiguration.EntryAssemblyName,
                    sp.GetRequiredService<IOptions<StorageServerOptions>>().Value.Fqdn)));
        builder.Services.AddRabbitMqInfrastructure();

        builder.Services.TryAddSingleton<IStorageCapacityReader>(static sp =>
            new CacheDirectoryCapacityReader(sp.GetRequiredService<StorageServerRuntimeOptions>().CacheDir));
        builder.Services.TryAddSingleton<StorageServerAdvertisementPublisherService>();
        builder.Services.TryAddSingleton<IStorageArticlePresence, DurableIndexArticlePresence>();
        builder.Services.TryAddSingleton<StorageArticleLookupConsumerService>();

        // Owns Open+Recover+Dispose. Resolve via this singleton only (no second engine factory).
        builder.Services.AddSingleton<StorageEngineApplicationService>();
        builder.Services.AddSingleton(static sp =>
            sp.GetRequiredService<StorageEngineApplicationService>().Engine);

        builder.Services.AddSingleton(static sp =>
        {
            var compaction = sp.GetRequiredService<IOptions<StorageServerOptions>>().Value.Storage.Compaction
                ?? new ArticleCompactionPolicyOptions();
            return new ArticleSegmentPolicy(compaction);
        });
        // Lazy: first resolve after StorageEngine StartAsync (registration order).
        builder.Services.AddSingleton(static sp => new Lazy<StorageMaintenanceCoordinator>(() =>
        {
            var storage = sp.GetRequiredService<IOptions<StorageServerOptions>>().Value.Storage;
            return new StorageMaintenanceCoordinator(
                sp.GetRequiredService<StorageEngineApplicationService>().Engine,
                sp.GetRequiredService<ArticleSegmentPolicy>(),
                storage?.JournalCheckpointThresholdBytes ?? 0,
                sp.GetRequiredService<ILogger<StorageMaintenanceCoordinator>>(),
                storage?.IndexCheckpointThresholdBytes ?? 0,
                storage?.MaxRetentionAge ?? TimeSpan.Zero);
        }));
        builder.Services.AddSingleton<StorageMaintenanceService>();

        // Startup order: StorageEngine → StorageMaintenance → Cloudflare → RabbitMQ →
        // ACME → Listener → AdvertisementPublisher → LookupConsumer.
        // ApplicationServiceManager stops in reverse, so presence and advertisement stop first.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, StorageEngineApplicationService>(static sp =>
                sp.GetRequiredService<StorageEngineApplicationService>()));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, StorageMaintenanceService>(static sp =>
                sp.GetRequiredService<StorageMaintenanceService>()));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, CloudflareDnsReconciliationApplicationService>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, RabbitMqService>(static sp =>
                sp.GetRequiredService<RabbitMqService>()));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, AcmeCertificateApplicationService>());
        builder.Services.AddSingleton(static provider =>
        {
            var runtime = provider.GetRequiredService<StorageServerRuntimeOptions>();
            var slots = runtime.Storage.JournalHardLimitBytes / ArticleResourceLimits.MaxArticleBytes;
            if (slots < 1)
            {
                slots = 1;
            }

            if (slots > int.MaxValue)
            {
                slots = int.MaxValue;
            }

            return new StorageVatpListenerService(
                runtime,
                provider.GetRequiredService<VectorNNTP.Common.Networking.Certificates.ITlsCertificateContextProvider>(),
                provider.GetRequiredService<IStorageArticleOpenBoundary>(),
                provider.GetRequiredService<IAcmeCertificateReadiness>(),
                provider.GetRequiredService<ILogger<StorageVatpListenerService>>(),
                provider.GetRequiredService<StorageEngineApplicationService>(),
                new StoreAssemblyAdmission((int)slots));
        });
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, StorageVatpListenerService>(static provider =>
                provider.GetRequiredService<StorageVatpListenerService>()));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, StorageServerAdvertisementPublisherService>(static sp =>
                sp.GetRequiredService<StorageServerAdvertisementPublisherService>()));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, StorageArticleLookupConsumerService>(static sp =>
                sp.GetRequiredService<StorageArticleLookupConsumerService>()));

        builder.Services.AddSingleton<IApplicationLifecycleOptions>(static sp =>
            sp.GetRequiredService<IOptions<StorageServerOptions>>().Value);
        builder.Services.AddSingleton<ApplicationServiceManager>();
        builder.Services.AddSingleton<ApplicationLifecycle>(static sp => new ApplicationLifecycle(
            sp.GetRequiredService<ApplicationServiceManager>(),
            sp.GetRequiredService<IApplicationLifecycleOptions>(),
            sp.GetRequiredService<ILogger<ApplicationLifecycle>>()));
        builder.Services.AddSingleton<StorageServerHostShutdown>();
        builder.Services.AddSingleton<IApplicationHealth, ApplicationHealth>();

        builder.Services.TryAddSingleton<ISystemdNotifyBridge, SystemdNotifyBridge>();
        builder.Services.TryAddSingleton<ISystemdRuntime, SystemdRuntime>();

        builder.Services.AddSingleton<SystemdLifecycleNotifier>();
        builder.Services.AddHostedService(static provider =>
            provider.GetRequiredService<SystemdLifecycleNotifier>());
        builder.Services.AddHostedService<SystemdWatchdogService>();
        builder.Services.AddHostedService<StorageServerHostedService>();

        return builder;
    }

    /// <summary>
    /// Registers Windows Service and systemd lifetime support and keeps console formatting on Serilog.
    /// </summary>
    public static HostApplicationBuilder ConfigureStorageServerPlatformHosting(this HostApplicationBuilder builder)
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

            var shutdown = builder.Configuration.GetSection(StorageServerOptions.SectionName)
                .GetValue<TimeSpan?>("GracefulShutdownTimeout");
            if (shutdown is { } timeout)
            {
                options.ShutdownTimeout = timeout;
            }
        });

        return builder;
    }

    /// <summary>
    /// Removes Microsoft console formatter options that <c>AddSystemd()</c> may register.
    /// </summary>
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
