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

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>Phase 5F.19: PhysicalWritten publication fence against compaction and retirement.</summary>
public sealed class PhysicalWrittenCompactionFenceTests
{
    [Fact]
    public async Task A_PhysicalWrittenOnClosedSource_DoesNotCommit()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptPhysicalWrittenWithoutIndexAsync(engine, "<pw-a@seg.test>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        var compact = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);

        Assert.Equal(ArticleCompactionOutcome.Incomplete, compact.Outcome);
        Assert.Equal("pending-physical-written", compact.Reason);
        Assert.False(compact.CompactionCommittedAppended);
        Assert.True(engine.Journal.TryGetCompaction(compact.CompactionId, out var snap));
        Assert.False(snap.Committed);
        Assert.Equal(0, CountPresent(engine, sourceId));
        Assert.Contains(
            engine.Journal.EnumerateIncomplete(),
            s => s.PhysicalWritten is not null
                 && s.PhysicalWritten.Value.Location.SegmentId.Value == sourceId.Value);
    }

    [Fact]
    public async Task B_PhysicalWrittenBecomesPresent_ThenRelocationCommits()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<pw-b@seg.test>");
        var sourceId = await AcceptPhysicalWrittenWithoutIndexAsync(engine, "<pw-b@seg.test>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var blocked = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal("pending-physical-written", blocked.Reason);

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.None;
        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var published));
        Assert.Equal(ArticleStorageState.Present, published.State);
        Assert.Equal(sourceId.Value, published.Location.SegmentId.Value);

        var compact = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.Equal(blocked.CompactionId, compact.CompactionId);
        Assert.True(engine.Index.TryGet(record.ArtId, out var moved));
        Assert.Equal(ArticleStorageState.Present, moved.State);
        Assert.NotEqual(sourceId.Value, moved.Location.SegmentId.Value);
        Assert.Equal(0, CountPresent(engine, sourceId));
    }

    [Fact]
    public async Task C_MultiplePhysicalWritten_AllBlockCompletion()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptPhysicalWrittenWithoutIndexAsync(engine, "<pw-c1@seg.test>");
        var second = await engine.AcceptAsync(CreateRecord("<pw-c2@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, second.Outcome);
        var first = engine.Journal.EnumerateIncomplete().Single(s => s.PhysicalWritten is not null);
        var pw = await engine.Journal.AppendPhysicalWrittenAsync(
            new JournalPhysicalWrittenRecord(1, second.Sequence, first.PhysicalWritten!.Value.Location),
            CancellationToken.None);
        Assert.Equal(JournalAppendOutcome.Applied, pw);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        var compact = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);

        Assert.Equal(ArticleCompactionOutcome.Incomplete, compact.Outcome);
        Assert.Equal("pending-physical-written", compact.Reason);
        Assert.False(compact.CompactionCommittedAppended);
        Assert.Equal(2, engine.Journal.EnumerateIncomplete().Count(s => s.PhysicalWritten is not null));
    }

    [Fact]
    public async Task D_EvictedAtPhysicalWrittenLocation_DoesNotResurrect()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<pw-d@seg.test>");
        await AcceptPresentWithoutIndexCommittedAsync(engine, record);
        Assert.True(engine.TryEvict(record.ArtId));

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.None;
        await engine.RecoverAsync(CancellationToken.None);

        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Evicted, meta.State);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task E_InvalidAtPhysicalWrittenLocation_DoesNotResurrect()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<pw-e@seg.test>");
        await AcceptPresentWithoutIndexCommittedAsync(engine, record);
        Assert.True(engine.TryInvalidate(record.ArtId));

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.None;
        await engine.RecoverAsync(CancellationToken.None);

        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Invalid, meta.State);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task F_PresentElsewhere_DoesNotMoveIndexBackward()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<pw-f@seg.test>");
        await AcceptPresentWithoutIndexCommittedAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var atSource));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.None;

        var compact = await engine.CompactClosedSegmentAsync(atSource.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var moved));
        Assert.NotEqual(atSource.Location.SegmentId.Value, moved.Location.SegmentId.Value);

        await engine.RecoverAsync(CancellationToken.None);

        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Present, after.State);
        Assert.Equal(moved.Location.SegmentId.Value, after.Location.SegmentId.Value);
        Assert.Equal(moved.Location.Offset, after.Location.Offset);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task G_CorruptPhysicalWritten_FailClosed()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        engine.TestRewritePhysicalLocationAfterAppend = location =>
            location with { Offset = location.Offset + location.Length + 8 };
        var record = CreateRecord("<pw-g@seg.test>");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.RecoverAsync(CancellationToken.None));
        Assert.Contains("integrity proof", ex.Message, StringComparison.Ordinal);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        Assert.NotEmpty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task H_PublicationInFlight_BlocksRetirement()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        string? retireReason = null;
        engine.TestHookAfterPublicationEnteredBeforeIndexCommit = segmentId =>
        {
            Assert.True(engine.Catalogue.TryGet(segmentId, out var info));
            var compactionId = engine.Journal.AllocateCompactionId();
            Assert.Equal(
                JournalAppendOutcome.Applied,
                engine.Journal.AppendCompactionBeginAsync(
                    new JournalCompactionBeginRecord(1, compactionId, segmentId, info.Generation),
                    CancellationToken.None).AsTask().GetAwaiter().GetResult());
            Assert.Equal(
                JournalAppendOutcome.Applied,
                engine.Journal.AppendCompactionCommittedAsync(
                    new JournalCompactionCommittedRecord(1, compactionId),
                    CancellationToken.None).AsTask().GetAwaiter().GetResult());
            var retire = engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            retireReason = retire.Reason;
            Assert.Equal(ArticleSegmentRetirementOutcome.RejectedPresentRemain, retire.Outcome);
            Assert.False(retire.CompactionRetiredAppended);
        };

        var record = CreateRecord("<pw-h@seg.test>");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal("index-publication-in-flight", retireReason);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        Assert.NotEqual(SegmentState.Retired, info.State);
    }

    [Fact]
    public async Task I_CompactionRetired_RecoveryPresent_DoesNotRename()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<pw-i@seg.test>");
        var sourceId = await AcceptPhysicalWrittenWithoutIndexAsync(engine, "<pw-i@seg.test>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var compactionId = await ForceCommittedAsync(engine, sourceId);
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out var forced));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionRetiredAsync(
                new JournalCompactionRetiredRecord(
                    1,
                    compactionId,
                    sourceId,
                    forced.Begin.SourceGeneration),
                CancellationToken.None));

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.None;
        await engine.RecoverAsync(CancellationToken.None);

        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(sourceId.Value, meta.Location.SegmentId.Value);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var after));
        Assert.Equal(SegmentState.Closed, after.State);
    }

    [Fact]
    public async Task J_CompactionRetiredWithoutPublishableWork_Renames()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<pw-j@seg.test>");
        SegmentId sourceId;
        ulong compactionId;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            sourceId = meta.Location.SegmentId;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.TryEvict(record.ArtId));
            var compact = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
            Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
            compactionId = compact.CompactionId;
            engine.TestRetirementFaultPoint =
                FileArticleStorageEngine.RetirementFaultPoint.AfterCompactionRetiredBeforeCatalogue;
            await Assert.ThrowsAsync<IOException>(() =>
                engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None));
            Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var closed));
            Assert.Equal(SegmentState.Closed, closed.State);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.True(restarted.Segments.TryGetSegmentInfo(sourceId, out var retired));
        Assert.Equal(SegmentState.Retired, retired.State);
        Assert.Equal(0, CountPresent(restarted, sourceId));
    }

    [Fact]
    public async Task K_CommittedWithPresent_ResumesSameCompactionId()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<pw-k@seg.test>");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var compactionId = await ForceCommittedAsync(engine, meta.Location.SegmentId);
        var coordinator = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(enabled: true, minimumDeadBytes: 0, minimumDeadRatio: 0));

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(compactionId, result.CompactionId);
        Assert.True(engine.Index.TryGet(record.ArtId, out var moved));
        Assert.Equal(ArticleStorageState.Present, moved.State);
        Assert.NotEqual(meta.Location.SegmentId.Value, moved.Location.SegmentId.Value);
        Assert.False(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out _));
    }

    [Fact]
    public async Task L_CommittedWithPhysicalWritten_DoesNotRetireUntilResolved()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<pw-l@seg.test>");
        var sourceId = await AcceptPhysicalWrittenWithoutIndexAsync(engine, "<pw-l@seg.test>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var compactionId = await ForceCommittedAsync(engine, sourceId);
        var coordinator = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(enabled: true, minimumDeadBytes: 0, minimumDeadRatio: 0));

        var blocked = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Skipped, blocked.Outcome);
        Assert.Equal("pending-physical-written", blocked.SkipReason);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var stillClosed));
        Assert.Equal(SegmentState.Closed, stillClosed.State);
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out var snap));
        Assert.Null(snap.Retired);

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.None;
        await engine.RecoverAsync(CancellationToken.None);
        var finished = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, finished.Outcome);
        Assert.Equal(compactionId, finished.CompactionId);
        Assert.True(engine.Index.TryGet(record.ArtId, out var moved));
        Assert.NotEqual(sourceId.Value, moved.Location.SegmentId.Value);
    }

    [Fact]
    public async Task M_PendingPhysicalWrittenYieldsToOtherClosedVictim()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var victim = CreateRecord("<pw-m-victim@seg.test>");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(victim, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(victim.ArtId, out var victimMeta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(victim.ArtId));

        var blockedSource = await AcceptPhysicalWrittenWithoutIndexAsync(engine, "<pw-m-blocked@seg.test>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var blocked = await engine.CompactClosedSegmentAsync(blockedSource, CancellationToken.None);
        Assert.Equal("pending-physical-written", blocked.Reason);

        var coordinator = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(enabled: true, minimumDeadBytes: 0, minimumDeadRatio: 0));
        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(victimMeta.Location.SegmentId, result.SegmentId);
        Assert.Equal(blocked.CompactionId, result.DeferredOpenCompactionId);
        Assert.Equal("pending-physical-written", result.DeferredOpenSkipReason);
        Assert.True(engine.Segments.TryGetSegmentInfo(blockedSource, out var still));
        Assert.Equal(SegmentState.Closed, still.State);
        Assert.False(engine.Journal.TryGetCompaction(blocked.CompactionId, out var snap) && snap.Committed);
    }

    [Fact]
    public async Task N_PrePhysicalWrittenMarker_BlocksRetirementUntilJournal()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        string? reason = null;
        engine.TestHookAfterSataBeforePhysicalWritten = (_, location) =>
        {
            Assert.True(engine.Catalogue.TryGet(location.SegmentId, out var info));
            var compactionId = engine.Journal.AllocateCompactionId();
            Assert.Equal(
                JournalAppendOutcome.Applied,
                engine.Journal.AppendCompactionBeginAsync(
                    new JournalCompactionBeginRecord(1, compactionId, location.SegmentId, info.Generation),
                    CancellationToken.None).AsTask().GetAwaiter().GetResult());
            Assert.Equal(
                JournalAppendOutcome.Applied,
                engine.Journal.AppendCompactionCommittedAsync(
                    new JournalCompactionCommittedRecord(1, compactionId),
                    CancellationToken.None).AsTask().GetAwaiter().GetResult());
            var retire = engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            reason = retire.Reason;
            Assert.Equal(ArticleSegmentRetirementOutcome.RejectedPresentRemain, retire.Outcome);
            Assert.NotEqual(SegmentState.Retired, info.State);
        };

        var record = CreateRecord("<pw-n@seg.test>");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal("pending-inflight-append", reason);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task O_RestartAfterCommittedAndPhysicalWritten_PublishesPresent()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<pw-o@seg.test>");
        SegmentId sourceId;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            sourceId = await AcceptPhysicalWrittenWithoutIndexAsync(engine, "<pw-o@seg.test>");
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            _ = await ForceCommittedAsync(engine, sourceId);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.True(restarted.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(sourceId.Value, meta.Location.SegmentId.Value);
        Assert.True(restarted.Segments.TryGetSegmentInfo(sourceId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
    }

    [Fact]
    public async Task P_RestartAfterCompactionRetiredAndPhysicalWritten_StaysClosed()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<pw-p@seg.test>");
        SegmentId sourceId;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            sourceId = await AcceptPhysicalWrittenWithoutIndexAsync(engine, "<pw-p@seg.test>");
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            var compactionId = await ForceCommittedAsync(engine, sourceId);
            Assert.True(engine.Journal.TryGetCompaction(compactionId, out var forced));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engine.Journal.AppendCompactionRetiredAsync(
                    new JournalCompactionRetiredRecord(
                        1,
                        compactionId,
                        sourceId,
                        forced.Begin.SourceGeneration),
                    CancellationToken.None));
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.True(restarted.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.True(restarted.Segments.TryGetSegmentInfo(sourceId, out var after));
        Assert.Equal(SegmentState.Closed, after.State);
        var reclaim = await restarted.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.RejectedPresentRemain, reclaim.Outcome);
        Assert.False(reclaim.PhysicalFileDeleted);
    }

    [Fact]
    public async Task Q_RestartAfterLegalRename_StaysRetired()
    {
        using var dir = TempStorageDir.Create();
        SegmentId sourceId;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            var record = CreateRecord("<pw-q@seg.test>");
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            sourceId = meta.Location.SegmentId;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.TryEvict(record.ArtId));
            var compact = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
            Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
            var retire = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
            Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retire.Outcome);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.True(restarted.Segments.TryGetSegmentInfo(sourceId, out var info));
        Assert.Equal(SegmentState.Retired, info.State);
        var reclaim = await restarted.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaim.Outcome);
    }

    [Fact]
    public async Task R_ReconciliationRelocation_UsesCompactionReservation()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, 0.80, 0.10),
            capacityReader: capacity);
        var record = CreateRecord("<pw-r@seg.test>");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        _ = await ForceCommittedAsync(engine, meta.Location.SegmentId);
        var sawReservation = false;
        engine.TestHookAfterCompactionCapacityReserved = () =>
        {
            sawReservation = engine.ObserveCapacityAdmissionPressure().CompactionReservedBytes > 0;
        };

        var compact = await engine.CompactClosedSegmentAsync(meta.Location.SegmentId, CancellationToken.None);

        Assert.True(sawReservation);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.Equal(0, engine.ObserveCapacityAdmissionPressure().CompactionReservedBytes);
    }

    [Fact]
    public async Task S_CapacityRejection_DoesNotRetireSource()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, 0.80, 0.10),
            capacityReader: capacity);
        var record = CreateRecord("<pw-s@seg.test>");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var compactionId = await ForceCommittedAsync(engine, meta.Location.SegmentId);
        capacity.UsedBytes = capacity.TotalBytes;

        var compact = await engine.CompactClosedSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Incomplete, compact.Outcome);
        Assert.Contains("capacity", compact.Reason ?? string.Empty, StringComparison.Ordinal);
        var retire = await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.RejectedPresentRemain, retire.Outcome);
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
    }

    private static async Task<SegmentId> AcceptPhysicalWrittenWithoutIndexAsync(
        FileArticleStorageEngine engine,
        string messageId)
    {
        engine.SuspendBackgroundPersist = true;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeIndexCommit;
        engine.TestPersistFaultExceptionKind = FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
        var record = CreateRecord(messageId);
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.RecoverAsync(CancellationToken.None));
        var incomplete = engine.Journal.EnumerateIncomplete()
            .Single(s => s.Accept.ArtId == record.ArtId);
        Assert.NotNull(incomplete.PhysicalWritten);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        return incomplete.PhysicalWritten!.Value.Location.SegmentId;
    }

    private static async Task AcceptPresentWithoutIndexCommittedAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        engine.SuspendBackgroundPersist = true;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterIndexCommit;
        engine.TestPersistFaultExceptionKind = FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.RecoverAsync(CancellationToken.None));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Contains(engine.Journal.EnumerateIncomplete(), s => s.Accept.ArtId == record.ArtId);
    }

    private static async Task<ulong> ForceCommittedAsync(FileArticleStorageEngine engine, SegmentId sourceId)
    {
        Assert.True(engine.Catalogue.TryGet(sourceId, out var info));
        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, sourceId, info.Generation),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionCommittedAsync(
                new JournalCompactionCommittedRecord(1, compactionId),
                CancellationToken.None));
        return compactionId;
    }

    private static int CountPresent(FileArticleStorageEngine engine, SegmentId segmentId) =>
        engine.Index.Snapshot().Count(m =>
            m.State == ArticleStorageState.Present
            && m.Location.SegmentId.Value == segmentId.Value);

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

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: pw-fence\r\n");
        _ = builder.Append("\r\nline1\r\nline2\r\n");
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

        public long TotalBytes { get; }

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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-p5f19-" + Guid.NewGuid().ToString("N"));
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
                // best-effort
            }
        }
    }
}
