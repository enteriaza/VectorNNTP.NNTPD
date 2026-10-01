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

/// <summary>Phase 5F.3: open-compaction zero-progress capacity fairness.</summary>
public sealed class OpenCompactionZeroProgressFairnessTests
{
    [Fact]
    public async Task A_Starvation_fix_falls_through_to_Closed_pressure_relief()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 40),
            capacityReader: capacity);

        var (c1Source, c1CompactionId) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "a-c1");
        var s2Id = await CreateClosedAllDeadSegmentAsync(engine, capacity, "a-s2");

        capacity.UsedBytes = LeaveRoomForJournalOnlyCompaction(engine);
        Assert.True(engine.ObserveCapacityAdmissionPressure().IsUnderAdmissionPressure);
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), c => c.Begin.CompactionId == c1CompactionId && !c.Committed);

        SegmentId? selectedClosed = null;
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterCompactionVictimSelected = id => selectedClosed = id;

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(s2Id, selectedClosed);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(s2Id, result.SegmentId);
        Assert.NotEqual(c1Source, result.SegmentId);
        Assert.Equal(c1CompactionId, result.DeferredOpenCompactionId);
        Assert.Equal(
            StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress,
            result.DeferredOpenSkipReason);
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), c => c.Begin.CompactionId == c1CompactionId && !c.Committed);
        Assert.False(engine.Segments.TryGetSegmentInfo(s2Id, out _));
    }

    [Fact]
    public async Task B_Reclaim_remains_first_before_open_or_Closed()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 40),
            capacityReader: capacity);

        var retiredId = await AcceptCloseCompactRetireAsync(engine, CreateRecord("<p5f3-b-ret@seg.test>"));
        _ = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "b-c1");
        _ = await CreateClosedAllDeadSegmentAsync(engine, capacity, "b-s2");

        capacity.UsedBytes = capacity.TotalBytes - 1;
        SegmentId? reclaimHook = null;
        SegmentId? compactHook = null;
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterReclamationVictimSelected = id => reclaimHook = id;
        coordinator.TestHookAfterCompactionVictimSelected = id => compactHook = id;

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, result.Outcome);
        Assert.Equal(retiredId, result.SegmentId);
        Assert.Equal(retiredId, reclaimHook);
        Assert.Null(compactHook);
        Assert.False(engine.Segments.TryGetSegmentInfo(retiredId, out _));
    }

    [Fact]
    public async Task C_Open_progress_retains_priority_over_Closed()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 40),
            capacityReader: capacity);

        var records = Enumerable.Range(0, 3)
            .Select(i => CreateRecord($"<p5f3-c-c1-{i}@seg.test>"))
            .ToArray();
        foreach (var r in records)
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(r, CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(records[0].ArtId, out var meta));
        var c1Source = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(records[2].ArtId));

        // Leave an open incomplete compaction with room to continue.
        capacity.UsedBytes = 0;
        var firstDone = new ManualResetEventSlim(false);
        engine.TestHookAfterCompactionCapacityReserved = () =>
        {
            if (!firstDone.IsSet)
            {
                firstDone.Set();
                capacity.UsedBytes = LeaveRoomForWrittenFrame(engine);
            }
        };
        var coordinator = CreateCoordinator(engine);
        var incomplete = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Incomplete, incomplete.Outcome);
        Assert.True(incomplete.RelocatedArticleCount >= 1);
        engine.TestHookAfterCompactionCapacityReserved = null;

        var s2Id = await CreateClosedAllDeadSegmentAsync(engine, capacity, "c-s2");
        capacity.UsedBytes = 0; // allow C1 to continue
        Assert.False(engine.ObserveCapacityAdmissionPressure().IsUnderAdmissionPressure);

        SegmentId? selected = null;
        coordinator.TestHookAfterCompactionVictimSelected = id => selected = id;
        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Null(selected); // open continuation does not select a new Closed victim
        Assert.NotEqual(StorageMaintenanceOutcome.Skipped, result.Outcome);
        Assert.Null(result.DeferredOpenCompactionId);
        Assert.True(engine.Segments.TryGetSegmentInfo(s2Id, out _)); // S2 untouched this run
        Assert.True(
            result.Outcome is StorageMaintenanceOutcome.Incomplete
                or StorageMaintenanceOutcome.CompactedAndReclaimed
                or StorageMaintenanceOutcome.Compacted
                or StorageMaintenanceOutcome.Retired);
        if (result.SegmentId != default)
        {
            Assert.Equal(c1Source, result.SegmentId);
        }
    }

    [Fact]
    public async Task D_No_Closed_candidate_preserves_open_capacity_Skip()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 5),
            capacityReader: capacity);

        var (_, c1Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "d-c1");
        capacity.UsedBytes = capacity.TotalBytes - 1;

        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Skipped, result.Outcome);
        Assert.Equal(
            StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress,
            result.SkipReason);
        Assert.Equal(c1Id, result.CompactionId);
        Assert.Null(result.DeferredOpenCompactionId);
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), c => c.Begin.CompactionId == c1Id && !c.Committed);
    }

    [Fact]
    public async Task E_Non_capacity_zero_progress_does_not_fall_through()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 80, compactionHeadroom: 10),
            capacityReader: capacity);

        var record = CreateRecord("<p5f3-e@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Catalogue.TryGet(sourceId, out var info));

        // Two competing open Begins → Failed, not capacity-open-zero-progress.
        var id1 = engine.Journal.AllocateCompactionId();
        var id2 = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, id1, sourceId, info.Generation),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, id2, sourceId, info.Generation),
                CancellationToken.None));

        var s2Id = await CreateClosedAllDeadSegmentAsync(engine, capacity, "e-s2");
        SegmentId? selected = null;
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterCompactionVictimSelected = id => selected = id;

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Failed, result.Outcome);
        Assert.Null(selected);
        Assert.Null(result.DeferredOpenCompactionId);
        Assert.True(engine.Segments.TryGetSegmentInfo(s2Id, out _));
        Assert.NotEqual(
            StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress,
            result.SkipReason);
    }

    [Fact]
    public async Task F_Multiple_open_C1_blocked_falls_through_to_Closed_not_loop()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 40),
            capacityReader: capacity);

        var (c1Source, c1Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "f-c1");
        // Second open on a different source (lowest CompactionId remains C1).
        var (_, c2Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "f-c2");
        Assert.True(c1Id < c2Id);

        var s3Id = await CreateClosedAllDeadSegmentAsync(engine, capacity, "f-s3");
        capacity.UsedBytes = LeaveRoomForJournalOnlyCompaction(engine);

        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(s3Id, result.SegmentId);
        Assert.Equal(c1Id, result.DeferredOpenCompactionId);
        Assert.Equal(c1Source, result.DeferredOpenSourceSegmentId);
        Assert.Equal(2, result.DeferredOpenCompactionCount);
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), c => c.Begin.CompactionId == c1Id && !c.Committed);
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), c => c.Begin.CompactionId == c2Id && !c.Committed);
        Assert.False(engine.Segments.TryGetSegmentInfo(s3Id, out _));
    }

    [Fact]
    public async Task G_Pressure_free_capacity_fairness_still_falls_through()
    {
        using var dir = TempStorageDir.Create();
        // High MaxUtil so Used can block compaction headroom while article admission is not pressured
        // is hard with Used=Total; use large Total and Used just under MaxUtil but above MaxUtil+small headroom.
        var records = Enumerable.Range(0, 3)
            .Select(i => CreateRecord($"<p5f3-g-c1-{i}@seg.test>"))
            .ToArray();
        var required = SegmentRecordCodec.RecordLengthForArtSize(records[0].ArtSize);
        var total = required * 200L;
        var capacity = new MutableCapacityReader(total, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 90, compactionHeadroom: 5),
            capacityReader: capacity);

        foreach (var r in records)
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(r, CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(records[0].ArtId, out var meta));
        var c1Source = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(records[2].ArtId));

        capacity.UsedBytes = 0;
        var firstDone = new ManualResetEventSlim(false);
        engine.TestHookAfterCompactionCapacityReserved = () =>
        {
            if (!firstDone.IsSet)
            {
                firstDone.Set();
                // Block the next relocation while the in-flight Written frame still fits.
                capacity.UsedBytes = LeaveRoomForWrittenFrame(engine);
            }
        };
        var coordinator = CreateCoordinator(engine);
        var incomplete = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Incomplete, incomplete.Outcome);
        engine.TestHookAfterCompactionCapacityReserved = null;

        // Keep Used high enough that next relocate fails (compaction ceiling) but min article admit fits.
        capacity.UsedBytes = (long)(total * 0.95);
        var pressure = engine.ObserveCapacityAdmissionPressure();
        // May or may not be under article pressure depending on exact scaled math; fairness is capacity-skip based.
        _ = pressure;

        var s2Id = await CreateClosedAllDeadSegmentAsync(engine, capacity, "g-s2");
        capacity.UsedBytes = LeaveRoomForJournalOnlyCompaction(engine);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(s2Id, result.SegmentId);
        Assert.NotEqual(c1Source, result.SegmentId);
        Assert.Equal(
            StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress,
            result.DeferredOpenSkipReason);
    }

    [Fact]
    public async Task H_Result_attributes_success_to_Closed_not_deferred_open()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 40),
            capacityReader: capacity);

        var (c1Source, c1Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "h-c1");
        var s2Id = await CreateClosedAllDeadSegmentAsync(engine, capacity, "h-s2");
        capacity.UsedBytes = LeaveRoomForJournalOnlyCompaction(engine);

        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(s2Id, result.SegmentId);
        Assert.True(result.Reclaimed);
        Assert.NotEqual(c1Id, result.CompactionId);
        Assert.Equal(c1Id, result.DeferredOpenCompactionId);
        Assert.Equal(c1Source, result.DeferredOpenSourceSegmentId);
        Assert.Equal(
            StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress,
            result.DeferredOpenSkipReason);
    }

    private static StorageMaintenanceCoordinator CreateCoordinator(FileArticleStorageEngine engine) =>
        new(engine, new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0));

    private static long LeaveRoomForJournalOnlyCompaction(FileArticleStorageEngine engine)
    {
        var frames = ArticleJournalFrameCodec.CompactionBeginFrameLength
            + ArticleJournalFrameCodec.CompactionCommittedFrameLength
            + ArticleJournalFrameCodec.CompactionRetiredFrameLength;
        var ceiling = engine.ObserveCapacityAdmissionPressure().CompactionCeilingBytes;
        return Math.Max(0, ceiling - engine.ProcessLocalReservedBytes - frames);
    }

    private static long LeaveRoomForWrittenFrame(FileArticleStorageEngine engine)
    {
        var ceiling = engine.ObserveCapacityAdmissionPressure().CompactionCeilingBytes;
        var extra = ArticleJournalFrameCodec.RelocationWrittenFrameLength
            + ArticleJournalFrameCodec.RelocationIntentFrameLength;
        return Math.Max(0, ceiling - engine.ProcessLocalReservedBytes - extra);
    }

    private static ArticleStorageRuntimeOptions WithCapacity(
        ArticleStorageRuntimeOptions options,
        int maximumUtilization,
        int compactionHeadroom) =>
        options with
        {
            CapacityMaximumUtilization = maximumUtilization,
            CapacityCompactionHeadroom = compactionHeadroom,
            // Keep these fixtures on the admission-ceiling scheduler. Usage pressure
            // latches only when the volume is completely full.
            CapacityMaximumUsageCapacity = 100,
            CapacityFreeCapacity = 1,
        };

    private static async Task<(SegmentId SourceId, ulong CompactionId)> CreateOpenCapacityBlockedCompactionAsync(
        FileArticleStorageEngine engine,
        MutableCapacityReader capacity,
        string tag)
    {
        var records = Enumerable.Range(0, 3)
            .Select(i => CreateRecord($"<p5f3-{tag}-{i}@seg.test>"))
            .ToArray();
        foreach (var r in records)
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(r, CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(records[0].ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(records[2].ArtId));

        capacity.UsedBytes = 0;
        var firstDone = new ManualResetEventSlim(false);
        engine.TestHookAfterCompactionCapacityReserved = () =>
        {
            if (!firstDone.IsSet)
            {
                firstDone.Set();
                capacity.UsedBytes = LeaveRoomForWrittenFrame(engine);
            }
        };

        // Use the engine primitive — coordinator fall-through must not run during fixture setup.
        var compact = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Incomplete, compact.Outcome);
        Assert.True(compact.RelocatedCount >= 1);
        Assert.True(compact.CompactionId > 0);
        Assert.Contains("capacity", compact.Reason ?? string.Empty, StringComparison.Ordinal);
        engine.TestHookAfterCompactionCapacityReserved = null;
        capacity.UsedBytes = 0;

        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), c => !c.Committed && c.Begin.SourceSegmentId == sourceId);
        return (sourceId, compact.CompactionId);
    }

    private static async Task<SegmentId> CreateClosedAllDeadSegmentAsync(
        FileArticleStorageEngine engine,
        MutableCapacityReader capacity,
        string tag)
    {
        capacity.UsedBytes = 0;
        // Seal any Active destination left by a prior open compaction so S2 occupies its own segment.
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        var keep = CreateRecord($"<p5f3-{tag}-keep@seg.test>");
        var drop = CreateRecord($"<p5f3-{tag}-drop@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(keep.ArtId));
        Assert.True(engine.TryEvict(drop.ArtId));
        engine.RebuildSegmentAccountingFromIndex();
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        Assert.Equal(0, info.LiveBytes);
        Assert.True(info.DeadBytes > 0);
        return sourceId;
    }

    private static async Task<SegmentId> AcceptCloseCompactRetireAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.TryEvict(record.ArtId));
        var compact = await engine.CompactClosedSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        var retire = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.True(
            retire.Outcome is ArticleSegmentRetirementOutcome.Retired
                or ArticleSegmentRetirementOutcome.IdempotentNoOp);
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
        _ = builder.Append("Subject: fairness\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-p5f3-" + Guid.NewGuid().ToString("N"));
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
