using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace VectorNNTP.BackFiller.Hosting;

/// <summary>
/// Extension methods for registering VectorNNTP.BackFiller hosting services.
/// </summary>
public static class BackFillerServiceCollectionExtensions
{
    /// <summary>
    /// Adds Phase 0 BackFiller host services: system time and the placeholder hosted service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> instance.</returns>
    /// <remarks>
    /// Article-work, RabbitMQ, NNTP, transit, retention, listener, accounts, and certificates
    /// are not registered in Phase 0.
    /// </remarks>
    public static IServiceCollection AddBackFillerHosting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<BackFillerHostedService>();
        return services;
    }

    /// <summary>
    /// Registers Windows Service and systemd lifetime support and keeps console formatting on Serilog.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    /// <remarks>
    /// <c>AddSystemd()</c> activates notify support only when the process is a systemd service
    /// or <c>NOTIFY_SOCKET</c> is set. Microsoft console formatter options registered by
    /// <c>AddSystemd()</c> are removed because Serilog owns console output.
    /// </remarks>
    public static HostApplicationBuilder ConfigureBackFillerPlatformHosting(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

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
