using Microsoft.Extensions.Configuration;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Logging;

/// <summary>
/// Resolves <see cref="NntpdOptions.LogDir"/> into the Serilog File path before
/// <c>ReadFrom.Configuration</c>. Operational File/Async settings live in <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// Serilog.Settings.Configuration cannot expand <c>Nntpd:LogDir</c> into <c>path</c>.
/// This type creates the directory and overwrites the File sink path so the JSON
/// placeholder is never the runtime directory. It does not add a second File sink.
/// </remarks>
internal static class NntpdFileLogging
{
    /// <summary>Serilog rolling path token: <c>{ApplicationName}-yyyyMMdd.log</c>.</summary>
    public const string RollingPathSuffix = "-.log";

    /// <summary>
    /// Suffix appended by <see cref="NntpdSerilogHooks.DailyGzipFastest"/> when the archive
    /// target directory is null: <c>{original-file-name}.gz</c>.
    /// </summary>
    public const string GzipArchiveSuffix = ".gz";

    /// <summary>Console-sink fallback minimum when <c>Serilog:WriteTo</c> is omitted.</summary>
    public const Serilog.Events.LogEventLevel ConsoleMinimumLevel = Serilog.Events.LogEventLevel.Information;

    /// <summary>
    /// Resolves <paramref name="logDir"/> the same way ACME resolves <see cref="NntpdOptions.AcmeStateDir"/>.
    /// </summary>
    public static string ResolveDirectory(string? logDir)
    {
        var configured = string.IsNullOrWhiteSpace(logDir)
            ? NntpdOptions.DefaultLogDir
            : logDir.Trim();
        return Path.GetFullPath(configured);
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
    /// Archive file name produced by <see cref="NntpdSerilogHooks.DailyGzipFastest"/>.
    /// </summary>
    public static string GzipArchiveFileName(string rolledLogPath) =>
        Path.GetFileName(rolledLogPath) + GzipArchiveSuffix;

    /// <summary>
    /// Creates <see cref="NntpdOptions.LogDir"/> and binds the resolved File path over the JSON placeholder.
    /// </summary>
    public static void BindResolvedFilePath(ConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var logDir = ResolveDirectory(configuration[$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.LogDir)}"]);
        Directory.CreateDirectory(logDir);

        var applicationName = configuration[$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.ApplicationName)}"];
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            applicationName = new NntpdOptions().ApplicationName;
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
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:path"] = "logs/VectorNNTP.NNTPD-.log",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:restrictedToMinimumLevel"] = "Verbose",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:outputTemplate"] =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:fileSizeLimitBytes"] = null,
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:buffered"] = "true",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:rollingInterval"] = "Day",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:rollOnFileSizeLimit"] = "false",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:retainedFileCountLimit"] = "1",
        [$"Serilog:WriteTo:{writeToIndex}:Args:configure:0:Args:hooks"] =
            "VectorNNTP.NNTPD.Logging.NntpdSerilogHooks::DailyGzipFastest, VectorNNTP.NNTPD",
    };
}
