using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Maintenance;

/// <summary>Phase 5B: explicitly-invoked storage maintenance coordinator.</summary>
public sealed class StorageMaintenanceCoordinatorTests
{
    [Fact]
    public async Task A_EmptyCatalogue_NoWork()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var coordinator = CreateCoordinator(engine);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.NoWork, result.Outcome);
        Assert.False(result.CompactionAttempted);
        Assert.False(result.ReclamationAttempted);
    }

    [Fact]
    public async Task B_RetiredPriority_BeforeNewCompaction()
    {
        using var dir = TempStorageDir.Create();
        var retiredArticle = CreateRecord("<mnt-b-ret@seg.test>");
        var closedArticle = CreateRecord("<mnt-b-cls@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var retiredId = await AcceptCloseCompactRetireAsync(engine, retiredArticle);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(closedArticle, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(closedArticle.ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(retiredId, out var retiredInfo));
        Assert.Equal(SegmentState.Retired, retiredInfo.State);

        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, result.Outcome);
        Assert.Equal(retiredId, result.SegmentId);
        Assert.True(result.Reclaimed);
        Assert.False(result.CompactionAttempted);
        Assert.False(engine.Segments.TryGetSegmentInfo(retiredId, out _));
    }

    [Fact]
    public async Task C_ReclamationOnly()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-c@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);
        var coordinator = CreateCoordinator(engine);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, result.Outcome);
        Assert.Equal(sourceId, result.SegmentId);
        Assert.True(result.ReclamationAttempted);
        Assert.True(result.Reclaimed);
        Assert.False(result.CompactionAttempted);
    }

    [Fact]
    public async Task D_F_NoRetired_EligibleClosed_FullLifecycle()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-d@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));

        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(sourceId, result.SegmentId);
        Assert.True(result.CompactionAttempted);
        Assert.True(result.CompactionCommitted);
        Assert.True(result.RetirementAttempted);
        Assert.True(result.Retired);
        Assert.True(result.ReclamationAttempted);
        Assert.True(result.Reclaimed);
        Assert.False(engine.Segments.TryGetSegmentInfo(sourceId, out _));
    }

    [Fact]
    public async Task E_StaleClosedCandidate_Skipped_NoSecondVictim()
    {
        using var dir = TempStorageDir.Create();
        var a = CreateRecord("<mnt-e-a@seg.test>");
        var b = CreateRecord("<mnt-e-b@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(a, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(a.ArtId));
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(b, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(b.ArtId));

        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        SegmentId selected = default;
        coordinator.TestHookAfterCompactionVictimSelected = id =>
        {
            selected = id;
            Assert.True(engine.Catalogue.TryGet(id, out var info));
            // Force Closed → Retired so revalidation refuses compaction.
            Assert.True(engine.Catalogue.TryRetire(id, info.Generation, DateTimeOffset.UtcNow));
        };

        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Skipped, result.Outcome);
        Assert.Equal(selected, result.SegmentId);
        Assert.False(result.CompactionAttempted);
        Assert.Contains("retired", result.SkipReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task G_CompactionIncomplete_NoRetirement()
    {
        using var dir = TempStorageDir.Create();
        var live = CreateRecord("<mnt-g-live@seg.test>");
        var phantom = CreateRecord("<mnt-g-phantom@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(live, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(live.ArtId, out var liveMeta));
        var sourceId = liveMeta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        engine.TestHookBeforeRelocateArticle = _ =>
        {
            Assert.True(engine.Index.TryCommitPresent(new StoredArticleMetadata(
                phantom.ArtId,
                phantom.ArtHash,
                phantom.ArtSize,
                liveMeta.Location,
                ArticleStorageState.Present,
                DateTimeOffset.UtcNow,
                1UL)));
        };

        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Incomplete, result.Outcome);
        Assert.Equal(sourceId, result.SegmentId);
        Assert.True(result.CompactionAttempted);
        Assert.False(result.CompactionCommitted);
        Assert.False(result.RetirementAttempted);
        Assert.True(engine.Journal.TryGetCompaction(result.CompactionId, out var snap));
        Assert.False(snap.Committed);
    }

    [Fact]
    public async Task H_UnprovedClosedSegment_IsNotACompactionVictim()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-h@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        // Replace the closed payload before any accounting scan. The segment cannot be
        // proved, so it must not be selected as a compaction victim.
        var closedPath = Path.Combine(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(sourceId, SegmentFileKind.Closed));
        await File.WriteAllBytesAsync(closedPath, [0x00, 0x01, 0x02, 0x03]);

        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.NoWork, result.Outcome);
        Assert.False(result.CompactionAttempted);
        Assert.False(result.CompactionCommitted);
        Assert.False(result.RetirementAttempted);
        Assert.False(result.Reclaimed);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var unaccounted));
        Assert.False(unaccounted.ExtentAccountingComplete);
    }

    [Fact]
    public async Task I_RetirementFailure_KeepsCommitted()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-i@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));

        engine.TestRetirementFaultPoint =
            FileArticleStorageEngine.RetirementFaultPoint.BeforeCompactionRetired;
        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        await Assert.ThrowsAsync<IOException>(() => coordinator.RunOnceAsync(CancellationToken.None));
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), static c => c.Committed);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
    }

    [Fact]
    public async Task J_ReclamationFailure_KeepsRetired()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-j@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));

        engine.TestReclamationFaultPoint =
            FileArticleStorageEngine.ReclamationFaultPoint.BeforeDelete;
        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        await Assert.ThrowsAsync<IOException>(() => coordinator.RunOnceAsync(CancellationToken.None));
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        Assert.Equal(SegmentState.Retired, info.State);
        Assert.True(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.retired").Any());
    }

    [Fact]
    public async Task K_X_AlreadyCommitted_ContinuesRetirement()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-k@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));
        var compact = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.True(engine.Journal.TryGetCompaction(compact.CompactionId, out var snap));
        Assert.True(snap.Committed);
        Assert.Null(snap.Retired);

        var openBefore = engine.Journal.EnumerateOpenCompactions().Count;
        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.True(result.CompactionAttempted);
        Assert.True(result.RetirementAttempted);
        Assert.True(result.Reclaimed);
        Assert.Equal(compact.CompactionId, result.CompactionId);
        Assert.True(engine.Journal.EnumerateOpenCompactions().Count <= openBefore);
    }

    [Fact]
    public async Task L_Y_AlreadyRetired_Reclaims()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-l@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);
        var coordinator = CreateCoordinator(engine);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, result.Outcome);
        Assert.Equal(sourceId, result.SegmentId);
        Assert.False(result.RetirementAttempted);
        Assert.True(result.Reclaimed);
    }

    [Fact]
    public async Task M_OpenCompaction_Continues_NoCompetingBegin()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-m@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Catalogue.TryGet(sourceId, out var info));

        var compactionId = engine.Journal.AllocateCompactionId();
        var begin = await engine.Journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, compactionId, sourceId, info.Generation),
            CancellationToken.None);
        Assert.Equal(JournalAppendOutcome.Applied, begin);
        Assert.Single(engine.Journal.EnumerateOpenCompactions());

        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(compactionId, result.CompactionId);
        Assert.True(result.CompactionAttempted);
        Assert.Equal(1, engine.Journal.EnumerateCompactions().Count(c => c.Begin.SourceSegmentId.Value == sourceId.Value));
    }

    [Fact]
    public async Task N_CompetingOpenCompactions_SurfacedAsFailed()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-n@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
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

        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Failed, result.Outcome);
        Assert.True(result.CompactionAttempted);
        Assert.Contains("multiple-uncommitted", result.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task O_EvictionDuringCompaction_Preserved()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-o@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        engine.TestHookBeforeRelocateArticle = _ => Assert.True(engine.TryEvict(record.ArtId));
        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Evicted, after.State);
    }

    [Fact]
    public async Task P_StaleBeforeReclamation_Skipped()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-p@seg.test>");
        var phantom = CreateRecord("<mnt-p-phantom@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);
        var coordinator = CreateCoordinator(engine);
        coordinator.TestHookAfterReclamationVictimSelected = id =>
        {
            Assert.Equal(sourceId, id);
            // Inject Present@retired so revalidation refuses.
            Assert.True(engine.Index.TryCommitPresent(new StoredArticleMetadata(
                phantom.ArtId,
                phantom.ArtHash,
                phantom.ArtSize,
                new StoredArticleLocation(sourceId, 0, phantom.ArtSize),
                ArticleStorageState.Present,
                DateTimeOffset.UtcNow,
                1UL)));
        };

        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Skipped, result.Outcome);
        Assert.False(result.Reclaimed);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out _));
    }

    [Fact]
    public async Task Q_Cancellation_NoFalseCompletion()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-q@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));

        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.RunOnceAsync(cts.Token));
    }

    [Fact]
    public async Task R_S_DeterministicSelection_AtMostOneTarget()
    {
        using var dir = TempStorageDir.Create();
        var a = CreateRecord("<mnt-r-a@seg.test>");
        var b = CreateRecord("<mnt-r-b@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var retiredA = await AcceptCloseCompactRetireAsync(engine, a);
        var retiredB = await AcceptCloseCompactRetireAsync(engine, b);
        var expected = retiredA.Value < retiredB.Value ? retiredA : retiredB;

        var coordinator = CreateCoordinator(engine);
        var first = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, first.Outcome);
        Assert.Equal(expected, first.SegmentId);

        var second = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, second.Outcome);
        Assert.NotEqual(first.SegmentId, second.SegmentId);
    }

    [Fact]
    public void T_U_NoWorkerTypes_PolicyReadOnly()
    {
        var coordinatorType = typeof(StorageMaintenanceCoordinator);
        Assert.DoesNotContain(
            coordinatorType.Assembly.GetTypes(),
            t => typeof(Microsoft.Extensions.Hosting.BackgroundService).IsAssignableFrom(t)
                 && t.Namespace is not null
                 && t.Namespace.Contains("Maintenance", StringComparison.Ordinal));
        Assert.Null(coordinatorType.GetMethod("StartAsync"));
        Assert.Null(coordinatorType.GetMethod("ExecuteAsync"));
    }

    [Fact]
    public async Task V_CacheUntouchedByCoordinator()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-v@seg.test>");
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryRead(record.ArtId, out _));
        var puts = cache.PutCount;
        var removes = cache.RemoveCount;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));
        // Evict may remove cache; snapshot after eviction baseline.
        puts = cache.PutCount;
        removes = cache.RemoveCount;

        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(puts, cache.PutCount);
        Assert.Equal(removes, cache.RemoveCount);
    }

    [Fact]
    public async Task W_Z_AccountingViaPrimitives_NoSataScan()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<mnt-w@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var before));

        var coordinator = CreateCoordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.False(engine.Segments.TryGetSegmentInfo(sourceId, out _));
        Assert.True(before.SizeBytes > 0);
    }

    private static StorageMaintenanceCoordinator CreateCoordinator(
        FileArticleStorageEngine engine,
        long minimumDeadBytes = ArticleCompactionPolicyOptions.DefaultMinimumDeadBytes,
        double minimumDeadRatio = ArticleCompactionPolicyOptions.DefaultMinimumDeadRatio) =>
        new(engine, new ArticleSegmentPolicy(enabled: true, minimumDeadBytes, minimumDeadRatio));

    private static async Task<SegmentId> AcceptCloseCompactRetireAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
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
        _ = builder.Append("Subject: maintenance\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-mnt-" + Guid.NewGuid().ToString("N"));
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

    private sealed class RecordingArticleMemoryCache : IArticleMemoryCache
    {
        private long _put;
        private long _remove;

        public RecordingArticleMemoryCache(IArticleMemoryCache inner) => Inner = inner;

        public IArticleMemoryCache Inner { get; }

        public long MaxBytes => Inner.MaxBytes;

        public long CurrentBytes => Inner.CurrentBytes;

        public int Count => Inner.Count;

        public long PutCount => Interlocked.Read(ref _put);

        public long RemoveCount => Interlocked.Read(ref _remove);

        public bool TryGet(ArticleId artId, out ArticleRecord record) => Inner.TryGet(artId, out record);

        public ArticleMemoryCachePutOutcome Put(in ArticleRecord record)
        {
            _ = Interlocked.Increment(ref _put);
            return Inner.Put(in record);
        }

        public bool Remove(ArticleId artId)
        {
            _ = Interlocked.Increment(ref _remove);
            return Inner.Remove(artId);
        }

        public void Clear() => Inner.Clear();
    }
}
