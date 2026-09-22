using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Hosting.Systemd;
using VectorNNTP.NNTPD.Networking;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Listeners;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Session.Authentication;

namespace VectorNNTP.NNTPD.Hosting;

/// <summary>
/// Extension methods for registering VectorNNTP.NNTPD hosting and lifecycle services.
/// </summary>
public static class NntpdServiceCollectionExtensions
{
    /// <summary>
    /// Adds NNTPD lifecycle, options, and hosted service integration to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional additional options configuration.</param>
    /// <param name="includePlaceholderService">
    /// When <see langword="true"/>, registers a no-op placeholder <see cref="IApplicationService"/>.
    /// Defaults to <see langword="false"/> now that NNTP listeners are registered.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance.</returns>
    public static IServiceCollection AddNntpdHosting(
        this IServiceCollection services,
        Action<NntpdOptions>? configure = null,
        bool includePlaceholderService = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILocalIpAddressAssignee, NetworkInterfaceLocalIpAddressAssignee>();
        services.TryAddSingleton<IBindAddressResolver, BindAddressResolver>();
        services.TryAddSingleton<ITrustedProxyHosts, TrustedProxyHosts>();
        services.TryAddSingleton<ICloudflareDnsReconciler, CloudflareDnsReconciler>();
        services.TryAddSingleton<ITlsCertificateContextProvider, TlsCertificateContextProvider>();
        // Real account backends replace this registration; default rejects all credentials.
        services.TryAddSingleton<INntpAuthenticationProvider>(DenyAllNntpAuthenticationProvider.Instance);

        services.AddHttpClient(CloudflareDnsClient.HttpClientName, static client =>
        {
            client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
            // Stall protection is per-request via CloudflareDnsClient (CancelAfter of
            // min(PerRequestTimeout, remaining operation budget)). Disabling HttpClient.Timeout
            // avoids a second, uncoordinated timer that can outlive a short remaining budget.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.ExpectContinue = false;
        });

        services.AddHttpClient(CertesAcmeIssuer.HttpClientName, static client =>
        {
            // ACME directory / order HTTP. Stall protection is left to call cancellation;
            // avoid a hard HttpClient.Timeout that races with application shutdown budgets.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.ExpectContinue = false;
        });

        services.TryAddSingleton<ICloudflareDnsClient>(static sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>()
                .CreateClient(CloudflareDnsClient.HttpClientName);
            return new CloudflareDnsClient(
                httpClient,
                sp.GetRequiredService<IOptions<NntpdOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CloudflareDnsClient>>());
        });

        services.TryAddSingleton<AcmeComponentFactory>();
        services.TryAddSingleton<IServerCertificateProvider>(static sp =>
            sp.GetRequiredService<AcmeComponentFactory>().GetCertificateProvider());

        var optionsBuilder = services
            .AddOptions<NntpdOptions>()
            .BindConfiguration(NntpdOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart()
            .PostConfigure(static options =>
            {
                options.Systemd ??= new SystemdOptions();
                options.ArticleIngestion ??= new ArticleIngestionOptions();
                NormalizeBindAddresses(options);
                NormalizeProxyHosts(options);
            });

        services.AddSingleton<IValidateOptions<NntpdOptions>, NntpdOptionsValidator>();

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // Startup order (sequential ApplicationServiceManager):
        // Cloudflare DNS → incoming spool writer → plain NNTP listener → ACME → TLS NNTP listener.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, CloudflareDnsReconciliationService>());

        services.TryAddSingleton<IArticleIngestionQueue, ArticleIngestionQueue>();
        services.TryAddSingleton<IIncomingArticlePersister, IncomingSpoolFilePersister>();
        services.TryAddSingleton<IncomingSpoolWriterService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, IncomingSpoolWriterService>(static sp =>
                sp.GetRequiredService<IncomingSpoolWriterService>()));

        services.TryAddSingleton<NntpPlainListenerService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, NntpPlainListenerService>(static sp =>
                sp.GetRequiredService<NntpPlainListenerService>()));

        services.TryAddSingleton<AcmeCertificateService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, AcmeCertificateService>(static sp =>
                sp.GetRequiredService<AcmeCertificateService>()));

        services.TryAddSingleton<NntpTlsListenerService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApplicationService, NntpTlsListenerService>(static sp =>
                sp.GetRequiredService<NntpTlsListenerService>()));

        if (includePlaceholderService)
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IApplicationService, PlaceholderApplicationService>());
        }

        services.AddSingleton<ApplicationServiceManager>();
        services.AddSingleton<ApplicationLifecycle>();
        services.AddSingleton<NntpdHostLifetime>();
        services.AddSingleton<IApplicationHealth, ApplicationHealth>();

        services.TryAddSingleton<ISystemdNotifyBridge, SystemdNotifyBridge>();
        services.TryAddSingleton<ISystemdRuntime, SystemdRuntime>();

        // Register systemd lifecycle notifier before the main hosted service so it is
        // constructed and subscribed before application startup transitions occur.
        services.AddSingleton<SystemdLifecycleNotifier>();
        services.AddHostedService(sp => sp.GetRequiredService<SystemdLifecycleNotifier>());
        services.AddHostedService<SystemdWatchdogService>();
        services.AddHostedService<NntpdHostedService>();

        return services;
    }

    /// <summary>
    /// Configures platform hosting integrations for Windows Service and Linux systemd without
    /// preventing interactive console execution on other platforms.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    /// <remarks>
    /// <para>
    /// <c>AddSystemd()</c> is context-aware: it activates systemd lifetime and notify support only
    /// when the process is detected as a systemd service or <c>NOTIFY_SOCKET</c> is set.
    /// Interactive console and Windows Service execution remain supported.
    /// </para>
    /// <para>
    /// Console log formatting is owned exclusively by Serilog. Any Microsoft console formatter
    /// options registered by <c>AddSystemd()</c> are removed immediately afterward because they
    /// are unused under the Serilog-only logging pipeline.
    /// </para>
    /// <para>
    /// Signal handling (SIGTERM/SIGINT) is provided by the Generic Host /
    /// <c>SystemdLifetime</c>. This application does not register competing signal handlers.
    /// </para>
    /// </remarks>
    public static HostApplicationBuilder ConfigureNntpdPlatformHosting(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = builder.Configuration[$"{NntpdOptions.SectionName}:ApplicationName"]
                ?? "VectorNNTP.NNTPD";
        });

        builder.Services.AddSystemd();
        RemoveObsoleteMicrosoftConsoleFormatterConfiguration(builder.Services);

        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;

            var shutdown = builder.Configuration.GetSection(NntpdOptions.SectionName)
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
    /// <remarks>
    /// <c>Microsoft.Extensions.Hosting.Systemd</c> configures
    /// <see cref="ConsoleLoggerOptions.FormatterName"/> to the systemd MEL formatter when it
    /// detects a systemd service. That configuration is obsolete after Phase 0.2 because Serilog
    /// owns console output. Stripping these registrations prevents a misleading unused MEL
    /// formatter from remaining in DI while preserving systemd lifetime and notify registration.
    /// </remarks>
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

    private static void NormalizeBindAddresses(NntpdOptions options)
    {
        if (options.BindAddress is null || options.BindAddress.Length == 0)
        {
            // Code default when configuration omits BindAddress: listen on all interfaces.
            options.BindAddress = ["*"];
            return;
        }

        for (var i = 0; i < options.BindAddress.Length; i++)
        {
            options.BindAddress[i] = options.BindAddress[i]?.Trim() ?? string.Empty;
        }
    }

    private static void NormalizeProxyHosts(NntpdOptions options)
    {
        if (options.ProxyHosts is null)
        {
            options.ProxyHosts = [];
            return;
        }

        for (var i = 0; i < options.ProxyHosts.Length; i++)
        {
            options.ProxyHosts[i] = options.ProxyHosts[i]?.Trim() ?? string.Empty;
        }
    }
}

/// <summary>
/// Phase-0 placeholder application service so the host has a deterministic no-op service to manage.
/// Replaced by real NNTP services in later phases.
/// </summary>
internal sealed class PlaceholderApplicationService : IApplicationService
{
    public string Name => "Placeholder";

    public Task? Execution => null;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
