using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Events;

namespace VectorNNTP.StorageServer.Logging;

/// <summary>
/// Configures Serilog as the exclusive logging implementation for VectorNNTP.StorageServer.
/// </summary>
public static partial class StorageServerLoggingExtensions
{
    /// <summary>
    /// Single-line console template suitable for interactive terminals and journald collection.
    /// </summary>
    public const string ConsoleOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Creates an early bootstrap logger used before the Generic Host is built.
    /// </summary>
    public static Serilog.ILogger CreateBootstrapLogger()
    {
        return new LoggerConfiguration()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", StorageServerFileLogging.ApplicationName)
            .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
            .CreateBootstrapLogger();
    }

    /// <summary>
    /// Makes <see cref="Console.Out"/> flush every write.
    /// </summary>
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
    public static HostApplicationBuilder ConfigureStorageServerLogging(
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
                StorageServerFileLogging.ConfigureLogger(loggerConfiguration, builder.Configuration);
                loggerConfiguration.ReadFrom.Services(services);
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
    public static void WriteLoggingInitialized(
        IServiceProvider services,
        string environmentName,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var factory = services.GetRequiredService<ILoggerFactory>();
        var logger = factory.CreateLogger(StorageServerLogCategories.Hosting);
        LoggingInitialized(
            logger,
            StorageServerFileLogging.ApplicationName,
            factory.GetType().Name,
            StorageServerLogCategories.Hosting,
            environmentName,
            contentRootPath);
    }

    /// <summary>
    /// Returns registered <see cref="ILoggerProvider"/> implementations for diagnostics and tests.
    /// </summary>
    public static IReadOnlyList<ILoggerProvider> GetLoggerProviders(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetServices<ILoggerProvider>().ToArray();
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Application logging initialized Application={Application} Provider={Provider} Category={Category} Environment={Environment} ContentRoot={ContentRoot}")]
    private static partial void LoggingInitialized(
        Microsoft.Extensions.Logging.ILogger logger,
        string application,
        string provider,
        string category,
        string environment,
        string contentRoot);
}
