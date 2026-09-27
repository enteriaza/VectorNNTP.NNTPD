using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.Debug;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Logging;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Tests.Logging;

[Collection(SerilogCollection.Name)]
public sealed class BackFillerSerilogLoggingTests
{
    [Fact]
    public void ConfigureBackFillerLogging_RegistersSerilog_AndRemovesDefaultProviders()
    {
        using var host = CreateLoggingHost();
        var factories = host.Services.GetServices<ILoggerFactory>().ToArray();
        Assert.Single(factories);
        Assert.Equal("SerilogLoggerFactory", factories[0].GetType().Name);

        var providers = host.Services.GetLoggerProviders();
        Assert.Empty(providers);
        Assert.DoesNotContain(providers, static p => p is ConsoleLoggerProvider);
        Assert.DoesNotContain(providers, static p => p is DebugLoggerProvider);
    }

    [Fact]
    public void ConsoleSink_IsConfiguredAtDebug()
    {
        using var host = CreateLoggingHost();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        var logger = factory.CreateLogger("VectorNNTP.BackFiller.Hosting");

        Assert.True(logger.IsEnabled(LogLevel.Debug));
        Assert.Equal(LogEventLevel.Debug, BackFillerFileLogging.ConsoleMinimumLevel);
        Assert.Equal("Debug", ProductionSerilogSection()["Serilog:WriteTo:0:Args:restrictedToMinimumLevel"]);
    }

    [Fact]
    public void FileSink_IsConfigured_BeneathApplicationLocalLogs()
    {
        var logDir = CreateTempLogDir();
        IHost? host = null;
        try
        {
            host = CreateLoggingHost(logDir);
            var logger = host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("VectorNNTP.BackFiller");
            logger.LogInformation("backfiller-file-sink-marker");
            host.Dispose();
            host = null;
            Log.CloseAndFlush();

            var expectedDir = ApplicationLocalPath.ResolveApplicationLocalPath(logDir, AppContext.BaseDirectory);
            Assert.Equal(expectedDir, BackFillerFileLogging.ResolveDirectory(logDir));
            Assert.True(Directory.Exists(expectedDir));
            Assert.Contains(
                Directory.GetFiles(expectedDir, "VectorNNTP.BackFiller-*.log"),
                path => File.ReadAllText(path).Contains("backfiller-file-sink-marker", StringComparison.Ordinal));
        }
        finally
        {
            host?.Dispose();
            TryDelete(logDir);
        }
    }

    [Fact]
    public void ResolveDirectory_UsesCommonHelper_NotCurrentWorkingDirectory()
    {
        var previous = Environment.CurrentDirectory;
        var cwd = Directory.CreateTempSubdirectory("bf-serilog-cwd-").FullName;
        try
        {
            Environment.CurrentDirectory = cwd;
            var resolved = BackFillerFileLogging.ResolveDirectory("logs");
            Assert.Equal(
                ApplicationLocalPath.ResolveApplicationLocalPath("logs", AppContext.BaseDirectory),
                resolved);
            Assert.StartsWith(
                Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                resolved,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(cwd), resolved, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(Path.GetFullPath("logs"), resolved);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TryDelete(cwd);
        }
    }

    [Fact]
    public void BindResolvedFilePath_OverwritesPlaceholder_FromLogDirectory()
    {
        var logDir = CreateTempLogDir();
        try
        {
            var configuration = new ConfigurationManager();
            foreach (var pair in BackFillerFileLogging.AsyncFileWriteToKeys())
            {
                configuration[pair.Key] = pair.Value;
            }

            configuration[$"{BackFillerOptions.SectionName}:{nameof(BackFillerOptions.LogDirectory)}"] = logDir;
            BackFillerFileLogging.BindResolvedFilePath(configuration);

            var expected = BackFillerFileLogging.RollingFilePath(logDir, BackFillerFileLogging.ApplicationName);
            Assert.Equal(expected, configuration["Serilog:WriteTo:1:Args:configure:0:Args:path"]);
            Assert.True(Directory.Exists(logDir));
            Assert.DoesNotContain(
                "logs/VectorNNTP.BackFiller-.log",
                configuration["Serilog:WriteTo:1:Args:configure:0:Args:path"],
                StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(logDir);
        }
    }

    [Fact]
    public void ProductionAppsettings_DeclaresNntpdEquivalentAsyncFileSink()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindProductionAppsettings()));
        var serilog = doc.RootElement.GetProperty("Serilog");
        var usingNames = serilog.GetProperty("Using").EnumerateArray().Select(static e => e.GetString()).ToArray();
        Assert.Contains("Serilog.Sinks.Console", usingNames);
        Assert.Contains("Serilog.Sinks.File", usingNames);
        Assert.Contains("Serilog.Sinks.Async", usingNames);
        Assert.Contains("Serilog.Sinks.File.Archive", usingNames);

        var console = serilog.GetProperty("WriteTo")[0];
        Assert.Equal("Console", console.GetProperty("Name").GetString());
        Assert.Equal("Debug", console.GetProperty("Args").GetProperty("restrictedToMinimumLevel").GetString());

        var async = serilog.GetProperty("WriteTo")[1];
        Assert.Equal("Async", async.GetProperty("Name").GetString());
        Assert.Equal(50000, async.GetProperty("Args").GetProperty("bufferSize").GetInt32());
        Assert.True(async.GetProperty("Args").GetProperty("blockWhenFull").GetBoolean());
        var file = async.GetProperty("Args").GetProperty("configure")[0];
        Assert.Equal("File", file.GetProperty("Name").GetString());
        var args = file.GetProperty("Args");
        Assert.Equal("Verbose", args.GetProperty("restrictedToMinimumLevel").GetString());
        Assert.Equal("Day", args.GetProperty("rollingInterval").GetString());
        Assert.Equal(1, args.GetProperty("retainedFileCountLimit").GetInt32());
        Assert.True(args.GetProperty("buffered").GetBoolean());
        Assert.False(args.GetProperty("rollOnFileSizeLimit").GetBoolean());
        Assert.Equal(JsonValueKind.Null, args.GetProperty("fileSizeLimitBytes").ValueKind);
        Assert.Equal("logs/VectorNNTP.BackFiller-.log", args.GetProperty("path").GetString());
        Assert.Equal(
            "VectorNNTP.BackFiller.Logging.BackFillerSerilogHooks::DailyGzipFastest, VectorNNTP.BackFiller",
            args.GetProperty("hooks").GetString());
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
                    [$"{BackFillerOptions.SectionName}:{nameof(BackFillerOptions.LogDirectory)}"] = logDir,
                });
            BackFillerFileLogging.BindResolvedFilePath(configuration);

            using var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

            var sinks = WalkLogEventSinks(logger).ToArray();
            var fileSink = Assert.Single(
                sinks,
                static n => n.GetType().Name.Equals("RollingFileSink", StringComparison.Ordinal));
            Assert.Null(ReadInstanceField(fileSink, "_fileSizeLimitBytes"));
            Assert.False(Assert.IsType<bool>(ReadInstanceField(fileSink, "_rollOnFileSizeLimit")!));
            Assert.Equal(1, ReadInstanceField(fileSink, "_retainedFileCountLimit"));
            Assert.True(Assert.IsType<bool>(ReadInstanceField(fileSink, "_buffered")!));
            Assert.Same(BackFillerSerilogHooks.DailyGzipFastest, ReadInstanceField(fileSink, "_hooks"));

            var asyncSink = Assert.Single(
                sinks,
                static n => n.GetType().Name.Equals("BackgroundWorkerSink", StringComparison.Ordinal));
            Assert.True(Assert.IsType<bool>(ReadInstanceField(asyncSink, "_blockWhenFull")!));
            var queue = ReadInstanceField(asyncSink, "_queue")
                        ?? throw new InvalidOperationException("BackgroundWorkerSink._queue was not found.");
            var boundedCapacity = queue.GetType().GetProperty("BoundedCapacity")?.GetValue(queue)
                                  ?? throw new InvalidOperationException("BoundedCapacity was not found.");
            Assert.Equal(50000, Convert.ToInt32(boundedCapacity, CultureInfo.InvariantCulture));
        }
        finally
        {
            Log.CloseAndFlush();
            TryDelete(logDir);
        }
    }

    [Fact]
    public void ConsoleReceivesDebug_FileReceivesVerbose()
    {
        var logDir = CreateTempLogDir();
        var captured = new StringWriter();
        var previous = Console.Out;
        IHost? host = null;
        try
        {
            Console.SetOut(captured);
            host = CreateLoggingHost(logDir);
            var logger = host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("VectorNNTP.BackFiller");

            Assert.True(logger.IsEnabled(LogLevel.Debug));
            Assert.True(logger.IsEnabled(LogLevel.Trace));

            logger.LogInformation("backfiller-console-info-marker");
            logger.LogDebug("backfiller-console-debug-marker");
            logger.LogTrace("backfiller-file-trace-marker");

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
        Assert.Contains("backfiller-console-info-marker", console, StringComparison.Ordinal);
        Assert.Contains("backfiller-console-debug-marker", console, StringComparison.Ordinal);
        Assert.DoesNotContain("backfiller-file-trace-marker", console, StringComparison.Ordinal);

        var daily = Assert.Single(
            Directory.GetFiles(logDir, "VectorNNTP.BackFiller-*.log"),
            path => Path.GetFileName(path).StartsWith("VectorNNTP.BackFiller-", StringComparison.Ordinal)
                    && path.EndsWith(".log", StringComparison.Ordinal)
                    && !path.EndsWith(".log.gz", StringComparison.Ordinal));
        var fileText = File.ReadAllText(daily);
        Assert.Contains("backfiller-console-info-marker", fileText, StringComparison.Ordinal);
        Assert.Contains("backfiller-console-debug-marker", fileText, StringComparison.Ordinal);
        Assert.Contains("backfiller-file-trace-marker", fileText, StringComparison.Ordinal);
        TryDelete(logDir);
    }

    [Fact]
    public void LoggingInitialized_ReachesSerilog_ThroughILogger()
    {
        var sink = new CollectingSink();
        using var host = CreateLoggingHost(configure: lc => lc.WriteTo.Sink(sink));

        BackFillerLoggingExtensions.WriteLoggingInitialized(
            host.Services,
            "Production",
            AppContext.BaseDirectory);

        var evt = Assert.Single(
            sink.Events,
            e => e.RenderMessage().Contains("Application logging initialized", StringComparison.Ordinal));
        Assert.Equal(LogEventLevel.Information, evt.Level);
        Assert.Contains("SerilogLoggerFactory", evt.RenderMessage(), StringComparison.Ordinal);
        Assert.Contains(BackFillerLogCategories.Hosting, evt.RenderMessage(), StringComparison.Ordinal);
        Assert.Contains(BackFillerFileLogging.ApplicationName, evt.RenderMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain("Password", evt.RenderMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionAssembly_ReferencesArchiveHooksAndConsole()
    {
        var names = typeof(BackFillerLoggingExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(static a => a.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Serilog.Sinks.Console", names);
        Assert.Contains("Serilog.Sinks.File.Archive", names);
        Assert.NotNull(BackFillerSerilogHooks.DailyGzipFastest);
    }

    private static IHost CreateLoggingHost(string? logDir = null, Action<LoggerConfiguration>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        var settings = ProductionSerilogSection();
        settings[$"{BackFillerOptions.SectionName}:{nameof(BackFillerOptions.LogDirectory)}"] =
            logDir ?? CreateTempLogDir();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.ConfigureBackFillerLogging(configure);
        return builder.Build();
    }

    private static Dictionary<string, string?> ProductionSerilogSection()
    {
        var section = new Dictionary<string, string?>
        {
            ["Serilog:Using:0"] = "Serilog.Sinks.Console",
            ["Serilog:Using:1"] = "Serilog.Sinks.File",
            ["Serilog:Using:2"] = "Serilog.Sinks.Async",
            ["Serilog:Using:3"] = "Serilog.Sinks.File.Archive",
            ["Serilog:MinimumLevel:Default"] = "Information",
            ["Serilog:MinimumLevel:Override:Microsoft"] = "Warning",
            ["Serilog:MinimumLevel:Override:Microsoft.Hosting.Lifetime"] = "Information",
            ["Serilog:MinimumLevel:Override:System"] = "Warning",
            ["Serilog:MinimumLevel:Override:VectorNNTP.BackFiller"] = "Verbose",
            ["Serilog:WriteTo:0:Name"] = "Console",
            ["Serilog:WriteTo:0:Args:restrictedToMinimumLevel"] = "Debug",
            ["Serilog:WriteTo:0:Args:outputTemplate"] = BackFillerLoggingExtensions.ConsoleOutputTemplate,
            ["Serilog:Enrich:0"] = "FromLogContext",
            ["Serilog:Properties:Application"] = BackFillerFileLogging.ApplicationName,
        };
        foreach (var pair in BackFillerFileLogging.AsyncFileWriteToKeys())
        {
            section[pair.Key] = pair.Value;
        }

        return section;
    }

    private static string CreateTempLogDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vectornntp-bf-filelog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FindProductionAppsettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "VectorNNTP.BackFiller", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate src/VectorNNTP.BackFiller/appsettings.json.");
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

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            _events.Enqueue(logEvent);
        }
    }
}
