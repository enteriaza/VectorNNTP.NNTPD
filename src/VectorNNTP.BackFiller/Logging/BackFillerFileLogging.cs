using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Configuration;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Formatting.Json;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Logging;

/// <summary>
/// Resolves <see cref="BackFillerFileLoggingTargetOptions.LogDir"/> and applies the BackFiller
/// Serilog sink graph in code (Native AOT / single-file safe).
/// </summary>
/// <remarks>
/// File, syslog, RabbitMQ, and optional console sinks are created here. The file directory is
/// <c>BackFiller:Logging:File:LogDir</c>. Level, retention, JSON formatting, and target
/// switches come from <see cref="BackFillerLoggingOptions"/>. A <c>Serilog</c> configuration
/// section is not read. RabbitMQ logging publishes through <see cref="RabbitMqLogEventSink"/>
/// on the existing <see cref="IRabbitMqService"/> connection.
/// </remarks>
internal static class BackFillerFileLogging
{
    /// <summary>Serilog rolling path token: <c>{entry assembly name}-yyyyMMdd.log</c>.</summary>
    internal const string RollingPathSuffix = "-.log";

    /// <summary>
    /// Text output template used when <see cref="BackFillerLoggingOptions.Json"/> is false.
    /// </summary>
    internal const string SinkOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>Restricted minimum level for sinks when the configured level is at least Debug.</summary>
    internal const LogEventLevel SinkMinimumLevel = LogEventLevel.Debug;

    /// <summary>Serilog.Sinks.Async buffer size.</summary>
    internal const int AsyncBufferSize = 50000;

    /// <summary>Serilog.Sinks.Async <c>blockWhenFull</c>.</summary>
    internal const bool AsyncBlockWhenFull = true;

    /// <summary>File sink buffering.</summary>
    internal const bool FileBuffered = true;

    /// <summary>
    /// Serilog File <c>flushToDiskInterval</c> of one second. The host logger is built here,
    /// not by <c>ReadFrom.Configuration</c>.
    /// </summary>
    internal static readonly TimeSpan FileFlushToDiskInterval = TimeSpan.FromSeconds(1);

    /// <summary>File sink size-based rolling.</summary>
    internal const bool RollOnFileSizeLimit = false;

    /// <summary>
    /// Resolves <paramref name="logDirectory"/> through Common
    /// <see cref="ApplicationLocalPath.ResolveApplicationLocalPath"/> against
    /// <paramref name="applicationBaseDirectory"/> (default
    /// <see cref="AppContext.BaseDirectory"/>). Absolute paths stay absolute.
    /// </summary>
    private static string ResolveDirectory(string logDirectory, string? applicationBaseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        return ApplicationLocalPath.ResolveApplicationLocalPath(
            logDirectory.Trim(),
            applicationBaseDirectory ?? AppContext.BaseDirectory);
    }

    /// <summary>
    /// Builds the Serilog rolling path <c>{logDirectory}/{applicationName}-.log</c>.
    /// </summary>
    private static string RollingFilePath(string logDirectory, string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        return Path.Combine(logDirectory, applicationName.Trim() + RollingPathSuffix);
    }

    /// <summary>
    /// Creates the log directory and returns the rolling File sink path for the current configuration.
    /// </summary>
    private static string EnsureRollingFilePath(
        IConfiguration configuration,
        string? applicationBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var logDir = ResolveDirectory(ReadRequiredFileLogDir(BindLogging(configuration)), applicationBaseDirectory);
        Directory.CreateDirectory(logDir);
        return RollingFilePath(logDir, ApplicationJsonConfiguration.EntryAssemblyName);
    }

    /// <summary>
    /// Applies the code-defined BackFiller sink graph.
    /// </summary>
    /// <param name="loggerConfiguration">The Serilog configuration to update.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="applicationBaseDirectory">
    /// Directory used to resolve a relative file-log path. Defaults to <see cref="AppContext.BaseDirectory"/>.
    /// </param>
    /// <param name="commandLine">
    /// Process switches that enable the console sink and ambient context enrichment.
    /// </param>
    /// <param name="services">
    /// Host services used to resolve <see cref="IRabbitMqService"/> when RabbitMQ logging is enabled.
    /// </param>
    /// <remarks>
    /// Does not call <c>ReadFrom.Configuration</c> and does not read a <c>Serilog</c> section.
    /// File, syslog, RabbitMQ, and console sinks are added only when their switches are on. Targets are not
    /// mutually exclusive. <paramref name="commandLine"/> enables the console sink and, when requested,
    /// ambient context enrichment. Those switches are not configuration settings.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Logging configuration is invalid.</exception>
    internal static void ConfigureLogger(
        LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        string? applicationBaseDirectory = null,
        BackFillerLoggingCommandLine commandLine = default,
        IServiceProvider? services = null)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(configuration);

        var logging = BindLogging(configuration);
        var minimumLevel = BackFillerLogLevelParser.ParseOrDefault(logging.LogLevel);
        var retainedFileCountLimit = ReadLogRetentionDays(logging);
        var rabbit = logging.RabbitMq ?? new BackFillerRabbitMqLoggingTargetOptions();
        if (rabbit.Enabled)
        {
            ValidateEnabledRabbitMq(rabbit);
        }

        var syslog = logging.Syslog ?? new BackFillerSyslogLoggingTargetOptions();
        if (syslog.Enabled)
        {
            ValidateEnabledSyslog(syslog);
        }

        var sinkLevel = minimumLevel < SinkMinimumLevel ? minimumLevel : SinkMinimumLevel;
        ITextFormatter? jsonFormatter = logging.Json ? new JsonFormatter(renderMessage: true) : null;
        var file = logging.File ?? new BackFillerFileLoggingTargetOptions();
        var fileEnabled = file.Enabled;

        loggerConfiguration
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("VectorNNTP.BackFiller", minimumLevel)
            .Enrich.WithProperty("Application", ApplicationJsonConfiguration.EntryAssemblyName);

        if (commandLine.EnrichFromLogContext)
        {
            loggerConfiguration.Enrich.FromLogContext();
        }

        if (commandLine.Console)
        {
            WriteConsole(loggerConfiguration, sinkLevel, jsonFormatter);
        }

        if (fileEnabled)
        {
            var path = EnsureRollingFilePath(configuration, applicationBaseDirectory);
            loggerConfiguration.WriteTo.Async(
                sink => WriteFile(sink, path, sinkLevel, retainedFileCountLimit, jsonFormatter),
                bufferSize: AsyncBufferSize,
                blockWhenFull: AsyncBlockWhenFull);
        }

        if (rabbit.Enabled)
        {
            WriteRabbitMq(loggerConfiguration, rabbit, sinkLevel, jsonFormatter, services);
        }

        if (syslog.Enabled)
        {
            WriteSyslog(loggerConfiguration, syslog, sinkLevel, jsonFormatter);
        }
    }

    /// <summary>Binds <c>BackFiller:Logging</c>. Missing keys keep the option defaults.</summary>
    private static BackFillerLoggingOptions BindLogging(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var logging = new BackFillerLoggingOptions();
        configuration.GetSection($"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}").Bind(logging);
        logging.File ??= new BackFillerFileLoggingTargetOptions();
        logging.RabbitMq ??= new BackFillerRabbitMqLoggingTargetOptions();
        logging.Syslog ??= new BackFillerSyslogLoggingTargetOptions();
        return logging;
    }

    /// <summary>
    /// Reads retention from a bound logging section, or the default when the object uses the default.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is outside the accepted range.</exception>
    private static int ReadLogRetentionDays(BackFillerLoggingOptions logging)
    {
        ArgumentNullException.ThrowIfNull(logging);
        var days = logging.LogRetentionDays;
        if (days < BackFillerLoggingOptions.MinimumLogRetentionDays
            || days > BackFillerLoggingOptions.MaximumLogRetentionDays)
        {
            throw new InvalidOperationException(
                $"BackFiller:Logging:LogRetentionDays '{days.ToString(CultureInfo.InvariantCulture)}' is not valid. Use an integer in the range {BackFillerLoggingOptions.MinimumLogRetentionDays}–{BackFillerLoggingOptions.MaximumLogRetentionDays}.");
        }

        return days;
    }

    private static string ReadRequiredFileLogDir(BackFillerLoggingOptions logging)
    {
        var file = logging.File ?? new BackFillerFileLoggingTargetOptions();
        if (!file.Enabled)
        {
            throw new InvalidOperationException("BackFiller file logging is disabled.");
        }

        if (string.IsNullOrWhiteSpace(file.LogDir))
        {
            throw new InvalidOperationException(
                "BackFiller:Logging:File:LogDir is required when file logging is enabled (old key: DirLogs).");
        }

        return file.LogDir.Trim();
    }

    private static void ValidateEnabledRabbitMq(BackFillerRabbitMqLoggingTargetOptions rabbit)
    {
        if (string.IsNullOrWhiteSpace(rabbit.Exchange))
        {
            throw new InvalidOperationException(
                "BackFiller:Logging:RabbitMQ:Exchange is required when RabbitMQ logging is enabled.");
        }

        if (string.IsNullOrWhiteSpace(rabbit.RoutingKey))
        {
            throw new InvalidOperationException(
                "BackFiller:Logging:RabbitMQ:RoutingKey is required when RabbitMQ logging is enabled.");
        }
    }

    private static void WriteRabbitMq(
        LoggerConfiguration loggerConfiguration,
        BackFillerRabbitMqLoggingTargetOptions rabbit,
        LogEventLevel sinkLevel,
        ITextFormatter? jsonFormatter,
        IServiceProvider? services)
    {
        var rabbitMq = services?.GetService<IRabbitMqService>()
            ?? throw new InvalidOperationException(
                "BackFiller:Logging:RabbitMQ is enabled but IRabbitMqService is not registered.");
        ITextFormatter formatter = jsonFormatter
            ?? new MessageTemplateTextFormatter(SinkOutputTemplate, CultureInfo.InvariantCulture);
        var sink = new RabbitMqLogEventSink(
            rabbitMq,
            rabbit.Exchange,
            rabbit.RoutingKey,
            ApplicationJsonConfiguration.EntryAssemblyName,
            formatter,
            jsonFormatter is null ? RabbitMqLogEventSink.TextContentType : RabbitMqLogEventSink.JsonContentType);
        loggerConfiguration.WriteTo.Async(
            writeTo => writeTo.Sink(sink, restrictedToMinimumLevel: sinkLevel),
            bufferSize: AsyncBufferSize,
            blockWhenFull: AsyncBlockWhenFull);
    }

    private static void ValidateEnabledSyslog(BackFillerSyslogLoggingTargetOptions syslog)
    {
        if (string.IsNullOrWhiteSpace(syslog.Host))
        {
            throw new InvalidOperationException(
                "BackFiller:Logging:Syslog:Host is required when syslog logging is enabled.");
        }

        if (syslog.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                "BackFiller:Logging:Syslog:Port must be an integer in the range 1–65535.");
        }

        if (!IsUdp(syslog.Protocol) && !IsTcp(syslog.Protocol))
        {
            throw new InvalidOperationException("BackFiller:Logging:Syslog:Protocol must be Udp or Tcp.");
        }
    }

    private static void WriteConsole(
        LoggerConfiguration loggerConfiguration,
        LogEventLevel sinkLevel,
        ITextFormatter? jsonFormatter)
    {
        if (jsonFormatter is null)
        {
            loggerConfiguration.WriteTo.Console(
                restrictedToMinimumLevel: sinkLevel,
                outputTemplate: SinkOutputTemplate);
            return;
        }

        loggerConfiguration.WriteTo.Console(jsonFormatter, restrictedToMinimumLevel: sinkLevel);
    }

    private static void WriteFile(
        LoggerSinkConfiguration sink,
        string path,
        LogEventLevel sinkLevel,
        int retainedFileCountLimit,
        ITextFormatter? jsonFormatter)
    {
        if (jsonFormatter is null)
        {
            sink.File(
                path,
                restrictedToMinimumLevel: sinkLevel,
                outputTemplate: SinkOutputTemplate,
                fileSizeLimitBytes: null,
                buffered: FileBuffered,
                flushToDiskInterval: FileFlushToDiskInterval,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: RollOnFileSizeLimit,
                retainedFileCountLimit: retainedFileCountLimit,
                hooks: BackFillerSerilogHooks.DailyGzipFastest);
            return;
        }

        sink.File(
            jsonFormatter,
            path,
            restrictedToMinimumLevel: sinkLevel,
            fileSizeLimitBytes: null,
            buffered: FileBuffered,
            flushToDiskInterval: FileFlushToDiskInterval,
            rollingInterval: RollingInterval.Day,
            rollOnFileSizeLimit: RollOnFileSizeLimit,
            retainedFileCountLimit: retainedFileCountLimit,
            hooks: BackFillerSerilogHooks.DailyGzipFastest);
    }

    private static void WriteSyslog(
        LoggerConfiguration loggerConfiguration,
        BackFillerSyslogLoggingTargetOptions syslog,
        LogEventLevel sinkLevel,
        ITextFormatter? jsonFormatter)
    {
        var host = syslog.Host.Trim();
        var appName = ApplicationJsonConfiguration.EntryAssemblyName;
        if (IsUdp(syslog.Protocol))
        {
            loggerConfiguration.WriteTo.UdpSyslog(
                host,
                syslog.Port,
                appName: appName,
                restrictedToMinimumLevel: sinkLevel,
                formatter: jsonFormatter);
            return;
        }

        loggerConfiguration.WriteTo.TcpSyslog(
            host,
            syslog.Port,
            appName: appName,
            useTls: false,
            restrictedToMinimumLevel: sinkLevel,
            formatter: jsonFormatter);
    }

    private static bool IsUdp(string? protocol) =>
        string.Equals(protocol?.Trim(), BackFillerSyslogLoggingTargetOptions.UdpProtocol, StringComparison.OrdinalIgnoreCase);

    private static bool IsTcp(string? protocol) =>
        string.Equals(protocol?.Trim(), BackFillerSyslogLoggingTargetOptions.TcpProtocol, StringComparison.OrdinalIgnoreCase);
}
