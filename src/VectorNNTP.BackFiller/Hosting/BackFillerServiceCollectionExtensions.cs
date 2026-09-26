using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.Hosting;

/// <summary>
/// Extension methods for registering VectorNNTP.BackFiller hosting services.
/// </summary>
public static class BackFillerServiceCollectionExtensions
{
    /// <summary>
    /// Adds Phase 1 BackFiller host services: configuration, validation, and system time.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    /// <remarks>
    /// No placeholder <see cref="IHostedService"/> is registered. The Generic Host stays
    /// alive via platform lifetime (console / systemd / Windows Service). Later phases add
    /// real hosted services. Article-work, RabbitMQ consume, NNTP, transit, retention,
    /// listener, accounts, and certificates are not registered in Phase 1.
    /// </remarks>
    public static HostApplicationBuilder AddBackFillerHosting(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<ILocalIpAddressAssignee, NetworkInterfaceLocalIpAddressAssignee>();
        builder.Services.TryAddSingleton<IPhysicalMemoryProvider, GcPhysicalMemoryProvider>();
        builder.Services.TryAddSingleton(TimeProvider.System);

        builder.Services
            .AddOptions<BackFillerOptions>()
            .BindConfiguration(BackFillerOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<BackFillerOptions>, BackFillerOptionsValidator>();

        builder.Services
            .AddOptions<BackFillerConnectionStringsOptions>()
            .BindConfiguration(BackFillerConnectionStringsOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<BackFillerConnectionStringsOptions>, BackFillerConnectionStringsOptionsValidator>();

        builder.Services.AddSingleton(static provider =>
        {
            var options = provider.GetRequiredService<IOptions<BackFillerOptions>>().Value;
            var connectionStrings = provider.GetRequiredService<IOptions<BackFillerConnectionStringsOptions>>().Value;
            return BackFillerRuntimeOptionsFactory.Create(options, connectionStrings);
        });

        builder.Services.AddOptions<HostOptions>()
            .PostConfigure<BackFillerRuntimeOptions>(static (host, runtime) =>
            {
                host.ShutdownTimeout = runtime.Shutdown.GracePeriod;
            });

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

        builder.Configuration.AddEnvironmentVariables(prefix: BackFillerOptions.EnvironmentVariablePrefix);

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
