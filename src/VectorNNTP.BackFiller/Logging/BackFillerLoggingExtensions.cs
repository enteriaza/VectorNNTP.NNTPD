using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Events;

namespace VectorNNTP.BackFiller.Logging;

/// <summary>
/// Configures Serilog as the exclusive logging implementation for VectorNNTP.BackFiller.
/// </summary>
public static partial class BackFillerLoggingExtensions
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
            .Enrich.WithProperty("Application", "VectorNNTP.BackFiller")
            .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
            .CreateBootstrapLogger();
    }

    /// <summary>
    /// Makes <see cref="Console.Out"/> flush every write.
    /// </summary>
    /// <remarks>
    /// When stdout is a pipe, <see cref="Console.Out"/> is block-buffered. Serilog's Console
    /// sink writes to <see cref="Console.Out"/> and does not flush. This must run before the
    /// first Serilog Console sink is created.
    /// </remarks>
    public static void UseAutoFlushConsoleOutput()
    {
        var writer = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding)
        {
            AutoFlush = true,
        };
        Console.SetOut(writer);
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
    public static HostApplicationBuilder ConfigureBackFillerLogging(
        this HostApplicationBuilder builder,
        Action<LoggerConfiguration>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.ClearProviders();
        builder.Services.RemoveAll<ILoggerFactory>();
        builder.Services.RemoveAll<ILoggerProvider>();

        builder.Services.AddSerilog(
            (services, loggerConfiguration) =>
            {
                loggerConfiguration
                    .ReadFrom.Configuration(builder.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty("Application", "VectorNNTP.BackFiller");

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
    /// Emits one Information event through the host <see cref="ILoggerFactory"/> after
    /// Serilog has replaced the bootstrap logger.
    /// </summary>
    /// <param name="services">The built host service provider.</param>
    /// <param name="environmentName">Host environment name (no secrets).</param>
    /// <param name="contentRootPath">Resolved content root used for <c>appsettings.json</c>.</param>
    public static void WriteLoggingInitialized(
        IServiceProvider services,
        string environmentName,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var factory = services.GetRequiredService<ILoggerFactory>();
        var logger = factory.CreateLogger("VectorNNTP.BackFiller.Hosting");
        LoggingInitialized(
            logger,
            "VectorNNTP.BackFiller",
            factory.GetType().Name,
            environmentName,
            contentRootPath);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Logging initialized for {Application}. Factory={LoggerFactoryType} Environment={EnvironmentName} ContentRoot={ContentRootPath}")]
    private static partial void LoggingInitialized(
        Microsoft.Extensions.Logging.ILogger logger,
        string application,
        string loggerFactoryType,
        string environmentName,
        string contentRootPath);
}
