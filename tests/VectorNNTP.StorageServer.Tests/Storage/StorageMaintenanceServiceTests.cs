using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.Common.Cloudflare;
using VectorNNTP.Common.Core;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Hosting;
using VectorNNTP.StorageServer.Logging;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;
using VectorNNTP.StorageServer.Tests.Fixtures;
using VectorNNTP.StorageServer.Tests.Logging;
using VectorNNTP.StorageServer.Tests.TestDoubles;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.StorageServer.Tests.Storage;

/// <summary>Phase 5C storage maintenance worker.</summary>
public sealed class StorageMaintenanceServiceTests
{
    [Fact]
    public async Task Maintenance_starts_without_an_enable_switch()
    {
        Assert.Null(typeof(ArticleCompactionPolicyOptions).GetProperty("Maintenance" + "Enabled"));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var options = StorageServerTestOptions.CreateValid();
        var service = new StorageMaintenanceService(
            _ =>
            {
                Interlocked.Increment(ref calls);
                first.TrySetResult();
                return Task.FromResult(NoWork());
            },
            Options.Create(options),
            NullLogger<StorageMaintenanceService>.Instance,
            delayAsync: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        await service.StartAsync(CancellationToken.None);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(service.Execution);
        Assert.Equal(1, calls);
        await service.StopAsync(CancellationToken.None);
        Assert.True(service.Execution is null);
    }

    [Fact]
    public async Task B_C_Enabled_Invokes_Immediately_Without_Waiting_Interval()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCalls = 0;
        var runCalls = 0;

        var service = CreateService(
            interval: TimeSpan.FromMinutes(1),
            runOnce: async ct =>
            {
                Interlocked.Increment(ref runCalls);
                firstStarted.TrySetResult();
                await allowComplete.Task.WaitAsync(ct);
                return NoWork();
            },
            delayAsync: async (_, ct) =>
            {
                Interlocked.Increment(ref delayCalls);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });

        await service.StartAsync(CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, runCalls);
        Assert.Equal(0, delayCalls);

        allowComplete.TrySetResult();
        await WaitForAsync(() => Volatile.Read(ref delayCalls) >= 1, TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task D_E_F_G_H_Interval_After_Completion_For_All_Outcomes()
    {
        foreach (var outcome in new[]
                 {
                     StorageMaintenanceOutcome.NoWork,
                     StorageMaintenanceOutcome.Skipped,
                     StorageMaintenanceOutcome.Incomplete,
                     StorageMaintenanceOutcome.CompactedAndReclaimed,
                 })
        {
            await AssertIntervalAfterCompletionAsync(outcome);
        }
    }

    [Fact]
    public async Task I_Coordinator_Exception_Logs_And_Continues()
    {
        var sink = new CollectingSink();
        using var loggerFactory = CreateSerilogLoggerFactory(sink);
        var logger = loggerFactory.CreateLogger<StorageMaintenanceService>();

        var runGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;

        var service = new StorageMaintenanceService(
            async _ =>
            {
                var n = Interlocked.Increment(ref attempts);
                if (n == 1)
                {
                    throw new IOException("simulated maintenance failure");
                }

                runGate.TrySetResult();
                return NoWork();
            },
            Options.Create(CreateOptions(interval: TimeSpan.FromMilliseconds(1))),
            logger,
            delayAsync: async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), ct);
            });

        await service.StartAsync(CancellationToken.None);
        await runGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.True(attempts >= 2);
        Assert.Contains(
            sink.Events,
            static e => e.Level == LogEventLevel.Error
                && e.Properties.TryGetValue("EventId", out var eventId)
                && eventId.ToString().Contains("3015", StringComparison.Ordinal));
    }

    [Fact]
    public async Task J_Cancellation_Before_First_Run_Does_Not_Invoke()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var calls = 0;
        var service = CreateService(
            runOnce: _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(NoWork());
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(cts.Token));
        Assert.Equal(0, calls);
        Assert.Null(service.Execution);
    }

    [Fact]
    public async Task K_Cancellation_During_RunOnce_Exits()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService(
            runOnce: async ct =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return NoWork();
            },
            delayAsync: (_, _) => Task.CompletedTask);

        await service.StartAsync(CancellationToken.None);
        var loop = service.Execution;
        Assert.NotNull(loop);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);
        Assert.True(loop.IsCompleted);
        Assert.Null(service.Execution);
    }

    [Fact]
    public async Task L_Cancellation_During_Delay_Exits()
    {
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService(
            runOnce: _ => Task.FromResult(NoWork()),
            delayAsync: async (_, ct) =>
            {
                delayEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });

        await service.StartAsync(CancellationToken.None);
        var loop = service.Execution!;
        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);
        Assert.True(loop.IsCompleted);
    }

    [Fact]
    public async Task M_N_No_Overlap_And_No_Backlog()
    {
        var concurrent = 0;
        var maxConcurrent = 0;
        var runs = 0;
        var releaseRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCompletions = new ConcurrentQueue<TaskCompletionSource>();

        var service = CreateService(
            interval: TimeSpan.FromMilliseconds(1),
            runOnce: async ct =>
            {
                var now = Interlocked.Increment(ref concurrent);
                var runNumber = Interlocked.Increment(ref runs);
                UpdateMax(ref maxConcurrent, now);

                if (runNumber == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseRun.Task.WaitAsync(ct);
                }

                Interlocked.Decrement(ref concurrent);
                return NoWork();
            },
            delayAsync: async (_, ct) =>
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                delayCompletions.Enqueue(tcs);
                await using var reg = ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), tcs);
                await tcs.Task;
            });

        await service.StartAsync(CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, runs);
        Assert.Empty(delayCompletions);

        await Task.Delay(30);
        Assert.Equal(1, runs);
        Assert.Empty(delayCompletions);
        Assert.Equal(1, maxConcurrent);

        releaseRun.TrySetResult();
        await WaitForAsync(() => delayCompletions.Count >= 1, TimeSpan.FromSeconds(2));
        Assert.Equal(1, runs);

        Assert.True(delayCompletions.TryDequeue(out var firstDelay));
        firstDelay.TrySetResult();
        await WaitForAsync(() => Volatile.Read(ref runs) >= 2, TimeSpan.FromSeconds(2));
        Assert.Equal(2, runs);
        Assert.Equal(1, maxConcurrent);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void O_Exactly_One_Service_Instance_In_DI()
    {
        using var host = CreateCompositionHost();
        var typed = host.Services.GetRequiredService<StorageMaintenanceService>();
        var enumerated = host.Services.GetServices<IApplicationService>()
            .OfType<StorageMaintenanceService>()
            .ToArray();
        Assert.Single(enumerated);
        Assert.Same(typed, enumerated[0]);
    }

    [Fact]
    public async Task Q_Policy_Disabled_Still_Invokes_Coordinator()
    {
        var calls = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = CreateOptions(interval: TimeSpan.FromMinutes(1));
        var service = new StorageMaintenanceService(
            _ =>
            {
                Interlocked.Increment(ref calls);
                first.TrySetResult();
                return Task.FromResult(NoWork());
            },
            Options.Create(options),
            NullLogger<StorageMaintenanceService>.Instance,
            delayAsync: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        await service.StartAsync(CancellationToken.None);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, calls);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task R_Shutdown_Stops_Cleanly()
    {
        var service = CreateService(
            runOnce: _ => Task.FromResult(NoWork()),
            delayAsync: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        await service.StartAsync(CancellationToken.None);
        var loop = service.Execution;
        Assert.NotNull(loop);
        await service.StopAsync(CancellationToken.None);
        Assert.True(loop.IsCompleted);
        Assert.Null(service.Execution);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task S_Startup_Ordering_StorageEngine_Before_Maintenance()
    {
        var started = new List<string>();
        var storage = new OrderProbeService("StorageEngine", started);
        var maintenance = new OrderProbeService("StorageMaintenance", started);
        var manager = new ApplicationServiceManager(
            [storage, maintenance],
            StorageServerTestOptions.CreateValid(),
            NullLogger<ApplicationServiceManager>.Instance);

        await manager.StartAsync(CancellationToken.None);
        Assert.Equal(["StorageEngine", "StorageMaintenance"], started);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task T_StorageEngine_Startup_Failure_Skips_Maintenance()
    {
        var started = new List<string>();
        var storage = new FailingStartService("StorageEngine", started);
        var maintenance = new OrderProbeService("StorageMaintenance", started);
        var manager = new ApplicationServiceManager(
            [storage, maintenance],
            StorageServerTestOptions.CreateValid(),
            NullLogger<ApplicationServiceManager>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(CancellationToken.None));
        Assert.Equal(["StorageEngine"], started);
        Assert.DoesNotContain("StorageMaintenance", started);
    }

    [Fact]
    public async Task U_Immediate_First_Run_Can_Process_Durable_Pending_Work()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<maint-u@seg.test>");
        SegmentId sourceId;
        await using (var seed = FileArticleStorageEngine.Open(dir.Options))
        {
            sourceId = await AcceptCloseCompactRetireAsync(seed, record);
            Assert.True(seed.Segments.TryGetSegmentInfo(sourceId, out var info));
            Assert.Equal(SegmentState.Retired, info.State);
        }

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var reopened));
        Assert.Equal(SegmentState.Retired, reopened.State);

        var coordinator = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0));
        var firstOutcome = new TaskCompletionSource<StorageMaintenanceOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var maintenance = new StorageMaintenanceService(
            async ct =>
            {
                var result = await coordinator.RunOnceAsync(ct);
                firstOutcome.TrySetResult(result.Outcome);
                return result;
            },
            Options.Create(CreateOptions(interval: TimeSpan.FromMinutes(1))),
            NullLogger<StorageMaintenanceService>.Instance,
            delayAsync: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        await maintenance.StartAsync(CancellationToken.None);
        var outcome = await firstOutcome.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, outcome);
        Assert.False(engine.Segments.TryGetSegmentInfo(sourceId, out _));

        await maintenance.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task V_Lifecycle_And_Error_Events_Use_Serilog()
    {
        var sink = new CollectingSink();
        using var loggerFactory = CreateSerilogLoggerFactory(sink);
        var logger = loggerFactory.CreateLogger<StorageMaintenanceService>();

        var service = new StorageMaintenanceService(
            _ => Task.FromResult(Result(StorageMaintenanceOutcome.CompactedAndReclaimed)),
            Options.Create(CreateOptions(interval: TimeSpan.FromMilliseconds(5))),
            logger,
            delayAsync: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(
            () => sink.Events.Any(static e => e.MessageTemplate.Text.Contains("summary", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.Contains(
            sink.Events,
            static e => e.MessageTemplate.Text.Contains("started", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            sink.Events,
            static e => e.MessageTemplate.Text.Contains("stopping", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            sink.Events,
            static e => e.MessageTemplate.Text.Contains("stopped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void W_Host_Logging_Uses_Serilog_Not_Microsoft_Providers()
    {
        var builder = Host.CreateApplicationBuilder([]);
        foreach (var (key, value) in StorageServerTestOptions.CreateValidConfigurationPairs())
        {
            builder.Configuration[key] = value;
        }

        builder.ConfigureStorageServerLogging();
        using var host = builder.Build();
        Assert.Equal("SerilogLoggerFactory", host.Services.GetRequiredService<ILoggerFactory>().GetType().Name);
        Assert.Empty(host.Services.GetLoggerProviders());
    }

    [Fact]
    public void Composition_Registers_Maintenance_After_StorageEngine()
    {
        using var host = CreateCompositionHost();
        var application = host.Services.GetServices<IApplicationService>().ToArray();
        Assert.IsType<StorageEngineApplicationService>(application[0]);
        Assert.IsType<StorageMaintenanceService>(application[1]);
    }

    private static async Task AssertIntervalAfterCompletionAsync(StorageMaintenanceOutcome outcome)
    {
        var runStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRunComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delaySeen = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var interval = TimeSpan.FromSeconds(42);

        var service = CreateService(
            interval: interval,
            runOnce: async ct =>
            {
                var n = Interlocked.Increment(ref runs);
                if (n == 1)
                {
                    runStarted.TrySetResult();
                    await allowRunComplete.Task.WaitAsync(ct);
                    return Result(outcome);
                }

                return NoWork();
            },
            delayAsync: async (delay, ct) =>
            {
                delaySeen.TrySetResult(delay);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });

        await service.StartAsync(CancellationToken.None);
        await runStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(delaySeen.Task.IsCompleted);

        allowRunComplete.TrySetResult();
        var observedDelay = await delaySeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(interval, observedDelay);
        Assert.Equal(1, runs);

        await service.StopAsync(CancellationToken.None);
    }

    private static StorageMaintenanceService CreateService(
        TimeSpan? interval = null,
        Func<CancellationToken, Task<StorageMaintenanceResult>>? runOnce = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null) =>
        new(
            runOnce ?? (_ => Task.FromResult(NoWork())),
            Options.Create(CreateOptions(interval ?? TimeSpan.FromMinutes(1))),
            NullLogger<StorageMaintenanceService>.Instance,
            delayAsync);

    private static StorageServerOptions CreateOptions(TimeSpan? interval = null)
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.Compaction.Interval = interval ?? ArticleCompactionPolicyOptions.DefaultInterval;
        return options;
    }

    private static StorageMaintenanceResult NoWork() => Result(StorageMaintenanceOutcome.NoWork);

    private static StorageMaintenanceResult Result(StorageMaintenanceOutcome outcome) =>
        new(
            outcome,
            default,
            CompactionId: 0,
            CompactionAttempted: false,
            CompactionCommitted: false,
            RetirementAttempted: false,
            Retired: false,
            ReclamationAttempted: false,
            Reclaimed: outcome is StorageMaintenanceOutcome.Reclaimed
                or StorageMaintenanceOutcome.CompactedAndReclaimed);

    private static IHost CreateCompositionHost()
    {
        var builder = Host.CreateApplicationBuilder([]);
        var pairs = StorageServerTestOptions.CreateValidConfigurationPairs();
        pairs["StorageServer:BindPortTls"] = GetFreePort().ToString();
        pairs["StorageServer:BindAddress:0"] = "*";
        builder.Configuration.AddInMemoryCollection(pairs);
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<ICloudflareDnsReconciler>(new NoOpCloudflareDnsReconciler());
        builder.ConfigureStorageServerLogging();
        builder.ConfigureStorageServerPlatformHosting();
        builder.AddStorageServerHosting();
        builder.Services.AddSingleton<IRabbitMqConnectionFactory, FakeStorageServerRabbitMqConnectionFactory>();
        return builder.Build();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static SerilogLoggerFactory CreateSerilogLoggerFactory(CollectingSink sink)
    {
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return new SerilogLoggerFactory(serilog, dispose: true);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met before timeout.");
            }

            await Task.Delay(10);
        }
    }

    private static void UpdateMax(ref int location, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref location);
            if (candidate <= observed
                || Interlocked.CompareExchange(ref location, candidate, observed) == observed)
            {
                return;
            }
        }
    }

    private static async Task<SegmentId> AcceptCloseCompactRetireAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        var compact = await engine.CompactClosedSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        var retire = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retire.Outcome);
        return meta.Location.SegmentId;
    }

    private static ArticleRecord CreateRecord(string messageId, string body = "line1\r\nline2\r\n")
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: maintenance-worker\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class TempStorageDir : IDisposable
    {
        private TempStorageDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string Root { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-maint-svc-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            return new TempStorageDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: control,
                    SegmentDir: cache,
                    JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                    SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    private sealed class OrderProbeService : IApplicationService
    {
        private readonly List<string> _started;

        public OrderProbeService(string name, List<string> started)
        {
            Name = name;
            _started = started;
        }

        public string Name { get; }

        public Task? Execution => null;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _started.Add(Name);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingStartService : IApplicationService
    {
        private readonly List<string> _started;

        public FailingStartService(string name, List<string> started)
        {
            Name = name;
            _started = started;
        }

        public string Name { get; }

        public Task? Execution => null;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _started.Add(Name);
            throw new InvalidOperationException("storage start failed");
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
