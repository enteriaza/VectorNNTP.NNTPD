using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Logging;

/// <summary>
/// Resolves <see cref="BackFillerOptions.LogDirectory"/> and applies the fixed BackFiller
/// Serilog sink graph in code (Native AOT / single-file safe).
/// </summary>
/// <remarks>
/// Operational Console / Async / File settings match production <c>appsettings.json</c>
/// <c>Serilog</c> section. The File path is never taken from JSON: it is always
/// <see cref="RollingFilePath"/> under the resolved log directory. Operator-configurable
/// location remains <see cref="BackFillerOptions.LogDirectory"/>.
/// </remarks>
internal static class BackFillerFileLogging
{
    /// <summary>Serilog rolling path token: <c>{ApplicationName}-yyyyMMdd.log</c>.</summary>
    public const string RollingPathSuffix = "-.log";

    /// <summary>
    /// Suffix appended by <see cref="BackFillerSerilogHooks.DailyGzipFastest"/> when the archive
    /// target directory is null: <c>{original-file-name}.gz</c>.
    /// </summary>
    public const string GzipArchiveSuffix = ".gz";

    /// <summary>Fixed application identity used in the rolling file name.</summary>
    public const string ApplicationName = "VectorNNTP.BackFiller";

    /// <summary>
    /// Console and File sink output template matching production <c>appsettings.json</c>.
    /// </summary>
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

    /// <summary>
    /// Serilog File <c>flushToDiskInterval</c>. Matches production <c>appsettings.json</c>
    /// (<c>00:00:01</c>). The host logger is built here, not by <c>ReadFrom.Configuration</c>.
    /// </summary>
    public static readonly TimeSpan FileFlushToDiskInterval = TimeSpan.FromSeconds(1);

    /// <summary>File sink size-based rolling.</summary>
    public const bool RollOnFileSizeLimit = false;

    /// <summary>
    /// Resolves <paramref name="logDirectory"/> through Common
    /// <see cref="ApplicationLocalPath.ResolveApplicationLocalPath"/> against
    /// <paramref name="applicationBaseDirectory"/> (default
    /// <see cref="AppContext.BaseDirectory"/>). Absolute paths stay absolute.
    /// </summary>
    public static string ResolveDirectory(string? logDirectory, string? applicationBaseDirectory = null)
    {
        var configured = string.IsNullOrWhiteSpace(logDirectory)
            ? BackFillerOptions.DefaultLogDirectory
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
    /// Archive file name produced by <see cref="BackFillerSerilogHooks.DailyGzipFastest"/>.
    /// </summary>
    public static string GzipArchiveFileName(string rolledLogPath) =>
        Path.GetFileName(rolledLogPath) + GzipArchiveSuffix;

    /// <summary>
    /// Creates the log directory and returns the rolling File sink path for the current configuration.
    /// </summary>
    public static string EnsureRollingFilePath(
        IConfiguration configuration,
        string? applicationBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var logDir = ResolveDirectory(
            configuration[$"{BackFillerOptions.SectionName}:{nameof(BackFillerOptions.LogDirectory)}"],
            applicationBaseDirectory);
        Directory.CreateDirectory(logDir);
        return RollingFilePath(logDir, ApplicationName);
    }

    /// <summary>
    /// Applies production-equivalent minimum levels, enrichers, Console, and Async+File sinks.
    /// </summary>
    /// <remarks>
    /// Does not use <c>Serilog.Settings.Configuration</c>, assembly scanning, or string-based hooks.
    /// </remarks>
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
            .MinimumLevel.Override("VectorNNTP.BackFiller", LogEventLevel.Debug)
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
                    flushToDiskInterval: FileFlushToDiskInterval,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: RollOnFileSizeLimit,
                    retainedFileCountLimit: RetainedFileCountLimit,
                    hooks: BackFillerSerilogHooks.DailyGzipFastest),
                bufferSize: AsyncBufferSize,
                blockWhenFull: AsyncBlockWhenFull);
    }
}
