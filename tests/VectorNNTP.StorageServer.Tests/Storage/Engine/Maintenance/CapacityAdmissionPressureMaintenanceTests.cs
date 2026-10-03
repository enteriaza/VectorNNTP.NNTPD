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

/// <summary>Phase 5F.2: capacity-pressure / admission-recovery maintenance.</summary>
public sealed class CapacityAdmissionPressureMaintenanceTests
{
    [Fact]
    public void Ledger_ceiling_and_recovery_target_match_WouldFit()
    {
        const long total = 1_000;
        const int maxUtil = 80;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, maxUtil);
        Assert.Equal(800, ceiling);

        Assert.Equal(
            0,
            ProcessLocalCapacityLedger.ComputeAdmissionRecoveryTargetBytes(
                usedBytes: 700,
                articleReservedBytes: 0,
                compactionReservedBytes: 0,
                totalBytes: total,
                maximumUtilization: maxUtil,
                minimumRequiredBytes: 1));

        // used + art + comp + min = 801 → need 1 byte of Used to disappear.
        Assert.Equal(
            1,
            ProcessLocalCapacityLedger.ComputeAdmissionRecoveryTargetBytes(
                usedBytes: 800,
                articleReservedBytes: 0,
                compactionReservedBytes: 0,
                totalBytes: total,
                maximumUtilization: maxUtil,
                minimumRequiredBytes: 1));

        // occupied = 790+40+20+1 = 851; ceiling = 800 → deficit 51.
        Assert.Equal(
            51,
            ProcessLocalCapacityLedger.ComputeAdmissionRecoveryTargetBytes(
                usedBytes: 790,
                articleReservedBytes: 40,
                compactionReservedBytes: 20,
                totalBytes: total,
                maximumUtilization: maxUtil,
                minimumRequiredBytes: 1));

        var ledger = new ProcessLocalCapacityLedger();
        ledger.TentativeAddArticle(40);
        Assert.False(ledger.WouldFit(790, total, requiredBytes: 1, maxUtil));
        Assert.True(
            ProcessLocalCapacityLedger.WouldFit(
                usedBytes: 790,
                articleReservedBytes: 40,
                compactionReservedBytes: 0,
                totalBytes: total,
                requiredBytes: 1,
                ceilingPercent: 90));
    }

    [Fact]
    public async Task A_Pressure_with_Retired_reclaims_first_and_reduces_recovery_target()
    {
        using var dir = TempStorageDir.Create();
        var retiredArticle = CreateRecord("<p5f2-a-ret@seg.test>");
        var closedArticle = CreateRecord("<p5f2-a-cls@seg.test>");
        var capacity = new DirectoryAwareCapacityReader(dir.Options.SegmentDir, total: 10_000_000, otherUsed: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 10),
            capacityReader: capacity);

        var retiredId = await AcceptCloseCompactRetireAsync(engine, retiredArticle);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(closedArticle, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(closedArticle.ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(retiredId, out var retiredInfo));
        Assert.Equal(SegmentState.Retired, retiredInfo.State);

        // Force admission pressure (MaxUtil 50% of 10MiB = 5MiB) while leaving a Retired file.
        capacity.OtherUsed = 6_000_000;
        var before = engine.ObserveCapacityAdmissionPressure();
        Assert.True(before.IsUnderAdmissionPressure);
        Assert.True(before.AdmissionRecoveryTargetBytes > 0);

        var coordinator = CreateCoordinator(engine);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, result.Outcome);
        Assert.Equal(retiredId, result.SegmentId);
        Assert.True(result.Reclaimed);
        Assert.False(result.CompactionAttempted);
        Assert.False(engine.Segments.TryGetSegmentInfo(retiredId, out _));

        Assert.True(result.AdmissionPressure.HasValue);
        Assert.True(result.CapacityUsedBytes < before.UsedBytes);
        Assert.True(result.AdmissionRecoveryTargetBytes < before.AdmissionRecoveryTargetBytes);
    }

    [Fact]
    public async Task B_Pressure_no_Retired_feasible_victim_compacts_toward_reclaim()
    {
        using var dir = TempStorageDir.Create();
        var keep = CreateRecord("<p5f2-b-keep@seg.test>");
        var drop = CreateRecord("<p5f2-b-drop@seg.test>");
        var capacity = new DirectoryAwareCapacityReader(dir.Options.SegmentDir, total: 10_000_000, otherUsed: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 40),
            capacityReader: capacity);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));

        capacity.OtherUsed = 6_000_000;
        Assert.True(engine.ObserveCapacityAdmissionPressure().IsUnderAdmissionPressure);

        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(sourceId, result.SegmentId);
        Assert.True(result.CompactionAttempted);
        Assert.True(result.Reclaimed);
        Assert.False(engine.Segments.TryGetSegmentInfo(sourceId, out _));
        Assert.True(engine.TryRead(keep.ArtId, out _));
    }

    [Fact]
    public async Task C_Pressure_insufficient_headroom_skips_without_destructive_work()
    {
        using var dir = TempStorageDir.Create();
        var keep = CreateRecord("<p5f2-c-keep@seg.test>");
        var drop = CreateRecord("<p5f2-c-drop@seg.test>");
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 5),
            capacityReader: capacity);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var before));

        // Used at Total → MaxUtil and MaxUtil+Headroom both reject a full-LiveBytes relocate.
        capacity.UsedBytes = capacity.TotalBytes - 1;
        Assert.True(engine.ObserveCapacityAdmissionPressure().IsUnderAdmissionPressure);
        Assert.False(
            ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(
                in before,
                engine.ObserveCapacityAdmissionPressure()));

        var openBefore = engine.Journal.EnumerateOpenCompactions().Count();
        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Skipped, result.Outcome);
        Assert.Equal(
            StorageMaintenanceSkipReasons.CapacityPressureNoFeasibleCandidate,
            result.SkipReason);
        Assert.False(result.CompactionAttempted);
        Assert.False(result.Reclaimed);
        Assert.Equal(openBefore, engine.Journal.EnumerateOpenCompactions().Count());
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var after));
        Assert.Equal(SegmentState.Closed, after.State);
        Assert.Equal(before.SizeBytes, after.SizeBytes);
        Assert.NotEqual(StorageMaintenanceOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task D_Open_compaction_zero_progress_capacity_is_Skipped_not_Failed()
    {
        using var dir = TempStorageDir.Create();
        var records = Enumerable.Range(0, 3)
            .Select(i => CreateRecord($"<p5f2-d-{i}@seg.test>"))
            .ToArray();
        var required = SegmentRecordCodec.RecordLengthForArtSize(records[0].ArtSize);
        var total = required * 100L;
        var capacity = new MutableCapacityReader(total, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 5),
            capacityReader: capacity);

        foreach (var r in records)
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(r, CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(records[2].ArtId));

        // Start an open compaction with partial progress, then deny capacity.
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
        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var incomplete = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Incomplete, incomplete.Outcome);
        Assert.True(incomplete.RelocatedArticleCount >= 1);
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), static c => !c.Committed);

        engine.TestHookAfterCompactionCapacityReserved = null;
        capacity.UsedBytes = total - 1;
        var articleResBefore = engine.ProcessLocalArticleReservedBytes;
        var skipped = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Skipped, skipped.Outcome);
        Assert.Equal(
            StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress,
            skipped.SkipReason);
        Assert.NotEqual(StorageMaintenanceOutcome.Failed, skipped.Outcome);
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), static c => !c.Committed);
        Assert.Equal(articleResBefore, engine.ProcessLocalArticleReservedBytes);
    }

    [Fact]
    public void E_Normal_pressure_free_ordering_unchanged()
    {
        var policy = new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0);
        var lowRatioLarge = Seg(1, size: 10_000, live: 9_000, dead: 1_000);
        var highRatioSmall = Seg(2, size: 1_000, live: 100, dead: 900);
        Assert.True(policy.TrySelectCompactionVictim([lowRatioLarge, highRatioSmall], out var victim));
        Assert.Equal(2UL, victim.SegmentId.Value);
    }

    [Fact]
    public void F_Pressure_aware_victim_prefers_physical_SizeBytes()
    {
        var policy = new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0);
        var lowRatioLarge = Seg(1, size: 10_000, live: 100, dead: 1_000);
        var highRatioSmall = Seg(2, size: 1_000, live: 100, dead: 900);
        var pressure = CapacityAdmissionPressureSnapshot.FromCapacityState(
            new StorageCapacitySnapshot(TotalBytes: 100_000, UsedBytes: 90_000, AvailableBytes: 10_000),
            articleReservedBytes: 0,
            compactionReservedBytes: 0,
            maximumUtilization: 80,
            compactionHeadroom: 15);

        Assert.True(pressure.IsUnderAdmissionPressure);
        Assert.True(
            policy.TrySelectPressureReliefCompactionVictim(
                [lowRatioLarge, highRatioSmall],
                in pressure,
                out var victim));
        Assert.Equal(1UL, victim.SegmentId.Value);

        // Normal ordering still prefers high dead ratio when not using pressure path.
        Assert.True(policy.TrySelectCompactionVictim([lowRatioLarge, highRatioSmall], out var normal));
        Assert.Equal(2UL, normal.SegmentId.Value);
    }

    [Fact]
    public async Task G_Reclaim_releases_the_retired_segment_hold_and_keeps_the_unwritten_one()
    {
        using var dir = TempStorageDir.Create();
        var held = CreateRecord("<p5f2-g-held@seg.test>");
        var retired = CreateRecord("<p5f2-g-ret@seg.test>");
        var heldBytes = SegmentRecordCodec.RecordLengthForArtSize(held.ArtSize);
        var capacity = new DirectoryAwareCapacityReader(dir.Options.SegmentDir, total: 10_000_000, otherUsed: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 10),
            capacityReader: capacity);

        var retiredId = await AcceptCloseCompactRetireAsync(engine, retired);

        engine.SuspendBackgroundPersist = true;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(held, CancellationToken.None)).Outcome);
        var articleRes = engine.ProcessLocalArticleReservedBytes;
        Assert.Equal(heldBytes, articleRes);

        capacity.OtherUsed = 6_000_000;
        var observed = engine.ObserveCapacityAdmissionPressure();
        Assert.Equal(articleRes, observed.ArticleReservedBytes);
        Assert.True(observed.IsUnderAdmissionPressure);

        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, result.Outcome);
        Assert.Equal(retiredId, result.SegmentId);
        Assert.Equal(heldBytes, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(heldBytes, result.CapacityArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalWrittenArticleBytesOnSegment(retiredId));
    }

    [Fact]
    public void H_CompactionReservedBytes_included_in_pressure_and_feasibility()
    {
        const long total = 1_000;
        const int maxUtil = 80;
        const int headroom = 10;

        var baseTarget = ProcessLocalCapacityLedger.ComputeAdmissionRecoveryTargetBytes(
            usedBytes: 780,
            articleReservedBytes: 0,
            compactionReservedBytes: 0,
            totalBytes: total,
            maximumUtilization: maxUtil,
            minimumRequiredBytes: 1);
        var withComp = ProcessLocalCapacityLedger.ComputeAdmissionRecoveryTargetBytes(
            usedBytes: 780,
            articleReservedBytes: 0,
            compactionReservedBytes: 50,
            totalBytes: total,
            maximumUtilization: maxUtil,
            minimumRequiredBytes: 1);
        Assert.Equal(0, baseTarget);
        Assert.True(withComp > baseTarget);

        var segment = Seg(1, size: 200, live: 120, dead: 80);
        var withoutCompRes = CapacityAdmissionPressureSnapshot.FromCapacityState(
            new StorageCapacitySnapshot(total, UsedBytes: 780, AvailableBytes: 220),
            articleReservedBytes: 0,
            compactionReservedBytes: 0,
            maximumUtilization: maxUtil,
            compactionHeadroom: headroom);
        var withCompRes = CapacityAdmissionPressureSnapshot.FromCapacityState(
            new StorageCapacitySnapshot(total, UsedBytes: 780, AvailableBytes: 220),
            articleReservedBytes: 0,
            compactionReservedBytes: 50,
            maximumUtilization: maxUtil,
            compactionHeadroom: headroom);

        // Compaction ceiling 0.90×1000 = 900: 780+120 fits; 780+50+120 does not.
        Assert.True(ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(in segment, in withoutCompRes));
        Assert.False(ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(in segment, in withCompRes));
        Assert.True(withCompRes.AdmissionRecoveryTargetBytes > withoutCompRes.AdmissionRecoveryTargetBytes);
    }

    [Fact]
    public async Task I_DeadBytes_alone_do_not_reduce_pressure()
    {
        using var dir = TempStorageDir.Create();
        var keep = CreateRecord("<p5f2-i-keep@seg.test>");
        var drop = CreateRecord("<p5f2-i-drop@seg.test>");
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 40),
            capacityReader: capacity);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var closed));
        Assert.True(closed.DeadBytes > 0);

        capacity.UsedBytes = 9_000_000;
        var before = engine.ObserveCapacityAdmissionPressure();
        Assert.True(before.IsUnderAdmissionPressure);
        var targetBefore = before.AdmissionRecoveryTargetBytes;

        // DeadBytes exist but Used is unchanged → pressure unchanged (no fake recovery).
        var afterEvict = engine.ObserveCapacityAdmissionPressure();
        Assert.Equal(targetBefore, afterEvict.AdmissionRecoveryTargetBytes);
        Assert.Equal(before.UsedBytes, afterEvict.UsedBytes);
    }

    [Fact]
    public async Task J_Retired_deletion_reduces_UsedBytes_in_next_capacity_snapshot()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<p5f2-j@seg.test>");
        var capacity = new DirectoryAwareCapacityReader(dir.Options.SegmentDir, total: 10_000_000, otherUsed: 500_000);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 80, compactionHeadroom: 10),
            capacityReader: capacity);

        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);
        var usedBefore = capacity.Read().UsedBytes;
        Assert.True(usedBefore > capacity.OtherUsed);

        var result = await CreateCoordinator(engine).RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, result.Outcome);
        Assert.Equal(sourceId, result.SegmentId);

        var usedAfter = capacity.Read().UsedBytes;
        Assert.True(usedAfter < usedBefore);
        Assert.Equal(capacity.OtherUsed, usedAfter);
        Assert.True(result.CapacityUsedBytes <= usedBefore);
    }

    [Fact]
    public async Task K_Pressure_no_feasible_candidate_is_Skipped_not_Failed()
    {
        using var dir = TempStorageDir.Create();
        var keep = CreateRecord("<p5f2-k-keep@seg.test>");
        var drop = CreateRecord("<p5f2-k-drop@seg.test>");
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 1),
            capacityReader: capacity);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));

        capacity.UsedBytes = capacity.TotalBytes - 1;
        var result = await CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0)
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Skipped, result.Outcome);
        Assert.Equal(
            StorageMaintenanceSkipReasons.CapacityPressureNoFeasibleCandidate,
            result.SkipReason);
        Assert.NotEqual(StorageMaintenanceOutcome.Failed, result.Outcome);
        Assert.True(result.AdmissionPressure);
    }

    [Fact]
    public async Task L_Stale_skip_remains_distinguishable_from_capacity_skip()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<p5f2-l-stale@seg.test>");
        var phantom = CreateRecord("<p5f2-l-phantom@seg.test>");
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 80, compactionHeadroom: 10),
            capacityReader: capacity);

        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterReclamationVictimSelected = id =>
        {
            Assert.Equal(sourceId, id);
            Assert.True(engine.Index.TryCommitPresent(new StoredArticleMetadata(
                phantom.ArtId,
                phantom.ArtHash,
                phantom.ArtSize,
                new StoredArticleLocation(sourceId, 0, phantom.ArtSize),
                ArticleStorageState.Present,
                DateTimeOffset.UtcNow,
                1UL)));
        };

        var stale = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Skipped, stale.Outcome);
        Assert.Equal("present-remain-on-source", stale.SkipReason);

        using var dir2 = TempStorageDir.Create();
        var keep = CreateRecord("<p5f2-l-keep@seg.test>");
        var drop = CreateRecord("<p5f2-l-drop@seg.test>");
        var capacity2 = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine2 = FileArticleStorageEngine.Open(
            WithCapacity(dir2.Options, maximumUtilization: 50, compactionHeadroom: 1),
            capacityReader: capacity2);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine2.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine2.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine2.DrainPendingAsync(CancellationToken.None);
        await engine2.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine2.TryEvict(drop.ArtId));
        capacity2.UsedBytes = capacity2.TotalBytes - 1;

        var capacitySkip = await CreateCoordinator(engine2, minimumDeadBytes: 0, minimumDeadRatio: 0)
            .RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Skipped, capacitySkip.Outcome);
        Assert.Equal(
            StorageMaintenanceSkipReasons.CapacityPressureNoFeasibleCandidate,
            capacitySkip.SkipReason);
        Assert.NotEqual(stale.SkipReason, capacitySkip.SkipReason);
        Assert.StartsWith("capacity-", capacitySkip.SkipReason, StringComparison.Ordinal);
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
            ClosedUtc: new DateTimeOffset(2024, 8, 23, 8, 0, 0, TimeSpan.Zero),
            ExtentAccountingComplete: true);

    private static StorageMaintenanceCoordinator CreateCoordinator(
        FileArticleStorageEngine engine,
        long minimumDeadBytes = ArticleCompactionPolicyOptions.DefaultMinimumDeadBytes,
        int minimumDeadRatio = ArticleCompactionPolicyOptions.DefaultMinimumDeadRatio) =>
        new(engine, new ArticleSegmentPolicy(minimumDeadBytes, minimumDeadRatio));

    private static long LeaveRoomForWrittenFrame(FileArticleStorageEngine engine)
    {
        var ceiling = engine.ObserveCapacityAdmissionPressure().CompactionCeilingBytes;
        return Math.Max(
            0,
            ceiling - engine.ProcessLocalReservedBytes - ArticleJournalFrameCodec.RelocationWrittenFrameLength);
    }

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
        _ = builder.Append("Subject: pressure\r\n");
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

    /// <summary>
    /// Capacity reader whose UsedBytes = OtherUsed + sum of files under the segment directory.
    /// Retired-file deletion reduces the next snapshot without sleeps.
    /// </summary>
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
                        // Ignore races with concurrent delete.
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-p5f2-" + Guid.NewGuid().ToString("N"));
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
