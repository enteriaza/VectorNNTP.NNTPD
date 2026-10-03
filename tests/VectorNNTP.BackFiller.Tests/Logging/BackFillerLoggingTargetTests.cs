using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Formatting.Json;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Logging;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.RabbitMq;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.Logging;

[Collection(SerilogCollection.Name)]
public sealed class BackFillerLoggingTargetTests
{
    [Fact]
    public void Defaults_EnableFile_AndDisableRabbitMq_Syslog_AndJson()
    {
        var configuration = new ConfigurationManager();
        var logging = new BackFillerLoggingOptions();
        configuration.GetSection($"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}").Bind(logging);
        Assert.True(logging.File.Enabled);
        Assert.Equal(BackFillerFileLoggingTargetOptions.DefaultLogDir, logging.File.LogDir);
        Assert.False(logging.RabbitMq.Enabled);
        Assert.Equal("logs", logging.RabbitMq.Exchange);
        Assert.Equal("backfiller", logging.RabbitMq.RoutingKey);
        Assert.False(logging.Syslog.Enabled);
        Assert.Equal(514, logging.Syslog.Port);
        Assert.Equal("Udp", logging.Syslog.Protocol);
        Assert.False(logging.Json);
        Assert.Equal(BackFillerLoggingOptions.DefaultLogLevel, logging.LogLevel);
        Assert.Equal(BackFillerLoggingOptions.DefaultLogRetentionDays, logging.LogRetentionDays);
    }

    [Fact]
    public void File_CanBeDisabled_WithoutCreatingTheLogDirectory()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "vectornntp-bf-nolog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = Configuration(logDir);
            configuration[$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:File:Enabled"] = "false";
            using var logger = Build(configuration);
            Assert.DoesNotContain(Sinks(logger), static sink => sink.GetType().Name.Contains("File", StringComparison.Ordinal));
            Assert.False(Directory.Exists(logDir));
        }
        finally
        {
            Log.CloseAndFlush();
            TryDelete(logDir);
        }
    }

    [Fact]
    public void File_Syslog_AndRabbitMq_CanBeEnabledTogether()
    {
        var logDir = NewLogDir();
        try
        {
            var configuration = Configuration(logDir);
            var logging = $"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}";
            configuration[$"{logging}:RabbitMQ:Enabled"] = "true";
            configuration[$"{logging}:Syslog:Enabled"] = "true";
            configuration[$"{logging}:Syslog:Host"] = "127.0.0.1";
            configuration[$"{logging}:Syslog:Protocol"] = "Udp";
            using var services = RabbitServices();
            using var logger = Build(configuration, services: services);
            var names = Sinks(logger).Select(static sink => sink.GetType().Name).ToArray();
            Assert.Contains(names, static name => name.Contains("File", StringComparison.Ordinal));
            Assert.Contains(names, static name => name.Contains("Syslog", StringComparison.Ordinal));
            Assert.Contains(names, static name => name.Equals("RabbitMqLogEventSink", StringComparison.Ordinal));
            var namespaced = typeof(BackFillerLoggingExtensions).Assembly.GetReferencedAssemblies().Select(static name => name.Name);
            Assert.DoesNotContain("Serilog.Sinks.RabbitMQ", namespaced);
        }
        finally
        {
            Log.CloseAndFlush();
            TryDelete(logDir);
        }
    }

    [Fact]
    public async Task RabbitMqTarget_PublishesOnTheExistingConnection_WithoutBlockingTheCaller()
    {
        var logDir = NewLogDir();
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var service = RabbitMqServiceTests.CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var connection = factory.LastConnection!;
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.DefaultPublishConfirmBehavior = FakePublishConfirmBehavior.Confirm;
            connection.BlockCreatePublishChannel = gate;
            connection.CreatePublishChannelStarted = started;
            connection.OnPublishChannelCreated = channel => channel.Enqueued = published;
            var configuration = Configuration(logDir);
            configuration[$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:RabbitMQ:Enabled"] = "true";
            var services = new ServiceCollection();
            services.AddSingleton<IRabbitMqService>(service);
            using var provider = services.BuildServiceProvider();
            using var logger = Build(configuration, services: provider);

            var logged = Task.Run(() => logger.Information("rabbit-existing-connection"));
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await started.Task.WaitAsync(safety.Token);
            await logged.WaitAsync(safety.Token);
            Assert.Equal(1, factory.ConnectCount);
            Assert.Empty(connection.PublishChannels);

            gate.TrySetResult();
            await published.Task.WaitAsync(safety.Token);
            Assert.Equal(1, factory.ConnectCount);
            var publication = Assert.Single(Assert.Single(connection.PublishChannels).Publications);
            Assert.Equal("logs", publication.Exchange);
            Assert.Equal("backfiller", publication.RoutingKey);
            Assert.Contains("rabbit-existing-connection", Encoding.UTF8.GetString(publication.Body.Span), StringComparison.Ordinal);
            Assert.DoesNotContain(BackFillerTestOptions.SecretPassword, Encoding.UTF8.GetString(publication.Body.Span), StringComparison.Ordinal);
        }
        finally
        {
            gate.TrySetResult();
            Log.CloseAndFlush();
            await service.DisposeAsync();
            TryDelete(logDir);
        }
    }

    [Theory]
    [InlineData("Udp")]
    [InlineData("TCP")]
    public async Task Syslog_ConfiguresUdpAndTcpTargets(string protocol)
    {
        var logDir = NewLogDir();
        UdpClient? udp = null;
        TcpListener? tcp = null;
        try
        {
            int port;
            if (protocol.Equals("Udp", StringComparison.OrdinalIgnoreCase))
            {
                udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            }
            else
            {
                tcp = new TcpListener(IPAddress.Loopback, 0);
                tcp.Start();
                port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            }

            var configuration = Configuration(logDir);
            var section = $"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:Syslog";
            configuration[$"{section}:Enabled"] = "true";
            configuration[$"{section}:Host"] = "127.0.0.1";
            configuration[$"{section}:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            configuration[$"{section}:Protocol"] = protocol;
            var logger = Build(configuration);
            logger.Information("syslog-target-marker");
            (logger as IDisposable)?.Dispose();

            Assert.Contains(Sinks(logger), static sink => sink.GetType().Name.Contains("Syslog", StringComparison.Ordinal));
            if (udp is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var packet = await udp.ReceiveAsync(timeout.Token);
                var text = Encoding.UTF8.GetString(packet.Buffer);
                Assert.Contains("syslog-target-marker", text, StringComparison.Ordinal);
            }
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var client = await tcp!.AcceptTcpClientAsync(timeout.Token);
                var buffer = new byte[256];
                var read = await client.GetStream().ReadAsync(buffer, timeout.Token);
                Assert.True(read > 0);
            }
        }
        finally
        {
            udp?.Dispose();
            tcp?.Stop();
            Log.CloseAndFlush();
            TryDelete(logDir);
        }
    }

    [Fact]
    public async Task JsonFormatter_AppliesToFile_Console_AndSyslog_WithoutPerTargetSettings()
    {
        var logDir = NewLogDir();
        var captured = new StringWriter();
        var previous = Console.Out;
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            Console.SetOut(captured);
            var configuration = Configuration(logDir);
            var logging = $"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}";
            configuration[$"{logging}:Json"] = "true";
            configuration[$"{logging}:File:Formatter"] = "not-a-formatter";
            configuration[$"{logging}:Syslog:Json"] = "true";
            configuration[$"{logging}:Syslog:Enabled"] = "true";
            configuration[$"{logging}:Syslog:Host"] = "127.0.0.1";
            configuration[$"{logging}:Syslog:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            configuration[$"{logging}:Syslog:Protocol"] = "Udp";
            configuration[$"{logging}:RabbitMQ:Enabled"] = "true";
            configuration[$"{logging}:RabbitMQ:Json"] = "true";
            using var services = RabbitServices();
            var logger = Build(
                configuration,
                new BackFillerLoggingCommandLine(Console: true, EnrichFromLogContext: false),
                services);
            logger.Information("json-formatter-marker");
            (logger as IDisposable)?.Dispose();
            var rabbitSink = Assert.Single(Sinks(logger).OfType<RabbitMqLogEventSink>());
            Assert.IsType<JsonFormatter>(rabbitSink.Formatter);
            Assert.Equal(RabbitMqLogEventSink.JsonContentType, rabbitSink.ContentType);

            var fileText = File.ReadAllText(Directory.GetFiles(logDir, "*.log").Single());
            Assert.Contains("\"MessageTemplate\"", fileText, StringComparison.Ordinal);
            Assert.Contains("json-formatter-marker", captured.ToString(), StringComparison.Ordinal);
            Assert.Contains("{", captured.ToString(), StringComparison.Ordinal);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var packet = await udp.ReceiveAsync(timeout.Token);
            var syslog = Encoding.UTF8.GetString(packet.Buffer);
            Assert.Contains("\"MessageTemplate\"", syslog, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previous);
            udp?.Dispose();
            Log.CloseAndFlush();
            TryDelete(logDir);
        }
    }

    [Fact]
    public void TextFormatter_StaysInEffect_WhenOnlyADecoyPerTargetFormatterIsSet()
    {
        var logDir = NewLogDir();
        try
        {
            var configuration = Configuration(logDir);
            var logging = $"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}";
            configuration[$"{logging}:File:Formatter"] = "Json";
            configuration[$"{logging}:RabbitMQ:Enabled"] = "true";
            configuration[$"{logging}:RabbitMQ:Formatter"] = "Json";
            using var services = RabbitServices();
            var logger = Build(configuration, services: services);
            var rabbitSink = Assert.Single(Sinks(logger).OfType<RabbitMqLogEventSink>());
            Assert.IsType<MessageTemplateTextFormatter>(rabbitSink.Formatter);
            Assert.Equal(RabbitMqLogEventSink.TextContentType, rabbitSink.ContentType);
            logger.Information("plain-template-marker");
            (logger as IDisposable)?.Dispose();
            var fileText = File.ReadAllText(Directory.GetFiles(logDir, "*.log").Single());
            Assert.Contains("plain-template-marker", fileText, StringComparison.Ordinal);
            Assert.DoesNotContain("\"MessageTemplate\"", fileText, StringComparison.Ordinal);
        }
        finally
        {
            Log.CloseAndFlush();
            TryDelete(logDir);
        }
    }

    [Fact]
    public void File_And_Syslog_CanBeEnabledTogether()
    {
        var logDir = NewLogDir();
        try
        {
            var configuration = Configuration(logDir);
            var syslog = $"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:Syslog";
            configuration[$"{syslog}:Enabled"] = "true";
            configuration[$"{syslog}:Host"] = "127.0.0.1";
            configuration[$"{syslog}:Protocol"] = "Tcp";
            configuration[$"{syslog}:Port"] = "9";
            using var logger = Build(configuration);
            var names = Sinks(logger).Select(static sink => sink.GetType().Name).ToArray();
            Assert.Contains(names, static name => name.Contains("File", StringComparison.Ordinal));
            Assert.Contains(names, static name => name.Contains("Syslog", StringComparison.Ordinal));
        }
        finally
        {
            Log.CloseAndFlush();
            TryDelete(logDir);
        }
    }

    [Fact]
    public void Console_IsAbsentUnlessTheConsoleSwitchIsPresent()
    {
        var absent = BackFillerLoggingCommandLine.FromArguments(["--other"]);
        Assert.False(absent.Console);
        var present = BackFillerLoggingCommandLine.FromArguments(["--console"]);
        Assert.True(present.Console);
        Assert.False(present.EnrichFromLogContext);

        var logDir = NewLogDir();
        var captured = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(captured);
            using var logger = Build(Configuration(logDir));
            logger.Information("console-should-stay-off");
            Log.CloseAndFlush();
            Assert.DoesNotContain("console-should-stay-off", captured.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Sinks(logger), static sink => sink.GetType().Name.Contains("Console", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(previous);
            Log.CloseAndFlush();
            TryDelete(logDir);
        }
    }

    [Fact]
    public void LogContextEnrichment_IsAppliedOnlyWhenRequested()
    {
        var off = BackFillerLoggingCommandLine.FromArguments(null);
        Assert.False(off.EnrichFromLogContext);
        var on = BackFillerLoggingCommandLine.FromArguments(["--log-context"]);
        Assert.True(on.EnrichFromLogContext);
        Assert.False(on.Console);

        Assert.False(EventHasProbe(off));
        Assert.True(EventHasProbe(on));
    }

    private static bool EventHasProbe(BackFillerLoggingCommandLine commandLine)
    {
        var logDir = NewLogDir();
        try
        {
            var sink = new CollectingSink();
            var configuration = new LoggerConfiguration();
            BackFillerFileLogging.ConfigureLogger(configuration, Configuration(logDir), commandLine: commandLine);
            configuration.WriteTo.Sink(sink);
            using var logger = configuration.CreateLogger();
            using (LogContext.PushProperty("ProbeToken", "probe-value"))
            {
                logger.Information("context-marker");
            }

            var evt = Assert.Single(sink.Events);
            return evt.Properties.ContainsKey("ProbeToken");
        }
        finally
        {
            Log.CloseAndFlush();
            TryDelete(logDir);
        }
    }

    private static ConfigurationManager Configuration(string logDir)
    {
        var configuration = new ConfigurationManager();
        configuration[$"{BackFillerOptions.SectionName}:{BackFillerLoggingOptions.SectionName}:File:{nameof(BackFillerFileLoggingTargetOptions.LogDir)}"] = logDir;
        return configuration;
    }

    private static Logger Build(
        IConfiguration configuration,
        BackFillerLoggingCommandLine commandLine = default,
        IServiceProvider? services = null)
    {
        var loggerConfiguration = new LoggerConfiguration();
        BackFillerFileLogging.ConfigureLogger(
            loggerConfiguration,
            configuration,
            commandLine: commandLine,
            services: services);
        return loggerConfiguration.CreateLogger();
    }

    private static ServiceProvider RabbitServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRabbitMqService>(new UnavailableRabbitMqService());
        return services.BuildServiceProvider();
    }

    private static IEnumerable<object> Sinks(object root)
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
                object? value;
                try
                {
                    value = field.GetValue(current);
                }
                catch (TargetInvocationException)
                {
                    continue;
                }

                if (value is null or string or ValueType)
                {
                    continue;
                }

                pending.Push(value);
                if (value is IEnumerable enumerable)
                {
                    foreach (var inner in enumerable)
                    {
                        if (inner is not null and not string and not ValueType)
                        {
                            pending.Push(inner);
                        }
                    }
                }
            }
        }
    }

    private static string NewLogDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vectornntp-bf-target-" + Guid.NewGuid().ToString("N"));
        return dir;
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

    private sealed class UnavailableRabbitMqService : IRabbitMqService
    {
        public bool IsReady => false;

        public long ConnectionGeneration => 0;

        public event EventHandler<RabbitMqConnectionReplacedEventArgs>? ConnectionReplaced
        {
            add { }
            remove { }
        }

        public bool TryGetCurrent(out RabbitMqConnectionHandle handle)
        {
            handle = default;
            return false;
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            _events.Add(logEvent);
        }
    }
}
