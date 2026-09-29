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

/// <summary>Phase 5F.4: same-run rotation across capacity-blocked open compactions.</summary>
public sealed class OpenCompactionMultiOpenFairnessTests
{
    [Fact]
    public async Task A_Blocked_C1_then_progressing_C2()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.50, compactionHeadroom: 0.40),
            capacityReader: capacity);

        var (c1Source, c1Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "a-c1");
        var (c2Source, c2Id) = await CreateOpenAllDeadBeginAsync(engine, capacity, "a-c2");
        Assert.True(c1Id < c2Id);

        capacity.UsedBytes = capacity.TotalBytes;
        var attempted = new List<(SegmentId Source, ulong CompactionId)>();
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterOpenUncommittedAttempted = (src, id, _) => attempted.Add((src, id));

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { (c1Source, c1Id), (c2Source, c2Id) }, attempted);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(c2Source, result.SegmentId);
        Assert.Equal(c2Id, result.CompactionId);
        Assert.Equal(c1Id, result.DeferredOpenCompactionId);
        Assert.Equal(c1Source, result.DeferredOpenSourceSegmentId);
        Assert.Equal(1, result.DeferredOpenCompactionCount);
        Assert.Equal(
            StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress,
            result.DeferredOpenSkipReason);
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), c => c.Begin.CompactionId == c1Id && !c.Committed);
        Assert.False(engine.Segments.TryGetSegmentInfo(c2Source, out _));
    }

    [Fact]
    public async Task B_Blocked_C1_C2_then_progressing_C3()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.50, compactionHeadroom: 0.40),
            capacityReader: capacity);

        var (c1Source, c1Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "b-c1");
        var (c2Source, c2Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "b-c2");
        var (c3Source, c3Id) = await CreateOpenAllDeadBeginAsync(engine, capacity, "b-c3");
        Assert.True(c1Id < c2Id && c2Id < c3Id);

        capacity.UsedBytes = capacity.TotalBytes;
        var attempted = new List<ulong>();
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterOpenUncommittedAttempted = (_, id, _) => attempted.Add(id);

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { c1Id, c2Id, c3Id }, attempted);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(c3Source, result.SegmentId);
        Assert.Equal(c3Id, result.CompactionId);
        Assert.Equal(c1Id, result.DeferredOpenCompactionId);
        Assert.Equal(2, result.DeferredOpenCompactionCount);
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), c => c.Begin.CompactionId == c1Id && !c.Committed);
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), c => c.Begin.CompactionId == c2Id && !c.Committed);
        Assert.False(engine.Segments.TryGetSegmentInfo(c3Source, out _));
        _ = c1Source;
        _ = c2Source;
    }

    [Fact]
    public async Task C_All_opens_capacity_blocked_Closed_fallback()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.50, compactionHeadroom: 0.40),
            capacityReader: capacity);

        var (c1Source, c1Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "c-c1");
        var (_, c2Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "c-c2");
        var closedId = await CreateClosedAllDeadSegmentAsync(engine, capacity, "c-s3");
        Assert.True(c1Id < c2Id);

        capacity.UsedBytes = capacity.TotalBytes;
        var openAttempts = new List<ulong>();
        SegmentId? closedSelected = null;
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterOpenUncommittedAttempted = (_, id, _) => openAttempts.Add(id);
        coordinator.TestHookAfterCompactionVictimSelected = id => closedSelected = id;

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { c1Id, c2Id }, openAttempts);
        Assert.Equal(closedId, closedSelected);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(closedId, result.SegmentId);
        Assert.Equal(c1Id, result.DeferredOpenCompactionId);
        Assert.Equal(c1Source, result.DeferredOpenSourceSegmentId);
        Assert.Equal(2, result.DeferredOpenCompactionCount);
        Assert.False(engine.Segments.TryGetSegmentInfo(closedId, out _));
    }

    [Fact]
    public async Task D_All_opens_capacity_blocked_no_Closed_preserves_Skip()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.50, compactionHeadroom: 0.05),
            capacityReader: capacity);

        var (_, c1Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "d-c1");
        var (_, c2Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "d-c2");
        Assert.True(c1Id < c2Id);

        capacity.UsedBytes = capacity.TotalBytes;
        var openAttempts = new List<ulong>();
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterOpenUncommittedAttempted = (_, id, _) => openAttempts.Add(id);

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { c1Id, c2Id }, openAttempts);
        Assert.Equal(StorageMaintenanceOutcome.Skipped, result.Outcome);
        Assert.Equal(
            StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress,
            result.SkipReason);
        Assert.Equal(c1Id, result.CompactionId);
        Assert.Equal(2, result.DeferredOpenCompactionCount);
        Assert.Equal(c1Id, result.DeferredOpenCompactionId);
    }

    [Fact]
    public async Task E_Progressing_C1_does_not_attempt_C2()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.50, compactionHeadroom: 0.40),
            capacityReader: capacity);

        var (c1Source, c1Id) = await CreateOpenAllDeadBeginAsync(engine, capacity, "e-c1");
        var (c2Source, c2Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "e-c2");
        Assert.True(c1Id < c2Id);

        capacity.UsedBytes = 0;
        var openAttempts = new List<ulong>();
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterOpenUncommittedAttempted = (_, id, _) => openAttempts.Add(id);

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { c1Id }, openAttempts);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(c1Source, result.SegmentId);
        Assert.Equal(c1Id, result.CompactionId);
        Assert.Null(result.DeferredOpenCompactionId);
        Assert.Equal(0, result.DeferredOpenCompactionCount);
        Assert.True(engine.Segments.TryGetSegmentInfo(c2Source, out _));
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), c => c.Begin.CompactionId == c2Id && !c.Committed);
    }

    [Fact]
    public async Task F_Failed_C1_does_not_attempt_C2()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.80, compactionHeadroom: 0.10),
            capacityReader: capacity);

        var record = CreateRecord("<p5f4-f@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Catalogue.TryGet(sourceId, out var info));

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

        var (_, c3Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "f-c3");
        Assert.True(id1 < c3Id);

        var openAttempts = new List<ulong>();
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterOpenUncommittedAttempted = (_, id, _) => openAttempts.Add(id);

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { id1 }, openAttempts);
        Assert.Equal(StorageMaintenanceOutcome.Failed, result.Outcome);
        Assert.Null(result.DeferredOpenCompactionId);
        Assert.DoesNotContain(openAttempts, id => id == c3Id);
    }

    [Fact]
    public async Task G_CompetingOpen_C1_does_not_attempt_C2()
    {
        // CompetingOpenCompaction maps to Failed; same fail-closed non-rotation as F.
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.80, compactionHeadroom: 0.10),
            capacityReader: capacity);

        var record = CreateRecord("<p5f4-g@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Catalogue.TryGet(sourceId, out var info));

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

        var (_, otherId) = await CreateOpenAllDeadBeginAsync(engine, capacity, "g-other");
        Assert.True(id1 < otherId);

        var openAttempts = new List<ulong>();
        SegmentId? closedSelected = null;
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterOpenUncommittedAttempted = (_, id, _) => openAttempts.Add(id);
        coordinator.TestHookAfterCompactionVictimSelected = id => closedSelected = id;

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { id1 }, openAttempts);
        Assert.Equal(StorageMaintenanceOutcome.Failed, result.Outcome);
        Assert.Contains("multiple-uncommitted", result.SkipReason ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(closedSelected);
        Assert.Null(result.DeferredOpenCompactionId);
    }

    [Fact]
    public async Task H_Stale_Skip_C1_does_not_attempt_C2()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.50, compactionHeadroom: 0.40),
            capacityReader: capacity);

        // Orphan Begin whose source was reclaimed → RejectedSourceMissing Skip (not capacity yield).
        var goneSource = await AcceptCloseCompactRetireAsync(
            engine,
            CreateRecord("<p5f4-h-gone@seg.test>"));
        Assert.True(engine.Catalogue.TryGet(goneSource, out var beforeReclaim));
        var goneGeneration = beforeReclaim.Generation;
        var reclaim = await engine.ReclaimRetiredSegmentAsync(goneSource, CancellationToken.None);
        Assert.True(
            reclaim.Outcome is ArticleSegmentReclamationOutcome.Reclaimed
                or ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed);
        Assert.False(engine.Catalogue.TryGet(goneSource, out _));

        var staleId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, staleId, goneSource, goneGeneration),
                CancellationToken.None));

        var (_, c2Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "h-c2");
        Assert.True(staleId < c2Id);

        capacity.UsedBytes = capacity.TotalBytes;
        var openAttempts = new List<ulong>();
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterOpenUncommittedAttempted = (_, id, _) => openAttempts.Add(id);

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { staleId }, openAttempts);
        Assert.Equal(StorageMaintenanceOutcome.Skipped, result.Outcome);
        Assert.NotEqual(
            StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress,
            result.SkipReason);
        Assert.Null(result.DeferredOpenCompactionId);
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), c => c.Begin.CompactionId == c2Id && !c.Committed);
    }

    [Fact]
    public async Task I_Same_source_competing_opens_remain_fail_closed()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.80, compactionHeadroom: 0.10),
            capacityReader: capacity);

        var record = CreateRecord("<p5f4-i@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Catalogue.TryGet(sourceId, out var info));

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

        var closedId = await CreateClosedAllDeadSegmentAsync(engine, capacity, "i-closed");
        capacity.UsedBytes = capacity.TotalBytes;

        SegmentId? closedSelected = null;
        var openAttempts = new List<ulong>();
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterOpenUncommittedAttempted = (_, id, _) => openAttempts.Add(id);
        coordinator.TestHookAfterCompactionVictimSelected = id => closedSelected = id;

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { id1 }, openAttempts);
        Assert.Equal(StorageMaintenanceOutcome.Failed, result.Outcome);
        Assert.Null(closedSelected);
        Assert.True(engine.Segments.TryGetSegmentInfo(closedId, out _));
    }

    [Fact]
    public async Task J_Result_accounting_belongs_to_progressing_open()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.50, compactionHeadroom: 0.40),
            capacityReader: capacity);

        var (c1Source, c1Id) = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "j-c1");
        var (c2Source, c2Id) = await CreateOpenAllDeadBeginAsync(engine, capacity, "j-c2");

        capacity.UsedBytes = capacity.TotalBytes;
        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(c2Source, result.SegmentId);
        Assert.Equal(c2Id, result.CompactionId);
        Assert.NotEqual(c1Source, result.SegmentId);
        Assert.Equal(c1Id, result.DeferredOpenCompactionId);
        // All-dead open relocates nothing; do not attribute C1's prior relocates to C2.
        Assert.Equal(0, result.RelocatedArticleCount);
        Assert.True(result.Reclaimed);
    }

    [Fact]
    public async Task K_Reclaim_still_first_before_open_scheduling()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.50, compactionHeadroom: 0.40),
            capacityReader: capacity);

        var retiredId = await AcceptCloseCompactRetireAsync(engine, CreateRecord("<p5f4-k-ret@seg.test>"));
        _ = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "k-c1");
        _ = await CreateOpenCapacityBlockedCompactionAsync(engine, capacity, "k-c2");

        capacity.UsedBytes = capacity.TotalBytes;
        SegmentId? reclaimHook = null;
        var openAttempts = 0;
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterReclamationVictimSelected = id => reclaimHook = id;
        coordinator.TestHookAfterOpenUncommittedAttempted = (_, _, _) => openAttempts++;

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, result.Outcome);
        Assert.Equal(retiredId, result.SegmentId);
        Assert.Equal(retiredId, reclaimHook);
        Assert.Equal(0, openAttempts);
    }

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

    private static async Task<(SegmentId SourceId, ulong CompactionId)> CreateOpenCapacityBlockedCompactionAsync(
        FileArticleStorageEngine engine,
        MutableCapacityReader capacity,
        string tag)
    {
        var records = Enumerable.Range(0, 3)
            .Select(i => CreateRecord($"<p5f4-{tag}-{i}@seg.test>"))
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
                capacity.UsedBytes = capacity.TotalBytes;
            }
        };

        var compact = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Incomplete, compact.Outcome);
        Assert.True(compact.RelocatedCount >= 1);
        Assert.True(compact.CompactionId > 0);
        Assert.Contains("capacity", compact.Reason ?? string.Empty, StringComparison.Ordinal);
        engine.TestHookAfterCompactionCapacityReserved = null;
        capacity.UsedBytes = 0;

        Assert.Contains(
            engine.Journal.EnumerateOpenCompactions(),
            c => !c.Committed && c.Begin.SourceSegmentId == sourceId);
        return (sourceId, compact.CompactionId);
    }

    /// <summary>
    /// Open uncommitted Begin on an all-dead Closed source — continues without capacity relocates.
    /// </summary>
    private static async Task<(SegmentId SourceId, ulong CompactionId)> CreateOpenAllDeadBeginAsync(
        FileArticleStorageEngine engine,
        MutableCapacityReader capacity,
        string tag)
    {
        var sourceId = await CreateClosedAllDeadSegmentAsync(engine, capacity, tag);
        Assert.True(engine.Catalogue.TryGet(sourceId, out var info));
        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, sourceId, info.Generation),
                CancellationToken.None));
        Assert.Contains(
            engine.Journal.EnumerateOpenCompactions(),
            c => c.Begin.CompactionId == compactionId && !c.Committed);
        return (sourceId, compactionId);
    }

    private static async Task<SegmentId> CreateClosedAllDeadSegmentAsync(
        FileArticleStorageEngine engine,
        MutableCapacityReader capacity,
        string tag)
    {
        capacity.UsedBytes = 0;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        var keep = CreateRecord($"<p5f4-{tag}-keep@seg.test>");
        var drop = CreateRecord($"<p5f4-{tag}-drop@seg.test>");
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
        _ = builder.Append("Subject: multi-open-fairness\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-p5f4-" + Guid.NewGuid().ToString("N"));
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
