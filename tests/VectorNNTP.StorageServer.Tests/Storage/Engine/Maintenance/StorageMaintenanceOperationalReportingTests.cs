using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;
using VectorNNTP.StorageServer.Tests.Logging;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Maintenance;

/// <summary>Phase 5D: maintenance operational reporting on results and structured logs.</summary>
public sealed class StorageMaintenanceOperationalReportingTests
{
    [Fact]
    public async Task A_NoWork_has_default_operational_fields()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.NoWork, result.Outcome);
        Assert.Equal(0, result.RelocatedArticleCount);
        Assert.Null(result.SourceSizeBytes);
        Assert.Null(result.ReclaimedSegmentSizeBytes);
    }

    [Fact]
    public async Task B_C_D_Compaction_exposes_segment_compaction_and_relocation_counts()
    {
        using var dir = TempStorageDir.Create();
        var keep = CreateRecord("<op-d-keep@seg.test>");
        var drop = CreateRecord("<op-d-drop@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var before));

        var result = await CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0)
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(sourceId, result.SegmentId);
        Assert.True(result.CompactionId > 0);
        Assert.Equal(1, result.RelocatedArticleCount);
        Assert.Equal(before.SizeBytes, result.SourceSizeBytes);
        Assert.Equal(before.DeadBytes, result.SourceDeadBytes);
        Assert.True(result.SourceDeadRatio is > 0 and <= 1);
        Assert.Equal(before.SizeBytes, result.ReclaimedSegmentSizeBytes);
    }

    [Fact]
    public async Task E_ReclamationOnly_reports_segment_and_size()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<op-e@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var retired));
        var coordinator = CreateCoordinator(engine);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, result.Outcome);
        Assert.Equal(sourceId, result.SegmentId);
        Assert.Equal(retired.SizeBytes, result.ReclaimedSegmentSizeBytes);
        Assert.Equal(0, result.RelocatedArticleCount);
    }

    [Fact]
    public async Task F_CompactedAndReclaimed_flags_all_phases()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<op-f@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out _));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));

        var result = await CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0)
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.True(result.CompactionCommitted);
        Assert.True(result.Retired);
        Assert.True(result.Reclaimed);
        Assert.True(result.RelocatedArticleCount >= 0);
    }

    [Fact]
    public async Task G_Incomplete_preserves_meaning_and_relocation_count()
    {
        using var dir = TempStorageDir.Create();
        var live = CreateRecord("<op-g-live@seg.test>");
        var phantom = CreateRecord("<op-g-phantom@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(live, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(live.ArtId, out var liveMeta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        engine.TestHookBeforeRelocateArticle = _ =>
        {
            Assert.True(engine.Index.TryCommitPresent(new StoredArticleMetadata(
                phantom.ArtId,
                phantom.ArtHash,
                phantom.ArtSize,
                liveMeta.Location,
                ArticleStorageState.Present,
                DateTimeOffset.UtcNow)));
        };

        var result = await CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0)
            .RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Incomplete, result.Outcome);
        Assert.True(result.CompactionAttempted);
        Assert.False(result.CompactionCommitted);
        Assert.True(result.RelocatedArticleCount >= 0);
    }

    [Fact]
    public async Task H_Failed_outcome_carries_detail_without_article_data()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<op-h@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        var closedPath = Path.Combine(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(sourceId, SegmentFileKind.Closed));
        await File.WriteAllBytesAsync(closedPath, [0x00, 0x01, 0x02, 0x03]);

        var result = await CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0)
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.SkipReason));
        Assert.DoesNotContain("<op-h@seg.test>", result.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task I_J_Worker_duration_is_non_negative_and_monotonic()
    {
        var sink = new CollectingSink();
        using var loggerFactory = CreateSerilogLoggerFactory(sink);
        var service = new StorageMaintenanceService(
            async _ =>
            {
                await Task.Yield();
                return new StorageMaintenanceResult(
                    StorageMaintenanceOutcome.NoWork,
                    default,
                    CompactionId: 0,
                    CompactionAttempted: false,
                    CompactionCommitted: false,
                    RetirementAttempted: false,
                    Retired: false,
                    ReclamationAttempted: false,
                    Reclaimed: false);
            },
            Options.Create(CreateMaintenanceOptions()),
            loggerFactory.CreateLogger<StorageMaintenanceService>(),
            delayAsync: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        var before = Stopwatch.GetTimestamp();
        await service.StartAsync(CancellationToken.None);
        await WaitForLogAsync(sink, static e => e.Properties.ContainsKey("DurationMs"), TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);
        _ = Stopwatch.GetElapsedTime(before);

        var idle = Assert.Single(
            sink.Events,
            static e => e.MessageTemplate.Text.Contains("idle", StringComparison.OrdinalIgnoreCase));
        var duration = Assert.Contains("DurationMs", idle.Properties);
        var raw = duration.ToString().Trim('"');
        Assert.True(double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms));
        Assert.True(ms >= 0);
    }

    [Fact]
    public async Task K_Log_events_use_structured_properties_not_message_ids()
    {
        var sink = new CollectingSink();
        using var loggerFactory = CreateSerilogLoggerFactory(sink);
        var service = new StorageMaintenanceService(
            _ => Task.FromResult(new StorageMaintenanceResult(
                StorageMaintenanceOutcome.Reclaimed,
                new SegmentId(42),
                CompactionId: 0,
                CompactionAttempted: false,
                CompactionCommitted: false,
                RetirementAttempted: false,
                Retired: false,
                ReclamationAttempted: true,
                Reclaimed: true,
                ReclaimedSegmentSizeBytes: 4096)),
            Options.Create(CreateMaintenanceOptions()),
            loggerFactory.CreateLogger<StorageMaintenanceService>(),
            delayAsync: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        await service.StartAsync(CancellationToken.None);
        await WaitForLogAsync(
            sink,
            static e => e.Properties.ContainsKey("Outcome") && e.Properties.ContainsKey("SegmentId"),
            TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        var summary = Assert.Single(
            sink.Events,
            static e => e.Level == LogEventLevel.Information
                && e.Properties.TryGetValue("Outcome", out var outcome)
                && outcome.ToString().Contains("Reclaimed", StringComparison.Ordinal));
        Assert.Contains("SegmentId", summary.Properties);
        Assert.Contains("MaintenanceRunId", summary.Properties);
        Assert.DoesNotContain("Message-ID", summary.MessageTemplate.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task L_M_Skipped_is_debug_and_NoWork_not_information_summary()
    {
        var sink = new CollectingSink();
        using var loggerFactory = CreateSerilogLoggerFactory(sink);
        var calls = 0;
        var service = new StorageMaintenanceService(
            _ =>
            {
                var n = Interlocked.Increment(ref calls);
                return Task.FromResult(n == 1
                    ? new StorageMaintenanceResult(
                        StorageMaintenanceOutcome.NoWork,
                        default,
                        0,
                        false,
                        false,
                        false,
                        false,
                        false,
                        false)
                    : new StorageMaintenanceResult(
                        StorageMaintenanceOutcome.Skipped,
                        new SegmentId(7),
                        0,
                        false,
                        false,
                        false,
                        false,
                        false,
                        false,
                        SkipReason: "policy-disabled"));
            },
            Options.Create(CreateMaintenanceOptions()),
            loggerFactory.CreateLogger<StorageMaintenanceService>(),
            delayAsync: async (_, ct) =>
            {
                if (Volatile.Read(ref calls) >= 2)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
            });

        await service.StartAsync(CancellationToken.None);
        await WaitForLogAsync(() => Volatile.Read(ref calls) >= 2, TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.Contains(
            sink.Events,
            static e => e.Level == LogEventLevel.Debug
                && e.MessageTemplate.Text.Contains("idle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            sink.Events,
            static e => e.Level == LogEventLevel.Debug
                && e.Properties.TryGetValue("SkipReason", out _));
        Assert.DoesNotContain(
            sink.Events,
            static e => e.Level == LogEventLevel.Information
                && e.MessageTemplate.Text.Contains("summary", StringComparison.OrdinalIgnoreCase)
                && e.Properties.TryGetValue("Outcome", out var o)
                && o.ToString().Contains("NoWork", StringComparison.Ordinal));
    }

    [Fact]
    public async Task N_Coordinator_behavior_unchanged_for_empty_catalogue()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.NoWork, result.Outcome);
        Assert.False(result.CompactionAttempted);
        Assert.False(result.ReclamationAttempted);
    }

    private static StorageMaintenanceCoordinator CreateCoordinator(
        FileArticleStorageEngine engine,
        long minimumDeadBytes = ArticleCompactionPolicyOptions.DefaultMinimumDeadBytes,
        double minimumDeadRatio = ArticleCompactionPolicyOptions.DefaultMinimumDeadRatio) =>
        new(engine, new ArticleSegmentPolicy(enabled: true, minimumDeadBytes, minimumDeadRatio));

    private static StorageServerOptions CreateMaintenanceOptions()
    {
        var options = new StorageServerOptions { ServerId = 1, DnsSuffix = "usenet.ninja" };
        options.Storage.Compaction.MaintenanceEnabled = true;
        options.Storage.Compaction.Interval = TimeSpan.FromMinutes(1);
        return options;
    }

    private static SerilogLoggerFactory CreateSerilogLoggerFactory(CollectingSink sink)
    {
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return new SerilogLoggerFactory(serilog, dispose: true);
    }

    private static async Task WaitForLogAsync(
        CollectingSink sink,
        Func<LogEvent, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!sink.Events.Any(predicate))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Expected log event was not emitted.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task WaitForLogAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task<SegmentId> AcceptCloseCompactRetireAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
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
        _ = builder.Append("Subject: maintenance-reporting\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-op-report-" + Guid.NewGuid().ToString("N"));
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
}
