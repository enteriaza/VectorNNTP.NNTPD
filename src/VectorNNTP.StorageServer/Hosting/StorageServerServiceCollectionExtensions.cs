using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Hosting;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.Common.Acme;
using VectorNNTP.Common.Cloudflare;
using VectorNNTP.Common.Core;
using VectorNNTP.Common.NntpDb;
using VectorNNTP.StorageServer.Acme;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Hosting.Systemd;
using VectorNNTP.StorageServer.Ingress;
using VectorNNTP.StorageServer.Listener;
using VectorNNTP.StorageServer.NntpDb;
using VectorNNTP.StorageServer.Storage;
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
    /// NntpDB → nntpsharedconfig → Cloudflare DNS → RabbitMQ (connectivity only) → ACME → VATP listener → AdvertisementPublisher.
    /// Shutdown is that order reversed, so advertisement stops before the listener.
    /// Topology, queues, and consumers are not registered here. NntpDB does not migrate schema.
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
            .PostConfigure(static options =>
            {
                options.Management ??= new RabbitMqManagementOptions();
                options.Username = null;
                options.Password = null;
            })
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<RabbitMqOptions>, RabbitMqOptionsValidator>();
        builder.Services.AddNntpDbOptions();

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
                acme.AcmeDirectoryUrl = string.Empty;
                acme.AcmeRenewalThresholdDays = 0;
                acme.CloudFlareZoneId = string.Empty;
                acme.DnsSuffix = string.Empty;
                acme.Fqdn = string.Empty;
                acme.AcmeEmail = string.Empty;
                acme.AcmeCertificatePassword = string.Empty;
                acme.CloudFlareApiKey = string.Empty;
            });
        builder.Services.AddSingleton<IValidateOptions<AcmeCloudflareOptions>>(static provider =>
            new AcmeCloudflareOptionsValidator(
                provider.GetRequiredService<ILocalIpAddressAssignee>(),
                validateSharedDatabaseFields: false,
                validateSharedCredentials: false));
        builder.Services.AddSingleton<IValidateOptions<AcmeCloudflareOptions>, TlsOnlyAcmeCloudflareOptionsValidator>();
        builder.Services.AddAcmeCloudflareInfrastructure();

        builder.Services.TryAddSingleton<StorageServerRuntimeOptionsSource>();
        builder.Services.AddSingleton(static provider =>
            provider.GetRequiredService<StorageServerRuntimeOptionsSource>().Current);

        builder.Services.AddOptions<HostOptions>()
            .PostConfigure<IOptions<StorageServerOptions>>(static (host, options) =>
            {
                host.ShutdownTimeout = options.Value.GracefulShutdownTimeout;
            });

        builder.Services.TryAddSingleton<IAcceptedArticleQueue, AcceptedArticleQueue>();
        builder.Services.TryAddSingleton<IStorageArticleOpenBoundary, RejectingStorageArticleOpenBoundary>();

        builder.Services.TryAddSingleton<IRabbitMqConnectionNameProvider>(static sp =>
            new DelegateRabbitMqConnectionNameProvider(() =>
                RabbitMqRuntimeOptions.GetDefaultConnectionName(
                    ApplicationJsonConfiguration.EntryAssemblyName,
                    sp.GetRequiredService<IOptions<StorageServerOptions>>().Value.Fqdn)));
        builder.Services.AddRabbitMqInfrastructure();

        builder.Services.TryAddSingleton<IStorageCapacityReader, NoSpoolCapacityReader>();
        builder.Services.TryAddSingleton<StorageServerAdvertisementPublisherService>();

        builder.Services.TryAddSingleton<INntpDbConnectionFactory, MySqlNntpDbConnectionFactory>();
        builder.Services.TryAddSingleton<NntpDbService>();
        builder.Services.TryAddSingleton<StorageServerSharedConfigurationService>();
        builder.Services.TryAddSingleton<INntpSharedConfigurationCatalogue>(static provider =>
            provider.GetRequiredService<StorageServerSharedConfigurationService>());

        // Startup order: NntpDB → nntpsharedconfig → Cloudflare → RabbitMQ → ACME → Listener → AdvertisementPublisher.
        // ApplicationServiceManager stops in reverse, so advertisement stops first.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, NntpDbService>(static provider =>
                provider.GetRequiredService<NntpDbService>()));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, StorageServerSharedConfigurationService>(static provider =>
                provider.GetRequiredService<StorageServerSharedConfigurationService>()));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, CloudflareDnsReconciliationApplicationService>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, RabbitMqService>(static sp =>
                sp.GetRequiredService<RabbitMqService>()));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, AcmeCertificateApplicationService>());
        builder.Services.AddSingleton(static provider =>
        {
            var listener = provider.GetRequiredService<IOptions<StorageServerOptions>>().Value.Listener;
            return new StorageVatpListenerService(
                provider.GetRequiredService<StorageServerRuntimeOptionsSource>(),
                provider.GetRequiredService<VectorNNTP.Common.Networking.Certificates.ITlsCertificateContextProvider>(),
                provider.GetRequiredService<IStorageArticleOpenBoundary>(),
                provider.GetRequiredService<IAcmeCertificateReadiness>(),
                provider.GetRequiredService<ILogger<StorageVatpListenerService>>(),
                provider.GetRequiredService<IAcceptedArticleQueue>(),
                new StoreAssemblyAdmission(listener.MaxActiveConnections));
        });
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, StorageVatpListenerService>(static provider =>
                provider.GetRequiredService<StorageVatpListenerService>()));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, StorageServerAdvertisementPublisherService>(static sp =>
                sp.GetRequiredService<StorageServerAdvertisementPublisherService>()));

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
