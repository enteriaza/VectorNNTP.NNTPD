using System.Reflection;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.File.Archive;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Logging;

/// <summary>
/// Dedicated Serilog pipeline for the INN <c>news</c> file.
/// </summary>
/// <remarks>
/// <para>
/// Operational File/Async settings bind from <c>Serilog:News</c> using the same
/// argument names as the application File sink (path, rolling, retention,
/// buffering, hooks). The INN line format is not read from configuration; the
/// sink is always constructed with <see cref="InnNewsTextFormatter"/>.
/// </para>
/// <para>
/// This small bridge exists because Serilog.Settings.Configuration cannot attach
/// a code-owned formatter without also exposing a user-editable
/// <c>outputTemplate</c> or <c>formatter</c> string. Path resolution still uses
/// <see cref="NntpdOptions.LogDir"/> via <see cref="NntpdFileLogging"/>.
/// </para>
/// </remarks>
public static class NntpdNewsLogging
{
    /// <summary>Serilog source context for news events. Not used by the application File/Console sinks.</summary>
    public const string SourceContext = "VectorNNTP.NNTPD.News";

    /// <summary>Configuration section that holds news File/Async operational arguments.</summary>
    public const string SectionName = "Serilog:News";

    /// <summary>Canonical rolling path token: <c>news-yyyyMMdd.log</c>.</summary>
    public const string RollingFileName = "news";

    /// <summary>Log event property for the INN disposition character.</summary>
    public const string DispositionProperty = "NewsDisposition";

    /// <summary>Log event property for the INN feed token.</summary>
    public const string FeedProperty = "NewsFeed";

    /// <summary>Log event property for the article Message-ID.</summary>
    public const string MessageIdProperty = "NewsMessageId";

    /// <summary>Log event property for optional future SITE tokens.</summary>
    public const string SitesProperty = "NewsSites";

    /// <summary>Log event property for the NNTP rejection response code.</summary>
    public const string ResponseCodeProperty = "NewsResponseCode";

    /// <summary>Log event property for the already-decided rejection reason.</summary>
    public const string ReasonProperty = "NewsReason";

    /// <summary>
    /// Overwrites <c>Serilog:News:path</c> from <see cref="NntpdOptions.LogDir"/>
    /// so the JSON placeholder is never the runtime path.
    /// </summary>
    public static void BindResolvedNewsPath(
        IConfiguration configuration,
        string? applicationBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration[SectionName + ":path"] is null)
        {
            return;
        }

        var logDir = NntpdFileLogging.ResolveDirectory(
            configuration[$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.LogDir)}"],
            applicationBaseDirectory);
        Directory.CreateDirectory(logDir);
        var path = NewsRollingFilePath(logDir);
        if (configuration is IConfigurationBuilder builder)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?> { [SectionName + ":path"] = path });
        }
    }

    /// <summary>Builds <c>{logDirectory}/news-.log</c> for Serilog daily rolling.</summary>
    public static string NewsRollingFilePath(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        return Path.Combine(logDirectory, RollingFileName + NntpdFileLogging.RollingPathSuffix);
    }

    /// <summary>
    /// Reads File/Async operational settings from <c>Serilog:News</c>.
    /// The INN formatter is never taken from configuration.
    /// </summary>
    public static NewsFileSinkSettings ReadSettings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var news = configuration.GetSection(SectionName);
        var path = news["path"];
        if (string.IsNullOrWhiteSpace(path))
        {
            path = NewsRollingFilePath(
                NntpdFileLogging.ResolveDirectory(
                    configuration[$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.LogDir)}"]));
        }

        return new NewsFileSinkSettings(
            path,
            ParseRolling(news["rollingInterval"]),
            news.GetValue<int?>("retainedFileCountLimit") ?? 1,
            news.GetValue("buffered", true),
            news.GetValue("rollOnFileSizeLimit", false),
            news.GetValue<long?>("fileSizeLimitBytes"),
            news.GetValue("bufferSize", 50000),
            news.GetValue("blockWhenFull", true),
            ResolveArchiveHooks(news["hooks"]));
    }

    /// <summary>
    /// Creates a dedicated Serilog logger that writes only INN <c>news</c> lines.
    /// </summary>
    public static Serilog.Core.Logger CreateLogger(IConfiguration configuration)
    {
        var settings = ReadSettings(configuration);
        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(settings.Path)) ?? NntpdOptions.DefaultLogDir);

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Async(
                a => a.File(
                    new InnNewsTextFormatter(),
                    settings.Path,
                    restrictedToMinimumLevel: LogEventLevel.Information,
                    fileSizeLimitBytes: settings.FileSizeLimitBytes,
                    buffered: settings.Buffered,
                    rollingInterval: settings.RollingInterval,
                    rollOnFileSizeLimit: settings.RollOnFileSizeLimit,
                    retainedFileCountLimit: settings.RetainedFileCountLimit,
                    hooks: settings.Hooks),
                bufferSize: settings.BufferSize,
                blockWhenFull: settings.BlockWhenFull)
            .CreateLogger();
    }

    /// <summary>Resolves File <c>hooks</c> from the same type/member string Serilog Settings uses.</summary>
    public static ArchiveHooks ResolveArchiveHooks(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return NntpdSerilogHooks.DailyGzipFastest;
        }

        var comma = configured.IndexOf(',');
        var typeMember = comma < 0 ? configured.Trim() : configured[..comma].Trim();
        var assembly = comma < 0 ? typeof(NntpdSerilogHooks).Assembly.GetName().Name : configured[(comma + 1)..].Trim();
        var separator = typeMember.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
        {
            return NntpdSerilogHooks.DailyGzipFastest;
        }

        var typeName = typeMember[..separator].Trim();
        var member = typeMember[(separator + 2)..].Trim();
        var type = Type.GetType($"{typeName}, {assembly}", throwOnError: false);
        var property = type?.GetProperty(member, BindingFlags.Public | BindingFlags.Static);
        return property?.GetValue(null) as ArchiveHooks ?? NntpdSerilogHooks.DailyGzipFastest;
    }

    private static RollingInterval ParseRolling(string? value) =>
        Enum.TryParse<RollingInterval>(value, ignoreCase: true, out var interval)
            ? interval
            : RollingInterval.Day;
}

/// <summary>
/// Operational File/Async settings for the dedicated news sink.
/// The INN line format is not represented here.
/// </summary>
/// <param name="Path">Resolved news file path, including Serilog rolling token.</param>
/// <param name="RollingInterval">Serilog File rolling interval.</param>
/// <param name="RetainedFileCountLimit">Serilog File uncompressed retention count.</param>
/// <param name="Buffered">Whether the File sink buffers writes.</param>
/// <param name="RollOnFileSizeLimit">Whether size-based rolling is enabled.</param>
/// <param name="FileSizeLimitBytes">Serilog File size cap; <see langword="null"/> is unlimited.</param>
/// <param name="BufferSize">Serilog.Sinks.Async buffer size.</param>
/// <param name="BlockWhenFull">Whether Async blocks when the buffer is full.</param>
/// <param name="Hooks">Resolved File archive hooks (compression).</param>
public readonly record struct NewsFileSinkSettings(
    string Path,
    RollingInterval RollingInterval,
    int RetainedFileCountLimit,
    bool Buffered,
    bool RollOnFileSizeLimit,
    long? FileSizeLimitBytes,
    int BufferSize,
    bool BlockWhenFull,
    ArchiveHooks Hooks);
