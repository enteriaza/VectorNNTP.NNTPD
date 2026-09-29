using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.StorageServer.Configuration;

namespace VectorNNTP.StorageServer.Logging;

/// <summary>
/// Resolves <see cref="StorageServerOptions.LogDirectory"/> and applies the fixed
/// Serilog sink graph in code (Native AOT / single-file safe).
/// </summary>
internal static class StorageServerFileLogging
{
    /// <summary>Serilog rolling path token: <c>{ApplicationName}-yyyyMMdd.log</c>.</summary>
    public const string RollingPathSuffix = "-.log";

    /// <summary>Fixed application identity used in the rolling file name.</summary>
    public const string ApplicationName = "VectorNNTP.StorageServer";

    /// <summary>Console and File sink output template.</summary>
    public const string SinkOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>Restricted minimum level for Console and File sinks.</summary>
    public const LogEventLevel SinkMinimumLevel = LogEventLevel.Debug;

    /// <summary>Serilog.Sinks.Async buffer size.</summary>
    public const int AsyncBufferSize = 50000;

    /// <summary>Serilog.Sinks.Async <c>blockWhenFull</c>.</summary>
    public const bool AsyncBlockWhenFull = true;

    /// <summary>File sink uncompressed retention count.</summary>
    public const int RetainedFileCountLimit = 1;

    /// <summary>File sink buffering.</summary>
    public const bool FileBuffered = true;

    /// <summary>File sink size-based rolling.</summary>
    public const bool RollOnFileSizeLimit = false;

    /// <summary>
    /// Resolves <paramref name="logDirectory"/> through Common path resolution.
    /// </summary>
    public static string ResolveDirectory(string? logDirectory, string? applicationBaseDirectory = null)
    {
        var configured = string.IsNullOrWhiteSpace(logDirectory)
            ? StorageServerOptions.DefaultLogDirectory
            : logDirectory.Trim();
        return ApplicationLocalPath.ResolveApplicationLocalPath(
            configured,
            applicationBaseDirectory ?? AppContext.BaseDirectory);
    }

    /// <summary>
    /// Builds the Serilog rolling path <c>{logDirectory}/{applicationName}-.log</c>.
    /// </summary>
    public static string RollingFilePath(string logDirectory, string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        return Path.Combine(logDirectory, applicationName.Trim() + RollingPathSuffix);
    }

    /// <summary>
    /// Creates the log directory and returns the rolling File sink path for the current configuration.
    /// </summary>
    public static string EnsureRollingFilePath(
        IConfiguration configuration,
        string? applicationBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var logDir = ResolveDirectory(
            configuration[$"{StorageServerOptions.SectionName}:{nameof(StorageServerOptions.LogDirectory)}"],
            applicationBaseDirectory);
        Directory.CreateDirectory(logDir);
        return RollingFilePath(logDir, ApplicationName);
    }

    /// <summary>
    /// Applies production-equivalent minimum levels, enrichers, Console, and Async+File sinks.
    /// </summary>
    public static void ConfigureLogger(
        LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        string? applicationBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(configuration);

        var path = EnsureRollingFilePath(configuration, applicationBaseDirectory);

        loggerConfiguration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("VectorNNTP.StorageServer", LogEventLevel.Debug)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", ApplicationName)
            .WriteTo.Console(
                restrictedToMinimumLevel: SinkMinimumLevel,
                outputTemplate: SinkOutputTemplate)
            .WriteTo.Async(
                a => a.File(
                    path,
                    restrictedToMinimumLevel: SinkMinimumLevel,
                    outputTemplate: SinkOutputTemplate,
                    fileSizeLimitBytes: null,
                    buffered: FileBuffered,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: RollOnFileSizeLimit,
                    retainedFileCountLimit: RetainedFileCountLimit,
                    hooks: StorageServerSerilogHooks.DailyGzipFastest),
                bufferSize: AsyncBufferSize,
                blockWhenFull: AsyncBlockWhenFull);
    }
}
