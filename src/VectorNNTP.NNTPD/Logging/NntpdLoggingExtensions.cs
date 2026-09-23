using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Events;

namespace VectorNNTP.NNTPD.Logging;

/// <summary>
/// Configures Serilog as the exclusive logging implementation for VectorNNTP.NNTPD.
/// </summary>
public static class NntpdLoggingExtensions
{
    /// <summary>
    /// Single-line console template suitable for interactive terminals and journald collection.
    /// </summary>
    public const string ConsoleOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Creates an early bootstrap logger used before the Generic Host is built.
    /// </summary>
    /// <returns>A bootstrap logger assigned to <see cref="Log.Logger"/>.</returns>
    public static Serilog.ILogger CreateBootstrapLogger()
    {
        return new LoggerConfiguration()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "VectorNNTP.NNTPD")
            .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
            .CreateBootstrapLogger();
    }

    /// <summary>
    /// Removes Microsoft default logging providers and registers Serilog as the sole provider.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configure">
    /// Optional additional Serilog configuration applied after reading <c>appsettings</c>.
    /// Used by tests to attach in-memory sinks without reintroducing MEL providers.
    /// </param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    /// <remarks>
    /// <para>
    /// Call order: clear MEL providers, remove default <see cref="ILoggerFactory"/> registrations,
    /// then <c>AddSerilog</c> with <c>writeToProviders: false</c> so framework and application
    /// <see cref="ILogger{T}"/> traffic flows only through Serilog sinks.
    /// </para>
    /// <para>
    /// Console formatting is owned by Serilog. journald collects Serilog console stdout under
    /// systemd. Microsoft console formatter options that <c>AddSystemd()</c> may register are
    /// stripped by <c>ConfigureNntpdPlatformHosting</c> and are not part of this logging pipeline.
    /// </para>
    /// </remarks>
    public static HostApplicationBuilder ConfigureNntpdLogging(
        this HostApplicationBuilder builder,
        Action<LoggerConfiguration>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.ClearProviders();

        // Host.CreateApplicationBuilder registers a default LoggerFactory; remove it so
        // SerilogLoggerFactory is the only ILoggerFactory in the container.
        builder.Services.RemoveAll<ILoggerFactory>();
        builder.Services.RemoveAll<ILoggerProvider>();

        builder.Services.AddSerilog(
            (services, loggerConfiguration) =>
            {
                loggerConfiguration
                    .ReadFrom.Configuration(builder.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty("Application", "VectorNNTP.NNTPD");

                // Ensure a console sink exists even if configuration omits WriteTo,
                // so interactive and systemd journal collection always have an output path.
                if (!builder.Configuration.GetSection("Serilog:WriteTo").GetChildren().Any())
                {
                    loggerConfiguration.WriteTo.Console(outputTemplate: ConsoleOutputTemplate);
                }

                configure?.Invoke(loggerConfiguration);
            },
            preserveStaticLogger: false,
            writeToProviders: false);

        return builder;
    }

    /// <summary>
    /// Returns registered <see cref="ILoggerProvider"/> implementations for diagnostics and tests.
    /// </summary>
    /// <remarks>
    /// When Serilog is configured via <c>AddSerilog</c>, logging is provided by replacing
    /// <see cref="ILoggerFactory"/> with <c>SerilogLoggerFactory</c>. In that mode the provider
    /// list is typically empty, which confirms Microsoft default providers were not retained.
    /// </remarks>
    public static IReadOnlyList<ILoggerProvider> GetLoggerProviders(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetServices<ILoggerProvider>().ToArray();
    }
}
