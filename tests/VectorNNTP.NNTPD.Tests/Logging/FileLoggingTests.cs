using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Hosting;
using VectorNNTP.NNTPD.Logging;
using VectorNNTP.NNTPD.Session.Commands;
using VectorNNTP.NNTPD.Session.CommandProcessor;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.NNTPD.Tests.Logging;

[Collection(SerilogCollection.Name)]
public sealed class FileLoggingTests
{
    [Fact]
    public void ProductionAppsettings_DeclaresAsyncFileSinkContract()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindProductionAppsettings()));
        var async = doc.RootElement.GetProperty("Serilog").GetProperty("WriteTo")[1];
        Assert.Equal("Async", async.GetProperty("Name").GetString());
        Assert.Equal(50000, async.GetProperty("Args").GetProperty("bufferSize").GetInt32());
        Assert.True(async.GetProperty("Args").GetProperty("blockWhenFull").GetBoolean());
        var file = async.GetProperty("Args").GetProperty("configure")[0];
        Assert.Equal("File", file.GetProperty("Name").GetString());
        var args = file.GetProperty("Args");
        Assert.Equal("Debug", args.GetProperty("restrictedToMinimumLevel").GetString());
        Assert.Equal("Day", args.GetProperty("rollingInterval").GetString());
        // Application File keeps 14 uncompressed days. Path-survey (inpaths) uses 1 so
        // Serilog's delete callback can hand off completed files at daily rotation.
        Assert.Equal(14, args.GetProperty("retainedFileCountLimit").GetInt32());
        Assert.True(args.GetProperty("buffered").GetBoolean());
        Assert.Equal("00:00:01", args.GetProperty("flushToDiskInterval").GetString());
        Assert.False(args.GetProperty("rollOnFileSizeLimit").GetBoolean());
        Assert.Equal(JsonValueKind.Null, args.GetProperty("fileSizeLimitBytes").ValueKind);
        Assert.Equal(
            "VectorNNTP.NNTPD.Logging.NntpdSerilogHooks::DailyGzipFastest, VectorNNTP.NNTPD",
            args.GetProperty("hooks").GetString());
        Assert.Equal(
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
            args.GetProperty("outputTemplate").GetString());
        Assert.Equal("logs/pending-.log", args.GetProperty("path").GetString());
        var news = doc.RootElement.GetProperty("Serilog").GetProperty("News");
        Assert.True(news.GetProperty("buffered").GetBoolean());
        Assert.Equal("00:00:01", news.GetProperty("flushToDiskInterval").GetString());
        var inpaths = doc.RootElement.GetProperty("Serilog").GetProperty("Inpaths");
        Assert.True(inpaths.GetProperty("buffered").GetBoolean());
        Assert.Equal("00:00:01", inpaths.GetProperty("flushToDiskInterval").GetString());
        var usingNames = doc.RootElement.GetProperty("Serilog").GetProperty("Using")
            .EnumerateArray().Select(static e => e.GetString()).ToArray();
        Assert.Contains("Serilog.Sinks.File", usingNames);
        Assert.Contains("Serilog.Sinks.Async", usingNames);
        Assert.Contains("Serilog.Sinks.File.Archive", usingNames);
        Assert.Equal("-.log", NntpdFileLogging.RollingPathSuffix);
        Assert.Equal(".gz", NntpdFileLogging.GzipArchiveSuffix);
    }

    [Fact]
    public void BindResolvedFilePath_OverwritesProductionJsonPlaceholderFromLogDir()
    {
        var logDir = CreateTempLogDir();
        try
        {
            var configuration = new ConfigurationManager();
            configuration.AddJsonFile(FindProductionAppsettings(), optional: false, reloadOnChange: false);
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.LogDir)}"] = logDir,
                    [$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.ApplicationName)}"] = "VectorNNTP.NNTPD",
                });

            NntpdFileLogging.BindResolvedFilePath(configuration);

            var expected = NntpdFileLogging.RollingFilePath(
                logDir,
                ApplicationJsonConfiguration.EntryAssemblyName);
            Assert.Equal(expected, configuration["Serilog:WriteTo:1:Args:configure:0:Args:path"]);
            Assert.DoesNotContain("VectorNNTP.NNTPD-", configuration["Serilog:WriteTo:1:Args:configure:0:Args:path"], StringComparison.Ordinal);
            Assert.Equal(NntpdNewsLogging.NewsRollingFilePath(logDir), configuration["Serilog:News:path"]);
            Assert.Equal(NntpdPathSurveyLogging.PathSurveyRollingFilePath(logDir), configuration["Serilog:Inpaths:path"]);
            Assert.True(Directory.Exists(logDir));
        }
        finally
        {
            TryDelete(logDir);
        }
    }

    [Fact]
    public void ProductionJson_BindsUnlimitedDailyAsyncGzipFileSink()
    {
        var logDir = CreateTempLogDir();
        try
        {
            var configuration = new ConfigurationManager();
            configuration.AddJsonFile(FindProductionAppsettings(), optional: false, reloadOnChange: false);
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.LogDir)}"] = logDir,
                    [$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.ApplicationName)}"] = "VectorNNTP.NNTPD",
                });
            NntpdFileLogging.BindResolvedFilePath(configuration);

            using var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

            var sinks = WalkLogEventSinks(logger).ToArray();
            var fileSink = Assert.Single(
                sinks,
                static n => n.GetType().Name.Equals("RollingFileSink", StringComparison.Ordinal));
            Assert.Null(ReadInstanceField(fileSink, "_fileSizeLimitBytes"));
            Assert.False(Assert.IsType<bool>(ReadInstanceField(fileSink, "_rollOnFileSizeLimit")!));
            // Uncompressed application File retention (14 daily files), not inpaths (1) or gzip archives.
            Assert.Equal(14, ReadInstanceField(fileSink, "_retainedFileCountLimit"));
            Assert.True(Assert.IsType<bool>(ReadInstanceField(fileSink, "_buffered")!));
            Assert.Same(
                NntpdSerilogHooks.DailyGzipFastest,
                ReadInstanceField(fileSink, "_hooks"));

            var asyncSink = Assert.Single(
                sinks,
                static n => n.GetType().Name.Equals("BackgroundWorkerSink", StringComparison.Ordinal));
            Assert.True(Assert.IsType<bool>(ReadInstanceField(asyncSink, "_blockWhenFull")!));
            var queue = ReadInstanceField(asyncSink, "_queue")
                        ?? throw new InvalidOperationException("BackgroundWorkerSink._queue was not found.");
            var boundedCapacity = queue.GetType().GetProperty("BoundedCapacity")?.GetValue(queue)
                                  ?? throw new InvalidOperationException("BoundedCapacity was not found.");
            Assert.Equal(50000, Convert.ToInt32(boundedCapacity, CultureInfo.InvariantCulture));
            AssertFlushesOncePerSecond(sinks);
        }
        finally
        {
            Log.CloseAndFlush();
            TryDelete(logDir);
        }
    }

    [Fact]
    public void DirectRegistration_MatchesProductionAsyncGzipFileSink()
    {
        var logDir = CreateTempLogDir();
        try
        {
            var configuration = new ConfigurationManager();
            configuration.AddJsonFile(FindProductionAppsettings(), optional: false, reloadOnChange: false);
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.LogDir)}"] = logDir,
                    [$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.ApplicationName)}"] = "VectorNNTP.NNTPD",
                });
            NntpdFileLogging.BindResolvedFilePath(configuration);

            var levels = NntpdConfiguredSinks.WithoutWriteTo(configuration);
            Assert.Empty(levels.GetSection("Serilog:WriteTo").GetChildren());
            Assert.Equal("Information", levels["Serilog:MinimumLevel:Default"]);
            Assert.Equal("Debug", levels["Serilog:MinimumLevel:Override:VectorNNTP.NNTPD"]);

            var loggerConfiguration = new LoggerConfiguration()
                .ReadFrom.Configuration(levels);
            NntpdConfiguredSinks.Apply(loggerConfiguration, configuration);
            using var logger = loggerConfiguration.CreateLogger();

            var sinks = WalkLogEventSinks(logger).ToArray();
            var fileSink = Assert.Single(
                sinks,
                static n => n.GetType().Name.Equals("RollingFileSink", StringComparison.Ordinal));
            Assert.Null(ReadInstanceField(fileSink, "_fileSizeLimitBytes"));
            Assert.False(Assert.IsType<bool>(ReadInstanceField(fileSink, "_rollOnFileSizeLimit")!));
            Assert.Equal(14, ReadInstanceField(fileSink, "_retainedFileCountLimit"));
            Assert.True(Assert.IsType<bool>(ReadInstanceField(fileSink, "_buffered")!));
            Assert.Same(NntpdSerilogHooks.DailyGzipFastest, ReadInstanceField(fileSink, "_hooks"));

            var asyncSink = Assert.Single(
                sinks,
                static n => n.GetType().Name.Equals("BackgroundWorkerSink", StringComparison.Ordinal));
            Assert.True(Assert.IsType<bool>(ReadInstanceField(asyncSink, "_blockWhenFull")!));
            var queue = ReadInstanceField(asyncSink, "_queue")
                        ?? throw new InvalidOperationException("BackgroundWorkerSink._queue was not found.");
            var boundedCapacity = queue.GetType().GetProperty("BoundedCapacity")?.GetValue(queue)
                                  ?? throw new InvalidOperationException("BoundedCapacity was not found.");
            Assert.Equal(50000, Convert.ToInt32(boundedCapacity, CultureInfo.InvariantCulture));
            AssertFlushesOncePerSecond(sinks);
            Assert.Single(sinks, static n => n.GetType().Name.Equals("ConsoleSink", StringComparison.Ordinal));
        }
        finally
        {
            Log.CloseAndFlush();
            TryDelete(logDir);
        }
    }

    [Fact]
    public void DirectRegistration_OmitsFileWhenOnlyConsoleIsConfigured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:WriteTo:0:Name"] = "Console",
                ["Serilog:WriteTo:0:Args:restrictedToMinimumLevel"] = "Warning",
                ["Serilog:WriteTo:0:Args:outputTemplate"] = NntpdLoggingExtensions.ConsoleOutputTemplate,
            })
            .Build();

        var loggerConfiguration = new LoggerConfiguration();
        NntpdConfiguredSinks.Apply(loggerConfiguration, configuration);
        using var logger = loggerConfiguration.CreateLogger();
        var sinks = WalkLogEventSinks(logger).ToArray();
        Assert.Single(sinks, static n => n.GetType().Name.Equals("ConsoleSink", StringComparison.Ordinal));
        Assert.DoesNotContain(sinks, static n => n.GetType().Name.Equals("RollingFileSink", StringComparison.Ordinal));
        Assert.DoesNotContain(sinks, static n => n.GetType().Name.Equals("BackgroundWorkerSink", StringComparison.Ordinal));
    }

    [Fact]
    public void DirectRegistration_LeavesUnsupportedFileArgumentsOnConfigurationBinding()
    {
        var logDir = CreateTempLogDir();
        try
        {
            var path = NntpdFileLogging.RollingFilePath(logDir, "VectorNNTP.NNTPD");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Serilog:Using:0"] = "Serilog.Sinks.File",
                    ["Serilog:WriteTo:0:Name"] = "File",
                    ["Serilog:WriteTo:0:Args:path"] = path,
                    ["Serilog:WriteTo:0:Args:shared"] = "true",
                    ["Serilog:WriteTo:0:Args:rollingInterval"] = "Day",
                })
                .Build();

            var loggerConfiguration = new LoggerConfiguration();
            NntpdConfiguredSinks.Apply(loggerConfiguration, configuration);
            using var logger = loggerConfiguration.CreateLogger();
            var fileSink = Assert.Single(
                WalkLogEventSinks(logger),
                static n => n.GetType().Name.Equals("RollingFileSink", StringComparison.Ordinal));
            Assert.True(Assert.IsType<bool>(ReadInstanceField(fileSink, "_shared")!));
        }
        finally
        {
            Log.CloseAndFlush();
            TryDelete(logDir);
        }
    }

    [Fact]
    public void ProductionNewsAndInpaths_FlushCurrentFileOncePerSecond()
    {
        var logDir = CreateTempLogDir();
        try
        {
            var configuration = new ConfigurationManager();
            configuration.AddJsonFile(FindProductionAppsettings(), optional: false, reloadOnChange: false);
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.LogDir)}"] = logDir,
                    [$"{NntpdOptions.SectionName}:{nameof(NntpdOptions.ApplicationName)}"] = "VectorNNTP.NNTPD",
                });
            NntpdFileLogging.BindResolvedFilePath(configuration);

            Assert.Equal(TimeSpan.FromSeconds(1), NntpdNewsLogging.ReadSettings(configuration).FlushToDiskInterval);
            Assert.Equal(TimeSpan.FromSeconds(1), NntpdPathSurveyLogging.ReadSettings(configuration).FlushToDiskInterval);

            using var news = NntpdNewsLogging.CreateLogger(configuration);
            using var inpaths = NntpdPathSurveyLogging.CreateLogger(
                configuration,
                new IgnoringCompletedPathSurveyFileHandler(),
                NullLogger.Instance);
            AssertFlushesOncePerSecond(WalkLogEventSinks(news));
            AssertFlushesOncePerSecond(WalkLogEventSinks(inpaths));
        }
        finally
        {
            Log.CloseAndFlush();
            TryDelete(logDir);
        }
    }

    [Fact]
    public void ResolveDirectory_UsesCommonHelperAgainstApplicationBase()
    {
        var relative = NntpdFileLogging.ResolveDirectory(NntpdOptions.DefaultLogDir);
        Assert.Equal(
            ApplicationLocalPath.ResolveApplicationLocalPath(NntpdOptions.DefaultLogDir, AppContext.BaseDirectory),
            relative);

        var absolute = Path.Combine(Path.GetTempPath(), "vectornntp-logdir-abs");
        Assert.Equal(Path.GetFullPath(absolute), NntpdFileLogging.ResolveDirectory(absolute));
        Assert.DoesNotContain("logs/", NntpdFileLogging.RollingFilePath(absolute, "VectorNNTP.NNTPD"), StringComparison.Ordinal);
    }

    [Fact]
    public void RollingFilePath_UsesApplicationName()
    {
        var path = NntpdFileLogging.RollingFilePath("C:\\logs", "VectorNNTP.NNTPD");
        Assert.Equal(Path.Combine("C:\\logs", "VectorNNTP.NNTPD-.log"), path);
    }

    [Fact]
    public void ArchiveHooks_WritesGzipBesideRolledFile_WithoutCompressingActiveConvention()
    {
        var dir = CreateTempLogDir();
        try
        {
            var rolled = Path.Combine(dir, "VectorNNTP.NNTPD-20260101.log");
            File.WriteAllText(rolled, "completed-day");
            var expectedName = NntpdFileLogging.GzipArchiveFileName(rolled);
            Assert.Equal("VectorNNTP.NNTPD-20260101.log.gz", expectedName);

            NntpdSerilogHooks.DailyGzipFastest.OnFileDeleting(rolled);

            var archive = Path.Combine(dir, expectedName);
            Assert.True(File.Exists(archive));
            Assert.True(File.Exists(rolled));
            using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            Assert.Equal("completed-day", reader.ReadToEnd());
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void ArchiveHooks_DefaultConstructor_DoesNotDeleteHistoricalGzipArchives()
    {
        var dir = CreateTempLogDir();
        try
        {
            var olderArchive = Path.Combine(dir, "VectorNNTP.NNTPD-20260101.log.gz");
            File.WriteAllBytes(olderArchive, [0x1F, 0x8B, 0x08]);
            var rolled = Path.Combine(dir, "VectorNNTP.NNTPD-20260102.log");
            File.WriteAllText(rolled, "newer-day");

            // Default ArchiveHooks has no archive count limit. Serilog File retention
            // matches *.log only; this hook must not remove sibling .gz files.
            NntpdSerilogHooks.DailyGzipFastest.OnFileDeleting(rolled);

            Assert.True(File.Exists(olderArchive));
            Assert.True(File.Exists(Path.Combine(dir, "VectorNNTP.NNTPD-20260102.log.gz")));
            Assert.Equal(2, Directory.GetFiles(dir, "*.gz").Length);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void ConsoleReceivesInformation_ButNotDebug_FileReceivesDebugAndTakeThis()
    {
        var dir = CreateTempLogDir();
        var captured = new StringWriter();
        var previous = Console.Out;
        IHost? host = null;
        try
        {
            Console.SetOut(captured);
            host = CreateFileLoggingHost(dir);
            var factory = host.Services.GetRequiredService<ILoggerFactory>();
            var logger = factory.CreateLogger("VectorNNTP.NNTPD.Session.NntpSession");

            Assert.True(logger.IsEnabled(LogLevel.Information));
            Assert.True(logger.IsEnabled(LogLevel.Debug));
            Assert.True(logger.IsEnabled(LogLevel.Trace));

            logger.LogInformation("file-logging-info-marker");
            logger.LogDebug("file-logging-debug-marker");
            CommandLogMessages.CommandRx(logger, "198.51.100.20:9", "TAKETHIS <diag@ex.com>");

            host.Dispose();
            host = null;
            Log.CloseAndFlush();
        }
        finally
        {
            Console.SetOut(previous);
            host?.Dispose();
        }

        var console = captured.ToString();
        Assert.Contains("file-logging-info-marker", console, StringComparison.Ordinal);
        Assert.DoesNotContain("file-logging-debug-marker", console, StringComparison.Ordinal);
        Assert.DoesNotContain("TAKETHIS", console, StringComparison.Ordinal);

        var applicationName = ApplicationJsonConfiguration.EntryAssemblyName;
        var daily = Assert.Single(
            Directory.GetFiles(dir, applicationName + "-*.log"),
            path => Path.GetFileName(path).StartsWith(applicationName + "-", StringComparison.Ordinal)
                    && path.EndsWith(".log", StringComparison.Ordinal)
                    && !path.EndsWith(".log.gz", StringComparison.Ordinal));
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(applicationName) + @"-\d{8}\.log$", Path.GetFileName(daily));
        Assert.DoesNotContain("VectorNNTP.NNTPD-", Path.GetFileName(daily), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(dir, "*.gz"));

        var fileText = File.ReadAllText(daily);
        Assert.Contains("file-logging-info-marker", fileText, StringComparison.Ordinal);
        Assert.Contains("file-logging-debug-marker", fileText, StringComparison.Ordinal);
        Assert.Contains("TAKETHIS <diag@ex.com>", fileText, StringComparison.Ordinal);

        TryDelete(dir);
    }

    [Fact]
    public void ConfiguredLogDir_IsHonoured_AndCreatedWhenMissing()
    {
        var parent = Path.Combine(Path.GetTempPath(), "vectornntp-logdir-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(parent, "nested-logs");
        Assert.False(Directory.Exists(dir));
        IHost? host = null;
        try
        {
            host = CreateFileLoggingHost(dir);
            var logger = host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("VectorNNTP.NNTPD");
            logger.LogInformation("created-dir-marker");
            host.Dispose();
            host = null;
            Log.CloseAndFlush();

            Assert.True(Directory.Exists(dir));
            Assert.Contains(
                Directory.GetFiles(dir, "*.log"),
                path => File.ReadAllText(path).Contains("created-dir-marker", StringComparison.Ordinal));
        }
        finally
        {
            host?.Dispose();
            TryDelete(parent);
        }
    }

    private static IHost CreateFileLoggingHost(string logDir)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        TestHostFactory.ConfigureNntpdTestHost(builder);
        var settings = new Dictionary<string, string?>
        {
            [$"{NntpdOptions.SectionName}:LogDir"] = logDir,
            [$"{NntpdOptions.SectionName}:ApplicationName"] = "VectorNNTP.NNTPD",
            ["Serilog:Using:0"] = "Serilog.Sinks.Console",
            ["Serilog:Using:1"] = "Serilog.Sinks.File",
            ["Serilog:Using:2"] = "Serilog.Sinks.Async",
            ["Serilog:Using:3"] = "Serilog.Sinks.File.Archive",
            ["Serilog:MinimumLevel:Default"] = "Information",
            ["Serilog:MinimumLevel:Override:Microsoft"] = "Warning",
            ["Serilog:MinimumLevel:Override:Microsoft.Hosting.Lifetime"] = "Information",
            ["Serilog:MinimumLevel:Override:System"] = "Warning",
            ["Serilog:MinimumLevel:Override:VectorNNTP.NNTPD"] = "Verbose",
            ["Serilog:WriteTo:0:Name"] = "Console",
            ["Serilog:WriteTo:0:Args:restrictedToMinimumLevel"] = "Information",
            ["Serilog:WriteTo:0:Args:outputTemplate"] = NntpdLoggingExtensions.ConsoleOutputTemplate,
        };
        foreach (var pair in NntpdFileLogging.AsyncFileWriteToKeys())
        {
            settings[pair.Key] = pair.Value;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.ConfigureNntpdLogging();
        builder.Services.AddNntpdHosting(includePlaceholderService: false);
        return builder.Build();
    }

    private static string CreateTempLogDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vectornntp-filelog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FindProductionAppsettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "VectorNNTP.NNTPD", "VectorNNTP.NNTPD.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate src/VectorNNTP.NNTPD/VectorNNTP.NNTPD.json.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static object? ReadInstanceField(object instance, string name)
    {
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field is not null)
            {
                return field.GetValue(instance);
            }
        }

        return null;
    }

    private static void AssertFlushesOncePerSecond(IEnumerable<object> sinks)
    {
        var flush = Assert.Single(
            sinks,
            static n => n.GetType().Name.Equals("PeriodicFlushToDiskSink", StringComparison.Ordinal));
        var timer = ReadInstanceField(flush, "_timer")
                    ?? throw new InvalidOperationException("PeriodicFlushToDiskSink._timer was not found.");
        var holder = timer.GetType().GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(timer)
                     ?? throw new InvalidOperationException("Timer._timer was not found.");
        var queueTimer = holder.GetType().GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(holder)
                         ?? throw new InvalidOperationException("TimerHolder._timer was not found.");
        var period = queueTimer.GetType().GetField("_period", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(queueTimer)
                     ?? throw new InvalidOperationException($"{queueTimer.GetType().FullName}._period was not found.");
        Assert.Equal(1000u, Assert.IsType<uint>(period));
    }

    private sealed class IgnoringCompletedPathSurveyFileHandler : ICompletedPathSurveyFileHandler
    {
        public void OnCompletedFile(string completedFilePath)
        {
        }
    }

    private static IEnumerable<object> WalkLogEventSinks(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;
            foreach (var field in current.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var value = field.GetValue(current);
                if (value is ILogEventSink sink)
                {
                    pending.Push(sink);
                    continue;
                }

                if (value is IEnumerable<ILogEventSink> sinks)
                {
                    foreach (var inner in sinks)
                    {
                        pending.Push(inner);
                    }
                }
            }
        }
    }
}
