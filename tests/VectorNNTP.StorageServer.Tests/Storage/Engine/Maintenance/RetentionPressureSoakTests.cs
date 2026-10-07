using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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

/// <summary>
/// End-to-end retention pressure. These tests use the real journal, index, segments, and
/// coordinator. The capacity reader is mutable so the logical volume can cross watermarks
/// without filling the developer disk.
/// </summary>
public sealed class RetentionPressureSoakTests
{
    private const long Total = 10_000_000;

    private static readonly DateTimeOffset Arrival = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    [Fact]
    public async Task Normal_accepts_and_publishes_without_false_recovery()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 1_000_000);
        await using var engine = Open(dir, reader, time);
        var first = await PublishAsync(engine, "<soak-normal-a@example>");
        var second = await PublishAsync(engine, "<soak-normal-b@example>");
        Assert.True(engine.Journal.OutstandingRecoverableBytes >= 0);

        var result = await Coordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(nameof(BulkStoragePressureState.Normal), result.Recovery!.Value.PressureStateBefore);
        Assert.Equal(0, result.Recovery.Value.AgeArticlesExpired);
        Assert.Equal(0, result.Recovery.Value.PressureArticlesExpired);
        Assert.Equal(0, result.Recovery.Value.LogicalExpiredBytes);
        Assert.Equal(0, result.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.Equal(0, result.Recovery.Value.FullyDeadSegmentsReclaimed);
        Assert.Equal(ArticleStorageState.Present, State(engine, first.ArtId));
        Assert.Equal(ArticleStorageState.Present, State(engine, second.ArtId));
        Assert.True(engine.TryRead(first.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(first.ArtData.Span));
        Assert.True(engine.TryRead(second.ArtId, out _));
    }

    [Fact]
    public async Task Warning_does_not_expire_and_age_policy_stays_independent()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 1_000_000);
        await using var engine = Open(dir, reader, time);
        var aged = await PublishAsync(engine, "<soak-warn-age@example>");
        time.Advance(TimeSpan.FromHours(2));
        var kept = await PublishAsync(engine, "<soak-warn-kept@example>");
        reader.UsedBytes = 7_600_000;

        var result = await Coordinator(engine, TimeSpan.FromHours(1)).RunOnceAsync(CancellationToken.None);

        Assert.Equal(nameof(BulkStoragePressureState.Warning), result.Recovery!.Value.PressureStateBefore);
        Assert.Equal(0, result.Recovery.Value.PressureArticlesExpired);
        Assert.True(result.Recovery.Value.AgeArticlesExpired >= 1);
        Assert.Equal(ArticleStorageState.Evicted, State(engine, aged.ArtId));
        Assert.Equal(ArticleStorageState.Present, State(engine, kept.ArtId));
        Assert.True(engine.TryRead(kept.ArtId, out _));
        Assert.False(engine.TryRead(aged.ArtId, out _));
        Assert.Equal(0, result.Recovery.Value.NetPhysicalRecoveryBytes);
    }

    [Fact]
    public async Task Pressure_expires_old_articles_and_a_later_cycle_reclaims_the_file_once()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time);
        var cold = await PublishAsync(engine, "<soak-cold@example>");
        var unread = await PublishAsync(engine, "<soak-unread@example>");
        time.Advance(TimeSpan.FromHours(1));
        Assert.True(engine.TryRead(cold.ArtId, out _));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        time.Advance(Grace);
        var recent = await PublishAsync(engine, "<soak-recent@example>");
        engine.SuspendBackgroundPersist = true;
        var ingress = Article("<soak-ingress@example>");
        var accepted = await engine.AcceptAsync(ingress, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.True(accepted.Sequence > 0);
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        reader.UsedBytes = 8_200_000;
        var closed = Location(engine, cold.ArtId).SegmentId;
        Assert.True(engine.Catalogue.TryGet(closed, out var segment));

        var logical = await Coordinator(engine, minimumDeadRatio: 99).RunOnceAsync(CancellationToken.None);

        Assert.Equal(nameof(BulkStoragePressureState.Pressure), logical.Recovery!.Value.PressureStateBefore);
        Assert.Equal(1, logical.Recovery.Value.PressureArticlesExpired);
        Assert.True(logical.Recovery.Value.LogicalExpiredBytes > 0);
        Assert.Equal(0, logical.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.Equal(0, logical.Recovery.Value.FullyDeadBytesReclaimed);
        Assert.NotEqual(logical.Recovery.Value.LogicalExpiredBytes, logical.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.Equal(ArticleStorageState.Evicted, State(engine, cold.ArtId));
        Assert.Equal(ArticleStorageState.Present, State(engine, unread.ArtId));
        Assert.Equal(ArticleStorageState.Present, State(engine, recent.ArtId));
        Assert.True(engine.TryRead(ingress.ArtId, out _));
        Assert.True(engine.TryRead(recent.ArtId, out _));
        Assert.False(engine.TryRead(cold.ArtId, out _));
        Assert.Equal(outstanding, engine.Journal.OutstandingRecoverableBytes);
        Assert.True(File.Exists(ClosedPath(engine, closed)));

        reader.UsedBytes = 8_700_000;
        var coordinator = Coordinator(engine, minimumDeadRatio: 99);
        coordinator.TestHookBeforeFullyDeadReclaim = id =>
        {
            Assert.True(engine.Catalogue.TryGet(id, out var info));
            reader.UsedBytes -= info.SizeBytes;
        };
        var physical = await coordinator.RunOnceAsync(CancellationToken.None);
        var again = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.True(physical.Recovery!.Value.LogicalExpiredBytes > 0);
        Assert.Equal(1, physical.Recovery.Value.FullyDeadSegmentsReclaimed);
        Assert.Equal(segment.SizeBytes, physical.Recovery.Value.FullyDeadBytesReclaimed);
        Assert.Equal(segment.SizeBytes, physical.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.True(physical.Recovery.Value.FreeBytesAfter > physical.Recovery.Value.FreeBytesBefore);
        Assert.True(physical.Recovery.Value.PressureImproved);
        Assert.False(engine.Catalogue.TryGet(closed, out _));
        Assert.False(engine.Index.TryGet(cold.ArtId, out _));
        Assert.False(engine.Index.TryGet(unread.ArtId, out _));
        Assert.Equal(ArticleStorageState.Present, State(engine, recent.ArtId));
        Assert.True(engine.TryRead(recent.ArtId, out _));
        Assert.True(engine.TryRead(ingress.ArtId, out _));
        Assert.Equal(0, again.Recovery!.Value.FullyDeadBytesReclaimed);
        Assert.Equal(0, again.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.True(physical.Recovery.Value.MaintenanceDurationMilliseconds < 5_000);
    }

    [Fact]
    public async Task Low_density_rewrite_under_pressure_reclaims_the_source_once()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 8_200_000);
        await using var engine = Open(dir, reader, time);
        var keep = await PublishAsync(engine, "<soak-rewrite-keep@example>");
        var drop = await PublishAsync(engine, "<soak-rewrite-drop@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        var source = Location(engine, keep.ArtId).SegmentId;
        Assert.True(engine.Catalogue.TryGet(source, out var before));
        Assert.True(engine.TryRead(keep.ArtId, out _));

        var result = await Coordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        var recovery = result.Recovery!.Value;
        Assert.Equal(0, recovery.PressureArticlesExpired);
        Assert.Equal(before.LiveBytes, recovery.CompactionDestinationBytesWritten);
        Assert.Equal(before.LiveBytes, recovery.CompactionBytesRelocated);
        Assert.Equal(before.SizeBytes, recovery.CompactionSourceBytesReclaimed);
        Assert.Equal(before.SizeBytes - before.LiveBytes, recovery.NetPhysicalRecoveryBytes);
        Assert.NotEqual(
            recovery.CompactionSourceBytesReclaimed + recovery.CompactionBytesRelocated,
            recovery.NetPhysicalRecoveryBytes);
        Assert.True(engine.TryRead(keep.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(keep.ArtData.Span));
        Assert.False(engine.Catalogue.TryGet(source, out _));
        Assert.False(engine.TryRead(drop.ArtId, out _));
    }

    [Fact]
    public async Task High_pressure_withholds_a_rewrite_that_would_consume_the_reserves()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 8_700_000);
        await using var engine = Open(dir, reader, time);
        var keep = await PublishAsync(engine, "<soak-withhold-keep@example>");
        var drop = await PublishAsync(engine, "<soak-withhold-drop@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        var source = Location(engine, keep.ArtId).SegmentId;

        var result = await Coordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.True(result.BulkRewriteSuppressed);
        Assert.True(result.Recovery!.Value.RewriteSuppressedByReserve);
        Assert.Equal(0, result.Recovery.Value.CompactionSourceBytesReclaimed);
        Assert.Equal(0, result.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.Equal(ArticleStorageState.Present, State(engine, keep.ArtId));
        Assert.True(engine.TryRead(keep.ArtId, out _));
        Assert.True(engine.Catalogue.TryGet(source, out _));
        Assert.True(File.Exists(ClosedPath(engine, source)));
    }

    [Fact]
    public async Task Emergency_with_only_recent_articles_rejects_new_work()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time);
        var recent = await PublishAsync(engine, "<soak-protected@example>");
        engine.SuspendBackgroundPersist = true;
        var ingress = Article("<soak-protected-ingress@example>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(ingress, CancellationToken.None)).Outcome);
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        reader.UsedBytes = 9_600_000;

        var result = await Coordinator(engine).RunOnceAsync(CancellationToken.None);
        var denied = await engine.AcceptAsync(Article("<soak-protected-new@example>"), CancellationToken.None);

        Assert.Equal(nameof(BulkStoragePressureState.Emergency), result.Recovery!.Value.PressureStateAfter);
        Assert.Equal(0, result.Recovery.Value.PressureArticlesExpired);
        Assert.True(result.Recovery.Value.PressureArticlesEvaluated >= 1);
        Assert.Equal(0, result.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.False(result.Recovery.Value.PressureImproved);
        Assert.Equal(ArticleStorageState.Present, State(engine, recent.ArtId));
        Assert.True(engine.TryRead(recent.ArtId, out _));
        Assert.True(engine.TryRead(ingress.ArtId, out _));
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, denied.Outcome);
        Assert.Equal(0UL, denied.Sequence);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, denied.Reason);
        Assert.False(engine.Journal.TryGetOutstanding(denied.ArtId, out _));
        Assert.Equal(outstanding, engine.Journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task Admission_resumes_after_physical_headroom_returns()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time);
        var old = await PublishAsync(engine, "<soak-resume-old@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        time.Advance(Grace);
        reader.UsedBytes = 9_600_000;
        var denied = await engine.AcceptAsync(Article("<soak-resume-denied@example>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, denied.Outcome);
        Assert.False(engine.Journal.TryGetOutstanding(denied.ArtId, out _));

        var coordinator = Coordinator(engine);
        coordinator.TestHookBeforeFullyDeadReclaim = _ => reader.UsedBytes = 5_000_000;
        var recovered = await coordinator.RunOnceAsync(CancellationToken.None);
        var resumed = await engine.AcceptAsync(Article("<soak-resume-ok@example>"), CancellationToken.None);

        Assert.True(recovered.Recovery!.Value.FullyDeadBytesReclaimed > 0);
        Assert.Equal(nameof(BulkStoragePressureState.Normal), recovered.Recovery.Value.PressureStateAfter);
        Assert.True(recovered.Recovery.Value.PressureImproved);
        Assert.Equal(ArticleAcceptOutcome.Accepted, resumed.Outcome);
        Assert.True(resumed.Sequence > 0);
        Assert.True(engine.Journal.TryGetOutstanding(resumed.ArtId, out _));
        Assert.False(engine.TryRead(old.ArtId, out _));
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryRead(resumed.ArtId, out _));
    }

    [Fact]
    public async Task Concurrent_accepts_at_the_reserve_boundary_do_not_both_ack()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var probe = Article("<soak-boundary-size@example>");
        var copy = SegmentRecordCodec.RecordLengthForArtSize(probe.ArtSize);
        var reserve = new BulkStoragePressurePolicy().Evaluate(Total, 0, Total).RecoveryReserveBytes;
        var free = reserve + copy;
        var reader = new MutableCapacityReader(Total, Total - free);
        await using var engine = Open(dir, reader, time);
        engine.SuspendBackgroundPersist = true;
        var first = Article("<soak-boundary-a@example>");
        var second = Article("<soak-boundary-b@example>");

        var results = await Task.WhenAll(
            engine.AcceptAsync(first, CancellationToken.None),
            engine.AcceptAsync(second, CancellationToken.None));

        var accepted = results.Where(result => result.Outcome == ArticleAcceptOutcome.Accepted).ToArray();
        var rejected = results.Where(result => result.Outcome == ArticleAcceptOutcome.RejectedCapacity).ToArray();
        Assert.Single(accepted);
        Assert.Single(rejected);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, rejected[0].Reason);
        Assert.True(accepted[0].Sequence > 0);
        Assert.Equal(0UL, rejected[0].Sequence);
        Assert.True(engine.Journal.TryGetOutstanding(accepted[0].ArtId, out _));
        Assert.False(engine.Journal.TryGetOutstanding(rejected[0].ArtId, out _));
        Assert.True(engine.TryRead(accepted[0].ArtId, out _));
        Assert.False(engine.TryRead(rejected[0].ArtId, out _));
    }

    [Fact]
    public async Task Repeated_cycles_follow_hysteresis_and_do_not_revive_expired_articles()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time);
        var records = new ArticleRecord[4];
        for (var i = 0; i < records.Length; i++)
        {
            records[i] = await PublishAsync(engine, $"<soak-osc-{i}@example>");
        }

        time.Advance(Grace);
        reader.UsedBytes = 8_200_000;
        var coordinator = Coordinator(engine);
        var first = await coordinator.RunOnceAsync(CancellationToken.None);
        var presentAfterHigh = CountPresent(engine, records);
        var second = await coordinator.RunOnceAsync(CancellationToken.None);
        reader.UsedBytes = 7_800_000;
        var winding = await coordinator.RunOnceAsync(CancellationToken.None);
        reader.UsedBytes = 7_000_000;
        var stopped = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(8, ArticlePressureRetentionPolicy.HighExpirationLimit);
        Assert.True(first.Recovery!.Value.PressureArticlesExpired >= 1);
        Assert.True(presentAfterHigh < records.Length);
        Assert.True(CountPresent(engine, records) <= presentAfterHigh);
        Assert.True(winding.Recovery!.Value.PressureArticlesExpired <= 1);
        Assert.Equal(0, stopped.Recovery!.Value.PressureArticlesExpired);
        Assert.Equal(nameof(BulkStoragePressureState.Normal), stopped.Recovery.Value.PressureStateAfter);
        foreach (var record in records)
        {
            if (engine.Index.TryGet(record.ArtId, out var row) && row.State == ArticleStorageState.Evicted)
            {
                Assert.False(engine.TryRead(record.ArtId, out _));
            }
        }

        _ = second;
    }

    [Fact]
    public async Task Restart_keeps_acked_ingress_evicted_rows_and_completed_reclaim()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 1_000_000);
        var ingress = Article("<soak-restart-ingress@example>");

        await using (var engine = Open(dir, reader, time))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(ingress, CancellationToken.None)).Outcome);
        }

        await using (var restarted = Open(dir, reader, time))
        {
            restarted.SuspendBackgroundPersist = true;
            Assert.True(restarted.TryRead(ingress.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(ingress.ArtData.Span));
            Assert.True(restarted.Journal.TryGetOutstanding(ingress.ArtId, out _));
        }

        SegmentId expiredSegment;
        long expiredSize;
        ArticleRecord kept;
        ArticleRecord doomed;
        await using (var engine = Open(dir, reader, time))
        {
            doomed = await PublishAsync(engine, "<soak-restart-doomed@example>");
            await engine.DrainPendingAsync(CancellationToken.None);
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromHours(2));
            kept = await PublishAsync(engine, "<soak-restart-published@example>");
            expiredSegment = Location(engine, doomed.ArtId).SegmentId;
            var aged = engine.ExpireRetentionBatch(TimeSpan.FromHours(1), CancellationToken.None);
            Assert.True(aged.Expired >= 1);
            Assert.True(aged.BytesExpired > 0);
            Assert.Equal(ArticleStorageState.Evicted, State(engine, doomed.ArtId));
            Assert.Equal(ArticleStorageState.Present, State(engine, kept.ArtId));
            Assert.True(engine.Catalogue.TryGet(expiredSegment, out var info));
            expiredSize = info.SizeBytes;
        }

        await using (var restarted = Open(dir, reader, time))
        {
            restarted.SuspendBackgroundPersist = true;
            Assert.True(restarted.TryRead(kept.ArtId, out _));
            Assert.Equal(ArticleStorageState.Present, State(restarted, kept.ArtId));
            Assert.False(restarted.TryRead(doomed.ArtId, out _));
            Assert.True(restarted.Catalogue.TryGet(expiredSegment, out _));
            Assert.True(File.Exists(ActivePath(restarted, expiredSegment)) || File.Exists(ClosedPath(restarted, expiredSegment)));
            await restarted.Segments.CloseActiveAsync(CancellationToken.None);
            var reclaimed = await Coordinator(restarted).RunOnceAsync(CancellationToken.None);
            Assert.Equal(expiredSize, reclaimed.Recovery!.Value.FullyDeadBytesReclaimed);
            Assert.False(restarted.Catalogue.TryGet(expiredSegment, out _));
        }

        await using var final = Open(dir, reader, time);
        var idempotent = await Coordinator(final).RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, idempotent.Recovery!.Value.FullyDeadBytesReclaimed);
        Assert.Equal(0, idempotent.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.True(final.TryRead(kept.ArtId, out _));
        Assert.False(final.TryRead(doomed.ArtId, out _));
    }

    [Fact]
    public async Task Restart_after_bytes_are_appended_and_before_index_commit_remains_readable()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 1_000_000);
        var record = Article("<soak-restart-mid@example>");
        await using (var engine = Open(dir, reader, time))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            engine.TestHookAfterSataBeforePhysicalWritten = (_, _) =>
                throw new IOException("phase30-stop-before-physical-written");
            _ = await Assert.ThrowsAsync<IOException>(() => engine.DrainPendingAsync(CancellationToken.None));
        }

        await using var restarted = Open(dir, reader, time);
        restarted.SuspendBackgroundPersist = true;
        Assert.True(restarted.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Reads_during_expiration_and_reclaim_follow_logical_state()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time);
        var doomed = await PublishAsync(engine, "<soak-read-doomed@example>");
        var live = await PublishAsync(engine, "<soak-read-live@example>");
        time.Advance(Grace);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var recent = await PublishAsync(engine, "<soak-read-recent@example>");
        reader.UsedBytes = 9_200_000;
        var sawDoomed = 0;
        var sawLive = 0;
        using var stop = new CancellationTokenSource();
        var readerTask = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (engine.TryRead(live.ArtId, out ArticleReadResult _))
                {
                    Interlocked.Increment(ref sawLive);
                }

                if (engine.TryRead(recent.ArtId, out ArticleReadResult _))
                {
                    Interlocked.Increment(ref sawLive);
                }
            }
        });
        var coordinator = Coordinator(engine);
        coordinator.TestHookBeforeFullyDeadReclaim = _ =>
        {
            Assert.True(engine.TryRead(recent.ArtId, out ArticleReadResult _));
            Assert.False(engine.TryRead(doomed.ArtId, out ArticleReadResult _));
        };
        engine.TestHookBeforeRetentionExpire = candidate =>
        {
            if (candidate.ArtId == doomed.ArtId)
            {
                Assert.True(engine.TryRead(candidate.ArtId, out _));
                Interlocked.Increment(ref sawDoomed);
            }
        };

        var result = await coordinator.RunOnceAsync(CancellationToken.None);
        stop.Cancel();
        await readerTask;

        Assert.True(sawDoomed >= 1);
        Assert.True(sawLive >= 1);
        Assert.True(result.Recovery!.Value.LogicalExpiredBytes > 0);
        Assert.True(result.Recovery.Value.FullyDeadBytesReclaimed > 0);
        Assert.False(engine.TryRead(doomed.ArtId, out _));
        Assert.False(engine.TryRead(live.ArtId, out _));
        Assert.True(engine.TryRead(recent.ArtId, out _));
    }

    [Fact]
    public async Task Idle_cycles_do_not_grow_index_journal_or_segment_files()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 1_000_000);
        await using var engine = Open(dir, reader, time);
        _ = await PublishAsync(engine, "<soak-bound-a@example>");
        _ = await PublishAsync(engine, "<soak-bound-b@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        var coordinator = Coordinator(engine);
        _ = await coordinator.RunOnceAsync(CancellationToken.None);
        var writes = engine.Index.DurableWriteCount;
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        var journalBytes = engine.Journal.JournalPhysicalBytes;
        var segments = engine.Catalogue.Snapshot().Count;
        var files = Directory.GetFiles(dir.Options.SegmentDir).Length;
        var useCounts = engine.Index.UseCountEntryCount;
        var slowest = 0d;

        for (var i = 0; i < 12; i++)
        {
            var cycle = await coordinator.RunOnceAsync(CancellationToken.None);
            slowest = Math.Max(slowest, cycle.Recovery!.Value.MaintenanceDurationMilliseconds);
            Assert.Equal(0, cycle.Recovery.Value.LogicalExpiredBytes);
            Assert.Equal(0, cycle.Recovery.Value.NetPhysicalRecoveryBytes);
        }

        Assert.Equal(writes, engine.Index.DurableWriteCount);
        Assert.Equal(outstanding, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(journalBytes, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(segments, engine.Catalogue.Snapshot().Count);
        Assert.Equal(files, Directory.GetFiles(dir.Options.SegmentDir).Length);
        Assert.Equal(useCounts, engine.Index.UseCountEntryCount);
        Assert.True(slowest < 5_000);
    }

    [Fact]
    public async Task Completed_retention_cycles_do_not_leave_growing_worklists()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time);
        for (var i = 0; i < 4; i++)
        {
            _ = await PublishAsync(engine, $"<soak-bound-cycle-{i}@example>");
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        time.Advance(Grace);
        reader.UsedBytes = 9_200_000;
        var coordinator = Coordinator(engine);
        coordinator.TestHookBeforeFullyDeadReclaim = segmentId =>
        {
            if (engine.Catalogue.TryGet(segmentId, out var info))
            {
                reader.UsedBytes = Math.Max(0L, reader.UsedBytes - info.SizeBytes);
            }
        };

        var completed = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.True(completed.Recovery!.Value.LogicalExpiredBytes > 0);
        Assert.True(completed.Recovery.Value.FullyDeadBytesReclaimed > 0);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        var writes = engine.Index.DurableWriteCount;
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        var journalBytes = engine.Journal.JournalPhysicalBytes;
        var segments = engine.Catalogue.Snapshot().Count;
        var files = Directory.GetFiles(dir.Options.SegmentDir).Length;
        var useCounts = engine.Index.UseCountEntryCount;
        var slowest = completed.Recovery.Value.MaintenanceDurationMilliseconds;

        for (var i = 0; i < 8; i++)
        {
            var cycle = await coordinator.RunOnceAsync(CancellationToken.None);
            slowest = Math.Max(slowest, cycle.Recovery!.Value.MaintenanceDurationMilliseconds);
            Assert.Equal(0, cycle.Recovery.Value.PressureArticlesExpired);
            Assert.Equal(0, cycle.Recovery.Value.LogicalExpiredBytes);
            Assert.Equal(0, cycle.Recovery.Value.FullyDeadBytesReclaimed);
            Assert.Equal(0, cycle.Recovery.Value.NetPhysicalRecoveryBytes);
        }

        Assert.Equal(writes, engine.Index.DurableWriteCount);
        Assert.Equal(outstanding, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(journalBytes, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(segments, engine.Catalogue.Snapshot().Count);
        Assert.Equal(files, Directory.GetFiles(dir.Options.SegmentDir).Length);
        Assert.Equal(useCounts, engine.Index.UseCountEntryCount);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.True(slowest < 5_000);
    }

    private static int CountPresent(FileArticleStorageEngine engine, ArticleRecord[] records) =>
        records.Count(record =>
            engine.Index.TryGet(record.ArtId, out var row)
            && row.State == ArticleStorageState.Present);

    private static StorageMaintenanceCoordinator Coordinator(
        FileArticleStorageEngine engine,
        TimeSpan? maxRetentionAge = null,
        int minimumDeadRatio = 10) =>
        new(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: minimumDeadRatio),
            logger: NullLogger.Instance,
            maxRetentionAge: maxRetentionAge ?? TimeSpan.Zero,
            bulkPressure: new BulkStoragePressureOptions
            {
                MinimumRetentionAge = Grace,
            });

    private static FileArticleStorageEngine Open(TempDir dir, IStorageCapacityReader reader, TimeProvider time) =>
        FileArticleStorageEngine.Open(
            dir.Options with
            {
                CapacityMaximumUtilization = 100,
                CapacityCompactionHeadroom = 0,
                CapacityMaximumUsageCapacity = 100,
                CapacityFreeCapacity = 1,
            },
            logger: NullLogger.Instance,
            timeProvider: time,
            capacityReader: reader);

    private static async Task<ArticleRecord> PublishAsync(FileArticleStorageEngine engine, string messageId)
    {
        var record = Article(messageId);
        engine.SuspendBackgroundPersist = true;
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.True(accepted.Sequence > 0);
        await engine.DrainPendingAsync(CancellationToken.None);
        return record;
    }

    private static ArticleRecord Article(string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase30\r\n\r\nbody\r\n");
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private static ArticleStorageState State(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        return row.State;
    }

    private static StoredArticleLocation Location(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        return row.Location;
    }

    private static string ActivePath(FileArticleStorageEngine engine, SegmentId segmentId) =>
        Path.Combine(engine.Segments.RootPath, SegmentFileNames.Format(segmentId, SegmentFileKind.Active));

    private static string ClosedPath(FileArticleStorageEngine engine, SegmentId segmentId) =>
        Path.Combine(engine.Segments.RootPath, SegmentFileNames.Format(segmentId, SegmentFileKind.Closed));

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

    private sealed class TempDir : IDisposable
    {
        private TempDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public ArticleStorageRuntimeOptions Options { get; }

        private string Root { get; }

        public static TempDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase30-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "control"));
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            return new TempDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: Path.Combine(root, "control"),
                    SegmentDir: Path.Combine(root, "cache"),
                    JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                    SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes,
                    MaxSegmentSealDelay: TimeSpan.Zero));
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
