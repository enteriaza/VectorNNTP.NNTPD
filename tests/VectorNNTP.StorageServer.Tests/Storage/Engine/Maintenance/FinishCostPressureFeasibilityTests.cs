using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Maintenance;

/// <summary>Phase 5F.9: pressure compaction starts only when full LiveBytes fits.</summary>
public sealed class FinishCostPressureFeasibilityTests
{
    [Fact]
    public async Task A_Full_LiveBytes_does_not_fit_does_not_start()
    {
        using var dir = TempStorageDir.Create();
        const long total = 10_000_000;
        var capacity = new MutableCapacityReader(total, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 10),
            capacityReader: capacity);

        var records = Enumerable.Range(0, 4)
            .Select(i => CreateRecord($"<p5f9-a-{i}@seg.test>", body: new string('x', 400) + "\r\n"))
            .ToArray();
        foreach (var record in records)
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(records[0].ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(records[^1].ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var before));
        Assert.True(before.LiveBytes > SegmentRecordCodec.MinimumRecordLength);

        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 60);
        var segmentCopies = engine.ProcessLocalArticleReservedBytes;
        capacity.UsedBytes = ceiling - SegmentRecordCodec.MinimumRecordLength - segmentCopies;
        Assert.True(capacity.UsedBytes >= 0);
        var pressure = engine.ObserveCapacityAdmissionPressure();
        Assert.True(pressure.IsUnderAdmissionPressure);
        Assert.True(
            ProcessLocalCapacityLedger.WouldFit(
                pressure.UsedBytes,
                pressure.ArticleReservedBytes,
                pressure.CompactionReservedBytes,
                pressure.TotalBytes,
                SegmentRecordCodec.MinimumRecordLength,
                pressure.MaximumUtilization + pressure.CompactionHeadroom));
        Assert.False(ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(in before, in pressure));
        Assert.Empty(engine.Journal.EnumerateOpenCompactions());

        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Skipped, result.Outcome);
        Assert.Equal(
            StorageMaintenanceSkipReasons.CapacityPressureNoFeasibleCandidate,
            result.SkipReason);
        Assert.False(result.CompactionAttempted);
        Assert.Empty(engine.Journal.EnumerateOpenCompactions());
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var after));
        Assert.Equal(before.SizeBytes, after.SizeBytes);
        Assert.Equal(before.LiveBytes, after.LiveBytes);
        Assert.Equal(before.DeadBytes, after.DeadBytes);
        Assert.Equal(SegmentState.Closed, after.State);
    }

    [Fact]
    public async Task B_Full_LiveBytes_fits_and_reclaims()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 40),
            capacityReader: capacity);

        var keep = CreateRecord("<p5f9-b-keep@seg.test>");
        var drop = CreateRecord("<p5f9-b-drop@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var closed));

        capacity.UsedBytes = 5_500_000;
        var pressure = engine.ObserveCapacityAdmissionPressure();
        Assert.True(pressure.IsUnderAdmissionPressure);
        Assert.True(ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(in closed, in pressure));

        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(sourceId, result.SegmentId);
        Assert.True(result.Reclaimed);
        Assert.False(engine.Segments.TryGetSegmentInfo(sourceId, out _));
        Assert.True(engine.TryRead(keep.ArtId, out _));
    }

    [Fact]
    public async Task C_Zero_live_remains_feasible_without_destination_slack()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 5),
            capacityReader: capacity);

        var keep = CreateRecord("<p5f9-c-keep@seg.test>");
        var drop = CreateRecord("<p5f9-c-drop@seg.test>");
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
        Assert.True(closed.SizeBytes > 0);

        var journalFrames = ArticleJournalFrameCodec.CompactionBeginFrameLength
            + ArticleJournalFrameCodec.CompactionCommittedFrameLength
            + ArticleJournalFrameCodec.CompactionRetiredFrameLength;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(
            capacity.TotalBytes,
            55);
        capacity.UsedBytes = ceiling - engine.ProcessLocalReservedBytes - journalFrames;
        var pressure = engine.ObserveCapacityAdmissionPressure();
        Assert.True(pressure.IsUnderAdmissionPressure);
        var articleBytes = SegmentRecordCodec.RecordLengthForArtSize(keep.ArtSize);
        Assert.True(articleBytes > journalFrames);
        Assert.False(
            ProcessLocalCapacityLedger.WouldFit(
                pressure.UsedBytes,
                pressure.ArticleReservedBytes,
                pressure.CompactionReservedBytes,
                pressure.TotalBytes,
                articleBytes,
                pressure.MaximumUtilization + pressure.CompactionHeadroom,
                pressure.CheckpointReservedBytes,
                pressure.JournalReservedBytes,
                pressure.IndexReservedBytes,
                pressure.CompactionJournalReservedBytes));
        Assert.True(
            ProcessLocalCapacityLedger.WouldFit(
                pressure.UsedBytes,
                pressure.ArticleReservedBytes,
                pressure.CompactionReservedBytes,
                pressure.TotalBytes,
                journalFrames,
                pressure.MaximumUtilization + pressure.CompactionHeadroom,
                pressure.CheckpointReservedBytes,
                pressure.JournalReservedBytes,
                pressure.IndexReservedBytes,
                pressure.CompactionJournalReservedBytes));
        Assert.True(ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(in closed, in pressure));

        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(sourceId, result.SegmentId);
        Assert.False(engine.Segments.TryGetSegmentInfo(sourceId, out _));
    }

    [Fact]
    public async Task D_Largest_article_fits_but_total_LiveBytes_does_not()
    {
        using var dir = TempStorageDir.Create();
        const long total = 10_000_000;
        var capacity = new MutableCapacityReader(total, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 10),
            capacityReader: capacity);

        var payload = string.Concat(Enumerable.Repeat("0123456789abcdef\r\n", 500));
        var first = CreateRecord("<p5f9-d-a@seg.test>", body: payload);
        var second = CreateRecord("<p5f9-d-b@seg.test>", body: payload);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(second, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(first.ArtId, out var firstMeta));
        Assert.True(engine.Index.TryGet(second.ArtId, out var secondMeta));
        var sourceId = firstMeta.Location.SegmentId;
        Assert.Equal(sourceId, secondMeta.Location.SegmentId);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var before));

        var larger = Math.Max(firstMeta.Location.Length, secondMeta.Location.Length);
        Assert.True(before.LiveBytes > larger);
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 60);
        capacity.UsedBytes = ceiling - larger - 64;
        var pressure = engine.ObserveCapacityAdmissionPressure();
        Assert.True(pressure.IsUnderAdmissionPressure);
        Assert.True(
            ProcessLocalCapacityLedger.WouldFit(
                pressure.UsedBytes,
                0,
                0,
                total,
                larger,
                60));
        Assert.False(ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(in before, in pressure));

        var opensBefore = engine.Journal.EnumerateOpenCompactions().Count();
        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Skipped, result.Outcome);
        Assert.Equal(
            StorageMaintenanceSkipReasons.CapacityPressureNoFeasibleCandidate,
            result.SkipReason);
        Assert.False(result.CompactionAttempted);
        Assert.Equal(opensBefore, engine.Journal.EnumerateOpenCompactions().Count());
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var after));
        Assert.Equal(before.LiveBytes, after.LiveBytes);
        Assert.Equal(before.DeadBytes, after.DeadBytes);
        Assert.Equal(before.SizeBytes, after.SizeBytes);
    }

    [Fact]
    public async Task E_Non_pressure_dead_ratio_selection_unchanged()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 90, compactionHeadroom: 5),
            capacityReader: capacity);

        var lowDead = Enumerable.Range(0, 3)
            .Select(i => CreateRecord($"<p5f9-e-low-{i}@seg.test>"))
            .ToArray();
        foreach (var record in lowDead)
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(lowDead[0].ArtId, out var lowMeta));
        var lowId = lowMeta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(lowDead[^1].ArtId));

        var keep = CreateRecord("<p5f9-e-high-keep@seg.test>");
        var drop = CreateRecord("<p5f9-e-high-drop@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var highMeta));
        var highId = highMeta.Location.SegmentId;
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

        Assert.Equal(highId, selected);
        Assert.Equal(highId, result.SegmentId);
        Assert.NotEqual(lowId, result.SegmentId);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
    }

    private static StorageMaintenanceCoordinator CreateCoordinator(FileArticleStorageEngine engine) =>
        new(engine, new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0));

    private static ArticleStorageRuntimeOptions WithCapacity(
        ArticleStorageRuntimeOptions options,
        int maximumUtilization,
        int compactionHeadroom) =>
        options with
        {
            CapacityMaximumUtilization = maximumUtilization,
            CapacityCompactionHeadroom = compactionHeadroom,
            CapacityMaximumUsageCapacity = 100,
            CapacityFreeCapacity = 1,
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
        _ = builder.Append("Subject: finish-cost\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-p5f9-" + Guid.NewGuid().ToString("N"));
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
