using System.Globalization;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Logging;

/// <summary>
/// Resolves <see cref="BackFillerOptions.LogDirectory"/> and applies the BackFiller
/// Serilog sink graph in code (Native AOT / single-file safe).
/// </summary>
/// <remarks>
/// Console, Async, and File settings are fixed in this type. The file path comes from
/// <see cref="BackFillerOptions.LogDirectory"/>. The minimum level comes from
/// <see cref="BackFillerOptions.LogLevel"/>. Daily retention comes from
/// <see cref="BackFillerOptions.LogRetentionDays"/>. A <c>Serilog</c> configuration
/// section is not read.
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
    /// Console and File sink output template matching production <c>VectorNNTP.BackFiller.json</c>.
    /// </summary>
    public const string SinkOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>Restricted minimum level for Console and File sinks when the configured level is at least Debug.</summary>
    public const LogEventLevel SinkMinimumLevel = LogEventLevel.Debug;

    /// <summary>Serilog.Sinks.Async buffer size.</summary>
    public const int AsyncBufferSize = 50000;

    /// <summary>Serilog.Sinks.Async <c>blockWhenFull</c>.</summary>
    public const bool AsyncBlockWhenFull = true;

    /// <summary>File sink buffering.</summary>
    public const bool FileBuffered = true;

    /// <summary>
    /// Serilog File <c>flushToDiskInterval</c> of one second. The host logger is built here,
    /// not by <c>ReadFrom.Configuration</c>.
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
    /// Applies the code-defined BackFiller sink graph.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Does not call <c>ReadFrom.Configuration</c> and does not read a <c>Serilog</c> section.
    /// <see cref="BackFillerOptions.LogDirectory"/>, <see cref="BackFillerOptions.LogLevel"/>, and
    /// <see cref="BackFillerOptions.LogRetentionDays"/> are read from the BackFiller section before
    /// sinks are created.
    /// </para>
    /// <para>
    /// Microsoft, Microsoft.Hosting.Lifetime, and System level overrides stay fixed in code.
    /// The configured level is the default minimum and the <c>VectorNNTP.BackFiller</c> minimum.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <c>BackFiller:LogLevel</c> or <c>BackFiller:LogRetentionDays</c> is present and invalid.
    /// </exception>
    public static void ConfigureLogger(
        LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        string? applicationBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(configuration);

        var minimumLevel = BackFillerLogLevelParser.ParseOrDefault(
            configuration[$"{BackFillerOptions.SectionName}:{nameof(BackFillerOptions.LogLevel)}"]);
        var retainedFileCountLimit = ReadLogRetentionDays(configuration);
        var path = EnsureRollingFilePath(configuration, applicationBaseDirectory);
        var sinkLevel = minimumLevel < SinkMinimumLevel ? minimumLevel : SinkMinimumLevel;

        loggerConfiguration
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("VectorNNTP.BackFiller", minimumLevel)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", ApplicationName)
            .WriteTo.Console(
                restrictedToMinimumLevel: sinkLevel,
                outputTemplate: SinkOutputTemplate)
            .WriteTo.Async(
                a => a.File(
                    path,
                    restrictedToMinimumLevel: sinkLevel,
                    outputTemplate: SinkOutputTemplate,
                    fileSizeLimitBytes: null,
                    buffered: FileBuffered,
                    flushToDiskInterval: FileFlushToDiskInterval,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: RollOnFileSizeLimit,
                    retainedFileCountLimit: retainedFileCountLimit,
                    hooks: BackFillerSerilogHooks.DailyGzipFastest),
                bufferSize: AsyncBufferSize,
                blockWhenFull: AsyncBlockWhenFull);
    }

    /// <summary>
    /// Reads <c>BackFiller:LogRetentionDays</c>, or the default when the key is absent.
    /// </summary>
    /// <param name="configuration">Application configuration already loaded for the host.</param>
    /// <returns>The File sink retention count.</returns>
    /// <exception cref="InvalidOperationException">The configured value is not an integer in range.</exception>
    public static int ReadLogRetentionDays(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var text = configuration[$"{BackFillerOptions.SectionName}:{nameof(BackFillerOptions.LogRetentionDays)}"];
        if (text is null)
        {
            return BackFillerOptions.DefaultLogRetentionDays;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            || days < BackFillerOptions.MinimumLogRetentionDays
            || days > BackFillerOptions.MaximumLogRetentionDays)
        {
            throw new InvalidOperationException(
                $"BackFiller:LogRetentionDays '{text}' is not valid. Use an integer in the range {BackFillerOptions.MinimumLogRetentionDays}–{BackFillerOptions.MaximumLogRetentionDays}.");
        }

        return days;
    }
}
