using Microsoft.Extensions.Configuration;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.StorageServer.Configuration;

namespace VectorNNTP.StorageServer.Logging;

/// <summary>
/// Resolves <see cref="StorageServerOptions.LogDirectory"/> into the Serilog File path before
/// <c>ReadFrom.Configuration</c>. Operational File/Async settings live in <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// Serilog.Settings.Configuration cannot expand <c>StorageServer:LogDirectory</c> into <c>path</c>.
/// This type creates the directory and overwrites the File sink path so the JSON
/// placeholder is never the runtime directory. It does not add a second File sink.
/// </remarks>
internal static class StorageServerFileLogging
{
    /// <summary>Serilog rolling path token: <c>{ApplicationName}-yyyyMMdd.log</c>.</summary>
    public const string RollingPathSuffix = "-.log";

    /// <summary>
    /// Suffix appended by <see cref="StorageServerSerilogHooks.DailyGzipFastest"/> when the archive
    /// target directory is null: <c>{original-file-name}.gz</c>.
    /// </summary>
    public const string GzipArchiveSuffix = ".gz";

    /// <summary>Fixed application identity used in enrichment and the rolling file name.</summary>
    public const string ApplicationName = "VectorNNTP.StorageServer";

    /// <summary>Console-sink fallback minimum when <c>Serilog:WriteTo</c> is omitted.</summary>
    public const Serilog.Events.LogEventLevel ConsoleMinimumLevel = Serilog.Events.LogEventLevel.Debug;

    /// <summary>
    /// Resolves <paramref name="logDirectory"/> through Common
    /// <see cref="ApplicationLocalPath.ResolveApplicationLocalPath"/> against
    /// <paramref name="applicationBaseDirectory"/> (default
    /// <see cref="AppContext.BaseDirectory"/>). Absolute paths stay absolute.
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
    /// Archive file name produced by <see cref="StorageServerSerilogHooks.DailyGzipFastest"/>.
    /// </summary>
    public static string GzipArchiveFileName(string rolledLogPath) =>
        Path.GetFileName(rolledLogPath) + GzipArchiveSuffix;

    /// <summary>
    /// Creates <see cref="StorageServerOptions.LogDirectory"/> and binds the resolved File path over the JSON placeholder.
    /// </summary>
    public static void BindResolvedFilePath(
        ConfigurationManager configuration,
        string? applicationBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var logDir = ResolveDirectory(
            configuration[$"{StorageServerOptions.SectionName}:{nameof(StorageServerOptions.LogDirectory)}"],
            applicationBaseDirectory);
        Directory.CreateDirectory(logDir);

        var applicationName = configuration[$"{StorageServerOptions.SectionName}:{nameof(StorageServerOptions.ApplicationName)}"];
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            applicationName = ApplicationName;
        }

        var path = RollingFilePath(logDir, applicationName);
        var pathKey = FindConfiguredFilePathKey(configuration);
        if (pathKey is null)
        {
            return;
        }

        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [pathKey] = path });
    }

    private static string? FindConfiguredFilePathKey(IConfiguration configuration)
    {
        foreach (var writeTo in configuration.GetSection("Serilog:WriteTo").GetChildren())
        {
            if (!string.Equals(writeTo["Name"], "Async", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var inner in writeTo.GetSection("Args:configure").GetChildren())
            {
                if (string.Equals(inner["Name"], "File", StringComparison.OrdinalIgnoreCase))
                {
                    return $"{inner.Path}:Args:path";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// In-memory Serilog File/Async keys matching production <c>appsettings.json</c> (path is a placeholder).
    /// </summary>
    public static Dictionary<string, string?> AsyncFileWriteToKeys(int writeToIndex = 1) => new()
    {
        [$"Serilog:WriteTo:{writeToIndex}:Name"] = "Async",
        [$"Serilog:WriteTo:{writeToIndex}:Args:bufferSize"] = "50000",
        [$"Serilog:WriteTo:{writeToIndex}:Args:blockWhenFull"] = "true",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Name"] = "File",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:path"] = "logs/VectorNNTP.StorageServer-.log",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:restrictedToMinimumLevel"] = "Debug",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:outputTemplate"] =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:fileSizeLimitBytes"] = null,
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:buffered"] = "true",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:rollingInterval"] = "Day",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:rollOnFileSizeLimit"] = "false",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:retainedFileCountLimit"] = "14",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:hooks"] =
            "VectorNNTP.StorageServer.Logging.StorageServerSerilogHooks::DailyGzipFastest, VectorNNTP.StorageServer",
    };
}
