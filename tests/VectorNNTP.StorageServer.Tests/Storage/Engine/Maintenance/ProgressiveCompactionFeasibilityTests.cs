using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Maintenance;

/// <summary>Phase 5F.5: progressive compaction feasibility under admission pressure.</summary>
public sealed class ProgressiveCompactionFeasibilityTests
{
    [Fact]
    public void T1_Full_LiveBytes_infeasible_minimum_record_feasible()
    {
        // Compaction ceiling 0.60×10_000_000 = 6_000_000.
        // Used 5_500_000 → admission pressure (MaxUtil 0.50) but room for one min record under headroom.
        // LiveBytes 1_000_000 would not fit as a single claim.
        const long total = 10_000_000;
        const long used = 5_500_000;
        const double maxUtil = 0.50;
        const double headroom = 0.10;

        var segment = Seg(1, size: 2_000_000, live: 1_000_000, dead: 1_000_000);
        var pressure = CapacityAdmissionPressureSnapshot.FromCapacityState(
            new StorageCapacitySnapshot(total, used, total - used),
            articleReservedBytes: 0,
            compactionReservedBytes: 0,
            maximumUtilization: maxUtil,
            compactionHeadroom: headroom);

        Assert.True(pressure.IsUnderAdmissionPressure);
        Assert.False(
            ArticleSegmentPolicy.WouldEntireLiveBytesFitUnderHeadroom(in segment, in pressure));
        Assert.True(
            ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(in segment, in pressure));

        var policy = new ArticleSegmentPolicy(enabled: true, minimumDeadBytes: 0, minimumDeadRatio: 0);
        Assert.True(policy.TrySelectPressureReliefCompactionVictim([segment], in pressure, out var victim));
        Assert.Equal(segment.SegmentId, victim.SegmentId);
    }

    [Fact]
    public async Task T1b_Maintenance_attempts_Closed_when_only_progressive_feasible()
    {
        using var dir = TempStorageDir.Create();
        // MaxUtil 0.50 → 5_000_000; MaxUtil+Headroom 0.55 → 5_500_000.
        const long total = 10_000_000;
        var capacity = new MutableCapacityReader(total, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.50, compactionHeadroom: 0.05),
            capacityReader: capacity);

        // Enough live articles that LiveBytes exceeds remaining headroom at Used near the
        // compaction ceiling, while MinimumRecordLength still fits.
        var records = Enumerable.Range(0, 50)
            .Select(i => CreateRecord($"<p5f5-t1-{i}@seg.test>", body: new string('x', 800) + "\r\n"))
            .ToArray();
        foreach (var r in records)
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(r, CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(records[0].ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(records[^1].ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var closed));
        Assert.True(closed.LiveBytes > 0);
        Assert.True(closed.DeadBytes > 0);

        // Compaction ceiling 5_500_000; leave < LiveBytes but ≥ MinimumRecordLength.
        capacity.UsedBytes = 5_490_000;
        var pressure = engine.ObserveCapacityAdmissionPressure();
        Assert.True(pressure.IsUnderAdmissionPressure);
        Assert.True(closed.LiveBytes > pressure.MinimumAdmissionRequiredBytes);
        Assert.True(closed.LiveBytes > pressure.CompactionCeilingBytes - pressure.UsedBytes);
        Assert.False(
            ArticleSegmentPolicy.WouldEntireLiveBytesFitUnderHeadroom(in closed, in pressure));
        Assert.True(
            ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(in closed, in pressure));

        SegmentId? selected = null;
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterCompactionVictimSelected = id => selected = id;

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(sourceId, selected);
        Assert.NotEqual(
            StorageMaintenanceSkipReasons.CapacityPressureNoFeasibleCandidate,
            result.SkipReason);
        Assert.True(result.CompactionAttempted);
        Assert.True(
            result.Outcome is StorageMaintenanceOutcome.Incomplete
                or StorageMaintenanceOutcome.Compacted
                or StorageMaintenanceOutcome.CompactedAndReclaimed
                or StorageMaintenanceOutcome.Retired);
    }

    [Fact]
    public async Task T2_Minimum_record_also_infeasible_preserves_no_feasible_candidate()
    {
        using var dir = TempStorageDir.Create();
        var keep = CreateRecord("<p5f5-t2-keep@seg.test>");
        var drop = CreateRecord("<p5f5-t2-drop@seg.test>");
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.50, compactionHeadroom: 0.05),
            capacityReader: capacity);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var before));

        capacity.UsedBytes = capacity.TotalBytes;
        var pressure = engine.ObserveCapacityAdmissionPressure();
        Assert.True(pressure.IsUnderAdmissionPressure);
        Assert.False(ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(in before, in pressure));
        Assert.False(ArticleSegmentPolicy.WouldEntireLiveBytesFitUnderHeadroom(in before, in pressure));

        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Skipped, result.Outcome);
        Assert.Equal(
            StorageMaintenanceSkipReasons.CapacityPressureNoFeasibleCandidate,
            result.SkipReason);
        Assert.False(result.CompactionAttempted);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out _));
    }

    [Fact]
    public async Task T3_Zero_live_Closed_remains_pressure_feasible()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.50, compactionHeadroom: 0.05),
            capacityReader: capacity);

        var keep = CreateRecord("<p5f5-t3-keep@seg.test>");
        var drop = CreateRecord("<p5f5-t3-drop@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(keep.ArtId));
        Assert.True(engine.TryEvict(drop.ArtId));
        engine.RebuildSegmentAccountingFromIndex();
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var closed));
        Assert.Equal(0, closed.LiveBytes);
        Assert.True(closed.DeadBytes > 0);

        capacity.UsedBytes = capacity.TotalBytes;
        var pressure = engine.ObserveCapacityAdmissionPressure();
        Assert.True(pressure.IsUnderAdmissionPressure);
        Assert.True(ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(in closed, in pressure));

        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(sourceId, result.SegmentId);
        Assert.False(engine.Segments.TryGetSegmentInfo(sourceId, out _));
    }

    [Fact]
    public async Task T4_Non_pressure_dead_ratio_selection_unchanged()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.90, compactionHeadroom: 0.05),
            capacityReader: capacity);

        // S1: low dead ratio (1 of 3 dead).
        var s1 = Enumerable.Range(0, 3)
            .Select(i => CreateRecord($"<p5f5-t4-s1-{i}@seg.test>"))
            .ToArray();
        foreach (var r in s1)
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(r, CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(s1[0].ArtId, out var s1Meta));
        var s1Id = s1Meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(s1[^1].ArtId));

        // S2: high dead ratio (both dead) — normal policy prefers this.
        var keep = CreateRecord("<p5f5-t4-s2-keep@seg.test>");
        var drop = CreateRecord("<p5f5-t4-s2-drop@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var s2Meta));
        var s2Id = s2Meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(keep.ArtId));
        Assert.True(engine.TryEvict(drop.ArtId));
        engine.RebuildSegmentAccountingFromIndex();

        capacity.UsedBytes = 0;
        Assert.False(engine.ObserveCapacityAdmissionPressure().IsUnderAdmissionPressure);

        SegmentId? selected = null;
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterCompactionVictimSelected = id => selected = id;

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(s2Id, selected);
        Assert.Equal(s2Id, result.SegmentId);
        Assert.NotEqual(s1Id, result.SegmentId);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
    }

    private static SegmentInfo Seg(ulong id, long size, long live, long dead) =>
        new(
            new SegmentId(id),
            SegmentState.Closed,
            Generation: id,
            SizeBytes: size,
            LiveBytes: live,
            DeadBytes: dead,
            CreatedUtc: new DateTimeOffset(2024, 8, 23, 7, 30, 10, TimeSpan.Zero),
            ClosedUtc: new DateTimeOffset(2024, 8, 23, 8, 0, 0, TimeSpan.Zero));

    private static StorageMaintenanceCoordinator CreateCoordinator(FileArticleStorageEngine engine) =>
        new(engine, new ArticleSegmentPolicy(enabled: true, minimumDeadBytes: 0, minimumDeadRatio: 0));

    private static ArticleStorageRuntimeOptions WithCapacity(
        ArticleStorageRuntimeOptions options,
        double maximumUtilization,
        double compactionHeadroom) =>
        options with
        {
            CapacityAdmissionEnabled = true,
            CapacityMaximumUtilization = maximumUtilization,
            CapacityCompactionHeadroom = compactionHeadroom,
        };

    private static ArticleRecord CreateRecord(string messageId, string body = "line1\r\nline2\r\n")
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: progressive-feasibility\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class MutableCapacityReader : IStorageCapacityReader
    {
        public MutableCapacityReader(long total, long used)
        {
            TotalBytes = total;
            UsedBytes = used;
        }

        public long TotalBytes { get; set; }

        public long UsedBytes { get; set; }

        public StorageCapacitySnapshot Read() =>
            new(TotalBytes, UsedBytes, Math.Max(0L, TotalBytes - UsedBytes));
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-p5f5-" + Guid.NewGuid().ToString("N"));
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
