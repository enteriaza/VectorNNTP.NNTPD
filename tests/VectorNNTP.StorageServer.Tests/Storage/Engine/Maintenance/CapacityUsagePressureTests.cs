using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.StorageServer.Tests.Fixtures;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Maintenance;

/// <summary>
/// Integer-percent usage pressure: trigger at MaximumUsageCapacity, recover FreeCapacity
/// percentage points of filesystem UsedBytes, and keep MaximumUtilization as the admission ceiling.
/// </summary>
public sealed class CapacityUsagePressureTests
{
    [Fact]
    public void Percent_trigger_and_recovery_target_use_filesystem_used_bytes()
    {
        const long total = 1_000;
        Assert.True(ProcessLocalCapacityLedger.IsUsageAtOrAbove(800, total, 80));
        Assert.True(ProcessLocalCapacityLedger.IsUsageAtOrAbove(801, total, 80));
        Assert.False(ProcessLocalCapacityLedger.IsUsageAtOrAbove(799, total, 80));
        Assert.Equal(750, ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 80 - 5));
        Assert.Equal(900, ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 90));
        Assert.Equal(1_000, ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 100));
        Assert.False(ProcessLocalCapacityLedger.WouldFit(900, 0, 0, total, 1, 90));
        Assert.True(ProcessLocalCapacityLedger.WouldFit(900, 0, 0, total, 1, 100));
    }

    [Fact]
    public void Invalid_percent_combinations_fail_validation_and_enabled_is_absent()
    {
        Assert.Null(typeof(ArticleCapacityOptions).GetProperty("Enabled"));

        var options = StorageServerTestOptions.CreateValid();
        Assert.False(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);

        options.Storage.Capacity.MaximumUsageCapacity = 90;
        options.Storage.Capacity.MaximumUtilization = 90;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);

        options = StorageServerTestOptions.CreateValid();
        options.Storage.Capacity.FreeCapacity = 90;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);

        options = StorageServerTestOptions.CreateValid();
        options.Storage.Capacity.CompactionHeadroom = 11;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);

        options = StorageServerTestOptions.CreateValid();
        options.Storage.Capacity.MaximumUtilization = 101;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageServer:Storage:Capacity:" + "Enabled"] = "false",
                ["StorageServer:Storage:Capacity:MaximumUtilization"] = "90",
                ["StorageServer:Storage:Capacity:MaximumUsageCapacity"] = "80",
                ["StorageServer:Storage:Capacity:FreeCapacity"] = "5",
                ["StorageServer:Storage:Capacity:CompactionHeadroom"] = "10",
            })
            .Build();
        var bound = new StorageServerOptions();
        configuration.GetSection(StorageServerOptions.SectionName).Bind(bound);
        Assert.Equal(90, bound.Storage.Capacity.MaximumUtilization);
        Assert.Equal(80, bound.Storage.Capacity.MaximumUsageCapacity);
        Assert.Null(typeof(ArticleCapacityOptions).GetProperty("Enabled"));
    }

    [Fact]
    public async Task Usage_below_trigger_does_not_latch_and_eighty_percent_does()
    {
        using var dir = TempStorageDir.Create();
        var reader = new DirectoryAwareCapacityReader(dir.Options.SegmentDir, total: 10_000, otherUsed: 0);
        await using var engine = Open(dir, reader);
        var record = CreateRecord("<cup-latch@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        var files = reader.Read().UsedBytes;
        reader.OtherUsed = 7_900 - files;
        var below = engine.ObserveCapacityAdmissionPressure();
        Assert.False(below.IsUnderUsagePressure);
        Assert.True(below.UsedBytes * 100 < below.TotalBytes * 80);

        reader.OtherUsed = 8_000 - files;
        var at = engine.ObserveCapacityAdmissionPressure();
        Assert.True(at.IsUnderUsagePressure);
        Assert.True(ProcessLocalCapacityLedger.IsUsageAtOrAbove(at.UsedBytes, at.TotalBytes, 80));
        Assert.Equal(7_500, at.UsageRecoveryTargetBytes);
    }

    [Fact]
    public async Task Pressure_evicts_least_frequently_used_without_freeing_used_bytes_until_reclaim()
    {
        using var dir = TempStorageDir.Create();
        var reader = new DirectoryAwareCapacityReader(dir.Options.SegmentDir, total: 1_000_000, otherUsed: 0);
        await using var engine = Open(dir, reader);
        var cold = CreateRecord("<cup-cold@seg.test>", body: string.Concat(Enumerable.Repeat(new string('c', 70) + "\r\n", 900)));
        var hot = CreateRecord("<cup-hot@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(cold, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(hot, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        _ = engine.CheckpointTruncateCommitted();
        _ = engine.CheckpointIndex();
        Assert.True(engine.TryRead(hot.ArtId, out _));
        Assert.True(engine.Index.UseCount(hot.ArtId) > engine.Index.UseCount(cold.ArtId));

        var files = reader.Read().UsedBytes;
        reader.OtherUsed = Math.Max(0, 800_000 - files);
        var before = engine.ObserveCapacityAdmissionPressure();
        Assert.True(before.IsUnderUsagePressure);
        var reserved = engine.ProcessLocalArticleReservedBytes;
        Assert.Equal(0, reserved);

        var evicted = engine.EvictLeastFrequentlyUsed(1);
        Assert.Equal(1, evicted);
        Assert.Equal(ArticleStorageState.Evicted, State(engine, cold.ArtId));
        Assert.Equal(ArticleStorageState.Present, State(engine, hot.ArtId));
        var afterEvict = reader.Read().UsedBytes;
        Assert.Equal(before.UsedBytes, afterEvict);
        Assert.True(engine.ObserveCapacityAdmissionPressure().IsUnderUsagePressure);
        Assert.Equal(reserved, engine.ProcessLocalArticleReservedBytes);

        var coordinator = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0));
        var recovered = await coordinator.RunUsagePressureRecoveryAsync(CancellationToken.None);
        var after = engine.ObserveCapacityAdmissionPressure();
        Assert.True(after.UsedBytes < before.UsedBytes);
        Assert.False(after.IsUnderUsagePressure);
        Assert.True(after.UsedBytes <= after.UsageRecoveryTargetBytes);
        Assert.Equal(ArticleStorageState.Present, State(engine, hot.ArtId));
    }

    [Fact]
    public async Task Worker_RunOnce_recovers_pressure_with_ordinary_compaction_disabled()
    {
        Assert.Null(typeof(ArticleCompactionPolicyOptions).GetProperty("Maintenance" + "Enabled"));
        using var dir = TempStorageDir.Create();
        var reader = new DirectoryAwareCapacityReader(dir.Options.SegmentDir, total: 1_000_000, otherUsed: 0);
        await using var engine = Open(dir, reader);
        var cold = CreateRecord("<cup-worker-cold@seg.test>", body: string.Concat(Enumerable.Repeat(new string('c', 70) + "\r\n", 900)));
        var hot = CreateRecord("<cup-worker-hot@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(cold, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(hot, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        _ = engine.CheckpointTruncateCommitted();
        _ = engine.CheckpointIndex();
        Assert.True(engine.TryRead(hot.ArtId, out _));

        var files = reader.Read().UsedBytes;
        reader.OtherUsed = Math.Max(0, 800_000 - files);
        Assert.True(engine.ObserveCapacityAdmissionPressure().IsUnderUsagePressure);

        var coordinator = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: long.MaxValue / 4, minimumDeadRatio: 100));
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = StorageServerTestOptions.CreateValid();
        var service = new StorageMaintenanceService(
            async ct =>
            {
                var result = await coordinator.RunOnceAsync(ct);
                finished.TrySetResult();
                return result;
            },
            Options.Create(options),
            NullLogger<StorageMaintenanceService>.Instance,
            delayAsync: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        await service.StartAsync(CancellationToken.None);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var after = engine.ObserveCapacityAdmissionPressure();
        Assert.True(after.UsedBytes <= after.UsageRecoveryTargetBytes);
        Assert.False(after.IsUnderUsagePressure);
        Assert.Equal(ArticleStorageState.Evicted, State(engine, cold.ArtId));
        Assert.Equal(ArticleStorageState.Present, State(engine, hot.ArtId));
        await service.StopAsync(CancellationToken.None);
        Assert.Null(service.Execution);
    }

    [Fact]
    public async Task Eighty_percent_does_not_reject_and_ninety_percent_admits_after_recovery()
    {
        using var dir = TempStorageDir.Create();
        var reader = new DirectoryAwareCapacityReader(dir.Options.SegmentDir, total: 20_000, otherUsed: 0);
        await using var engine = Open(dir, reader);
        var resident = CreateRecord("<cup-resident@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(resident, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var files = reader.Read().UsedBytes;
        reader.OtherUsed = 16_000 - files;
        Assert.True(engine.ObserveCapacityAdmissionPressure().IsUnderUsagePressure);

        var coordinator = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: long.MaxValue / 4, minimumDeadRatio: 100));
        var duringPressure = CreateRecord("<cup-during@seg.test>");
        var accepted = await engine.AcceptAsync(duringPressure, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(ArticleStorageState.Present, State(engine, duringPressure.ArtId));

        reader.OtherUsed = 19_000;
        var blocked = CreateRecord("<cup-blocked@seg.test>", body: new string('x', 400) + "\r\n");
        var rejected = await engine.AcceptAsync(blocked, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, rejected.Outcome);
        Assert.False(engine.Index.TryGet(blocked.ArtId, out _));
        _ = coordinator;
    }

    [Fact]
    public async Task Restart_preserves_evicted_state()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 1_000_000, used: 0);
        ArticleId artId;
        await using (var engine = Open(dir, reader))
        {
            var record = CreateRecord("<cup-restart@seg.test>");
            artId = record.ArtId;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.TryEvict(artId));
            Assert.Equal(ArticleStorageState.Evicted, State(engine, artId));
        }

        await using var reopened = Open(dir, reader);
        Assert.Equal(ArticleStorageState.Evicted, State(reopened, artId));
        Assert.Equal(0, reopened.Index.UseCount(artId));
    }

    [Fact]
    public void Compaction_headroom_remains_above_the_admission_ceiling()
    {
        const long total = 1_000;
        Assert.False(ProcessLocalCapacityLedger.WouldFit(900, 0, 0, total, 50, 90));
        Assert.True(ProcessLocalCapacityLedger.WouldFit(900, 0, 0, total, 50, 100));
        var snapshot = CapacityAdmissionPressureSnapshot.FromCapacityState(
            new StorageCapacitySnapshot(total, 900, 100),
            articleReservedBytes: 0,
            compactionReservedBytes: 0,
            maximumUtilization: 90,
            compactionHeadroom: 10,
            maximumUsageCapacity: 80,
            freeCapacity: 5);
        var segment = new SegmentInfo(
            new SegmentId(1),
            SegmentState.Closed,
            Generation: 1,
            SizeBytes: 80,
            LiveBytes: 50,
            DeadBytes: 30,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            ClosedUtc: DateTimeOffset.UnixEpoch,
            ExtentAccountingComplete: true);
        Assert.True(ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(in segment, in snapshot));
        var tooLive = segment with { LiveBytes = 200, DeadBytes = 0, SizeBytes = 200 };
        Assert.False(ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(in tooLive, in snapshot));
    }

    private static FileArticleStorageEngine Open(TempStorageDir dir, IStorageCapacityReader reader)
    {
        var options = dir.Options with
        {
            CapacityMaximumUtilization = 90,
            CapacityCompactionHeadroom = 10,
            CapacityMaximumUsageCapacity = 80,
            CapacityFreeCapacity = 5,
        };
        return FileArticleStorageEngine.Open(options, capacityReader: reader);
    }

    private static ArticleStorageState State(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var metadata));
        return metadata.State;
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
        _ = builder.Append("Subject: pressure\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class MutableCapacityReader(long total, long used) : IStorageCapacityReader
    {
        public long TotalBytes { get; } = total;

        public long UsedBytes { get; set; } = used;

        public StorageCapacitySnapshot Read() =>
            new(TotalBytes, UsedBytes, Math.Max(0L, TotalBytes - UsedBytes));
    }

    private sealed class DirectoryAwareCapacityReader : IStorageCapacityReader
    {
        private readonly string _segmentDir;

        public DirectoryAwareCapacityReader(string segmentDir, long total, long otherUsed)
        {
            _segmentDir = segmentDir;
            TotalBytes = total;
            OtherUsed = otherUsed;
        }

        public long TotalBytes { get; }

        public long OtherUsed { get; set; }

        public StorageCapacitySnapshot Read()
        {
            long fileBytes = 0;
            if (Directory.Exists(_segmentDir))
            {
                foreach (var path in Directory.EnumerateFiles(_segmentDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        fileBytes = checked(fileBytes + new FileInfo(path).Length);
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            var used = Math.Min(TotalBytes, checked(OtherUsed + fileBytes));
            return new StorageCapacitySnapshot(TotalBytes, used, Math.Max(0L, TotalBytes - used));
        }
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-cup-" + Guid.NewGuid().ToString("N"));
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
            catch (IOException)
            {
            }
        }
    }
}
