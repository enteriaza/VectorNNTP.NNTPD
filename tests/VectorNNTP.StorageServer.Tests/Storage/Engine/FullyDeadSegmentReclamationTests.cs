using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Whole-segment deletion of a closed segment that has no Present bytes.
/// A segment with any live article, and every active segment, stays on disk.
/// </summary>
public sealed class FullyDeadSegmentReclamationTests
{
    [Fact]
    public async Task Fully_dead_closed_segment_is_deleted_and_its_rows_are_forgotten()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var record = await PublishAsync(engine, "<dead@example>");
        var accepted = Accepted(engine, record.ArtId);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));
        var segmentId = SegmentOf(engine, record.ArtId);
        var info = RequireClosed(engine, segmentId);
        engine.CompleteUnreferencedExtentAccounting();
        info = RequireClosed(engine, segmentId);
        Assert.True(SegmentLifecycle.IsReclaimable(info));
        var writes = engine.Index.DurableWriteCount;
        Assert.NotEqual(DateTimeOffset.MinValue, accepted);

        var result = await engine.ReclaimFullyDeadClosedSegmentAsync(
            segmentId,
            info.Generation,
            CancellationToken.None);

        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, result.Outcome);
        Assert.True(result.PhysicalFileDeleted);
        Assert.True(result.CatalogueEntryRemoved);
        Assert.Equal("fully-dead", result.Reason);
        Assert.False(File.Exists(ClosedPath(engine, segmentId)));
        Assert.False(engine.Catalogue.TryGet(segmentId, out _));
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.Equal(writes, engine.Index.DurableWriteCount);
    }

    [Fact]
    public async Task Segment_with_one_present_article_is_not_reclaimed()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var live = await PublishAsync(engine, "<live@example>");
        var dead = await PublishAsync(engine, "<also@example>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(dead.ArtId));
        var segmentId = SegmentOf(engine, live.ArtId);
        Assert.Equal(segmentId, SegmentOf(engine, dead.ArtId));
        engine.CompleteUnreferencedExtentAccounting();
        var info = RequireClosed(engine, segmentId);
        Assert.False(SegmentLifecycle.IsReclaimable(info));

        var result = await engine.ReclaimFullyDeadClosedSegmentAsync(
            segmentId,
            info.Generation,
            CancellationToken.None);

        Assert.Equal(ArticleSegmentReclamationOutcome.RejectedPresentRemain, result.Outcome);
        Assert.False(result.PhysicalFileDeleted);
        Assert.True(File.Exists(ClosedPath(engine, segmentId)));
        Assert.True(engine.Catalogue.TryGet(segmentId, out _));
        Assert.True(engine.TryRead(live.ArtId, out var served));
        Assert.True(served.ArtData.Span.SequenceEqual(live.ArtData.Span));
        Assert.False(engine.TryRead(dead.ArtId, out _));
        Assert.True(engine.Index.TryGet(dead.ArtId, out var tombstone));
        Assert.Equal(ArticleStorageState.Evicted, tombstone.State);
    }

    [Fact]
    public async Task Fully_invalid_closed_segment_is_reclaimed()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var record = await PublishAsync(engine, "<invalid@example>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryInvalidate(record.ArtId));
        var segmentId = SegmentOf(engine, record.ArtId);
        engine.CompleteUnreferencedExtentAccounting();
        var info = RequireClosed(engine, segmentId);
        Assert.True(SegmentLifecycle.IsReclaimable(info));

        var result = await engine.ReclaimFullyDeadClosedSegmentAsync(
            segmentId,
            info.Generation,
            CancellationToken.None);

        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, result.Outcome);
        Assert.False(File.Exists(ClosedPath(engine, segmentId)));
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Active_segment_is_never_reclaimed()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var record = await PublishAsync(engine, "<active@example>");
        Assert.True(engine.TryEvict(record.ArtId));
        var segmentId = SegmentOf(engine, record.ArtId);
        Assert.True(engine.Catalogue.TryGet(segmentId, out var info));
        Assert.Equal(SegmentState.Active, info.State);
        Assert.False(SegmentLifecycle.IsReclaimable(info));

        var result = await engine.ReclaimFullyDeadClosedSegmentAsync(
            segmentId,
            info.Generation,
            CancellationToken.None);

        Assert.Equal(ArticleSegmentReclamationOutcome.RejectedActive, result.Outcome);
        Assert.False(result.PhysicalFileDeleted);
        Assert.True(engine.Catalogue.TryGet(segmentId, out info));
        Assert.Equal(SegmentState.Active, info.State);
        Assert.True(engine.Index.TryGet(record.ArtId, out var row));
        Assert.Equal(ArticleStorageState.Evicted, row.State);
        Assert.True(File.Exists(ActivePath(engine, segmentId)));
    }

    [Fact]
    public async Task Delete_failure_leaves_the_segment_retryable()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var record = await PublishAsync(engine, "<locked@example>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));
        var segmentId = SegmentOf(engine, record.ArtId);
        engine.CompleteUnreferencedExtentAccounting();
        var info = RequireClosed(engine, segmentId);
        var path = ClosedPath(engine, segmentId);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var failed = await engine.ReclaimFullyDeadClosedSegmentAsync(
                segmentId,
                info.Generation,
                CancellationToken.None);

            Assert.Equal(ArticleSegmentReclamationOutcome.Failed, failed.Outcome);
            Assert.False(failed.PhysicalFileDeleted);
            Assert.False(failed.CatalogueEntryRemoved);
            Assert.StartsWith("delete-failed:", failed.Reason, StringComparison.Ordinal);
            Assert.True(File.Exists(path));
            Assert.True(engine.Catalogue.TryGet(segmentId, out _));
            Assert.True(engine.Index.TryGet(record.ArtId, out var row));
            Assert.Equal(ArticleStorageState.Evicted, row.State);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        var retried = await engine.ReclaimFullyDeadClosedSegmentAsync(
            segmentId,
            info.Generation,
            CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, retried.Outcome);
        Assert.False(File.Exists(path));
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task Crash_before_delete_leaves_the_segment_recoverable()
    {
        using var dir = TempDir.Create();
        var options = dir.Options;
        SegmentId segmentId;
        ulong generation;
        ArticleId artId;
        await using (var engine = Open(dir))
        {
            var record = await PublishAsync(engine, "<before@example>");
            artId = record.ArtId;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.TryEvict(artId));
            segmentId = SegmentOf(engine, artId);
            engine.CompleteUnreferencedExtentAccounting();
            generation = RequireClosed(engine, segmentId).Generation;
            engine.TestReclamationFaultPoint = FileArticleStorageEngine.ReclamationFaultPoint.BeforeDelete;
            var thrown = await Assert.ThrowsAsync<IOException>(() =>
                engine.ReclaimFullyDeadClosedSegmentAsync(segmentId, generation, CancellationToken.None));
            Assert.Contains("BeforeDelete", thrown.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(ClosedPath(engine, segmentId)));
            Assert.True(engine.Catalogue.TryGet(segmentId, out _));
            Assert.True(engine.Index.TryGet(artId, out var row));
            Assert.Equal(ArticleStorageState.Evicted, row.State);
        }

        await using var restarted = FileArticleStorageEngine.Open(options, logger: NullLogger.Instance);
        Assert.True(restarted.Catalogue.TryGet(segmentId, out var restored));
        Assert.Equal(SegmentState.Closed, restored.State);
        Assert.True(restarted.Index.TryGet(artId, out var restoredRow));
        Assert.Equal(ArticleStorageState.Evicted, restoredRow.State);
        Assert.False(restarted.TryRead(artId, out _));
        var recovered = await restarted.ReclaimFullyDeadClosedSegmentAsync(
            segmentId,
            restored.Generation,
            CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, recovered.Outcome);
    }

    [Fact]
    public async Task Crash_after_physical_delete_does_not_recreate_or_serve_the_segment()
    {
        using var dir = TempDir.Create();
        var options = dir.Options;
        SegmentId segmentId;
        ulong generation;
        ArticleId artId;
        await using (var engine = Open(dir))
        {
            var record = await PublishAsync(engine, "<after-delete@example>");
            artId = record.ArtId;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.TryEvict(artId));
            segmentId = SegmentOf(engine, artId);
            engine.CompleteUnreferencedExtentAccounting();
            generation = RequireClosed(engine, segmentId).Generation;
            engine.TestReclamationFaultPoint = FileArticleStorageEngine.ReclamationFaultPoint.AfterDeleteBeforeCatalogueRemove;
            var thrown = await Assert.ThrowsAsync<IOException>(() =>
                engine.ReclaimFullyDeadClosedSegmentAsync(segmentId, generation, CancellationToken.None));
            Assert.Contains("AfterDeleteBeforeCatalogueRemove", thrown.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(ClosedPath(engine, segmentId)));
            Assert.True(engine.Catalogue.TryGet(segmentId, out _));
            Assert.True(engine.Index.TryGet(artId, out var stale));
            Assert.Equal(ArticleStorageState.Evicted, stale.State);
            Assert.False(engine.TryRead(artId, out _));
        }

        await using var restarted = FileArticleStorageEngine.Open(options, logger: NullLogger.Instance);
        Assert.False(restarted.Catalogue.TryGet(segmentId, out _));
        Assert.False(restarted.Index.TryGet(artId, out _));
        Assert.False(restarted.TryRead(artId, out _));
        Assert.False(File.Exists(ClosedPath(restarted, segmentId)));
        var again = await restarted.ReclaimFullyDeadClosedSegmentAsync(
            segmentId,
            generation,
            CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed, again.Outcome);
    }

    [Fact]
    public async Task Catalogue_removal_before_row_forget_does_not_resurrect_rows_as_present()
    {
        using var dir = TempDir.Create();
        var options = dir.Options;
        SegmentId segmentId;
        ulong generation;
        ArticleId artId;
        DateTimeOffset accepted;
        await using (var engine = Open(dir))
        {
            var record = await PublishAsync(engine, "<catalogue-first@example>");
            artId = record.ArtId;
            accepted = Accepted(engine, artId);
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.TryEvict(artId));
            segmentId = SegmentOf(engine, artId);
            engine.CompleteUnreferencedExtentAccounting();
            generation = RequireClosed(engine, segmentId).Generation;
            Assert.True(engine.Segments.TryReclaimFullyDeadClosed(segmentId, generation, out var reason));
            Assert.Null(reason);
            Assert.False(engine.Catalogue.TryGet(segmentId, out _));
            Assert.True(engine.Index.TryGet(artId, out var stale));
            Assert.Equal(ArticleStorageState.Evicted, stale.State);
            Assert.Equal(accepted, stale.AcceptedUtc);
            Assert.False(engine.TryRead(artId, out _));
        }

        await using var restarted = FileArticleStorageEngine.Open(options, logger: NullLogger.Instance);
        Assert.False(restarted.Catalogue.TryGet(segmentId, out _));
        Assert.False(restarted.Index.TryGet(artId, out _));
        Assert.False(restarted.TryRead(artId, out _));
        var cleanup = await restarted.ReclaimFullyDeadClosedSegmentAsync(
            segmentId,
            generation,
            CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed, cleanup.Outcome);
    }

    [Fact]
    public async Task Stale_generation_does_not_delete_the_segment()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var record = await PublishAsync(engine, "<stale@example>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));
        var segmentId = SegmentOf(engine, record.ArtId);
        engine.CompleteUnreferencedExtentAccounting();
        var info = RequireClosed(engine, segmentId);

        var result = await engine.ReclaimFullyDeadClosedSegmentAsync(
            segmentId,
            info.Generation + 1,
            CancellationToken.None);

        Assert.Equal(ArticleSegmentReclamationOutcome.RejectedUnexpectedPhysical, result.Outcome);
        Assert.Equal("generation-changed", result.Reason);
        Assert.False(result.PhysicalFileDeleted);
        Assert.True(File.Exists(ClosedPath(engine, segmentId)));
        Assert.True(engine.Index.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task Maintenance_reclaims_one_fully_dead_segment_per_cycle()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var first = await PublishAsync(engine, "<one@example>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var second = await PublishAsync(engine, "<two@example>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(first.ArtId));
        Assert.True(engine.TryEvict(second.ArtId));
        var low = SegmentOf(engine, first.ArtId);
        var high = SegmentOf(engine, second.ArtId);
        Assert.NotEqual(low, high);
        var coordinator = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(long.MaxValue, 100));

        var cycle = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, cycle.Outcome);
        Assert.True(cycle.Reclaimed);
        Assert.False(cycle.CompactionAttempted);
        var reclaimed = cycle.SegmentId;
        var remaining = reclaimed == low ? high : low;
        Assert.False(File.Exists(ClosedPath(engine, reclaimed)));
        Assert.True(File.Exists(ClosedPath(engine, remaining)));
        Assert.False(engine.Index.TryGet(reclaimed == low ? first.ArtId : second.ArtId, out _));
        Assert.True(engine.Index.TryGet(remaining == low ? first.ArtId : second.ArtId, out var kept));
        Assert.Equal(ArticleStorageState.Evicted, kept.State);

        var next = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, next.Outcome);
        Assert.Equal(remaining, next.SegmentId);
        Assert.False(File.Exists(ClosedPath(engine, remaining)));
        Assert.False(engine.Index.TryGet(first.ArtId, out _));
        Assert.False(engine.Index.TryGet(second.ArtId, out _));
    }

    [Fact]
    public async Task Multi_active_writer_protects_active_segments_and_reclaims_a_closed_dead_one()
    {
        using var dir = TempDir.Create(activeSegments: 2);
        await using var engine = Open(dir);
        var dead = await PublishAsync(engine, "<closed-dead@example>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(dead.ArtId));
        var deadSegment = SegmentOf(engine, dead.ArtId);
        var live = await PublishAsync(engine, "<still-active@example>");
        var liveSegment = SegmentOf(engine, live.ArtId);
        Assert.NotEqual(deadSegment, liveSegment);
        Assert.True(engine.Catalogue.TryGet(liveSegment, out var active));
        Assert.Equal(SegmentState.Active, active.State);
        engine.CompleteUnreferencedExtentAccounting();
        var deadInfo = RequireClosed(engine, deadSegment);

        var result = await engine.ReclaimFullyDeadClosedSegmentAsync(
            deadSegment,
            deadInfo.Generation,
            CancellationToken.None);

        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, result.Outcome);
        Assert.False(File.Exists(ClosedPath(engine, deadSegment)));
        Assert.False(engine.TryRead(dead.ArtId, out _));
        Assert.True(engine.Catalogue.TryGet(liveSegment, out active));
        Assert.Equal(SegmentState.Active, active.State);
        Assert.True(File.Exists(ActivePath(engine, liveSegment)));
        Assert.True(engine.TryRead(live.ArtId, out var served));
        Assert.True(served.ArtData.Span.SequenceEqual(live.ArtData.Span));

        var activeAttempt = await engine.ReclaimFullyDeadClosedSegmentAsync(
            liveSegment,
            active.Generation,
            CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.RejectedActive, activeAttempt.Outcome);
        Assert.True(engine.TryRead(live.ArtId, out _));
    }

    [Fact]
    public async Task Restart_after_successful_reclaim_does_not_restore_live_state()
    {
        using var dir = TempDir.Create();
        var options = dir.Options;
        SegmentId segmentId;
        ArticleId artId;
        await using (var engine = Open(dir))
        {
            var record = await PublishAsync(engine, "<gone@example>");
            artId = record.ArtId;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.TryEvict(artId));
            segmentId = SegmentOf(engine, artId);
            engine.CompleteUnreferencedExtentAccounting();
            var info = RequireClosed(engine, segmentId);
            var result = await engine.ReclaimFullyDeadClosedSegmentAsync(
                segmentId,
                info.Generation,
                CancellationToken.None);
            Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, result.Outcome);
        }

        await using var restarted = FileArticleStorageEngine.Open(options, logger: NullLogger.Instance);
        Assert.False(restarted.Catalogue.TryGet(segmentId, out _));
        Assert.False(restarted.Index.TryGet(artId, out _));
        Assert.False(restarted.TryRead(artId, out _));
        Assert.False(File.Exists(ClosedPath(restarted, segmentId)));
        Assert.False(restarted.Segments.TryReadProven(
            new StoredArticleLocation(segmentId, 0, 32),
            artId,
            0,
            32,
            out _));
    }

    [Fact]
    public async Task Cancelled_reclaim_does_not_delete()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var record = await PublishAsync(engine, "<cancel@example>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));
        var segmentId = SegmentOf(engine, record.ArtId);
        engine.CompleteUnreferencedExtentAccounting();
        var info = RequireClosed(engine, segmentId);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.ReclaimFullyDeadClosedSegmentAsync(segmentId, info.Generation, cancelled.Token));

        Assert.True(File.Exists(ClosedPath(engine, segmentId)));
        Assert.True(engine.Index.TryGet(record.ArtId, out var row));
        Assert.Equal(ArticleStorageState.Evicted, row.State);
    }

    private static FileArticleStorageEngine Open(TempDir dir) =>
        FileArticleStorageEngine.Open(dir.Options, logger: NullLogger.Instance);

    private static async Task<ArticleRecord> PublishAsync(FileArticleStorageEngine engine, string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase24\r\n\r\nbody\r\n");
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(created.Record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        return created.Record;
    }

    private static DateTimeOffset Accepted(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        return row.AcceptedUtc;
    }

    private static SegmentId SegmentOf(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        return row.Location.SegmentId;
    }

    private static SegmentInfo RequireClosed(FileArticleStorageEngine engine, SegmentId segmentId)
    {
        Assert.True(engine.Catalogue.TryGet(segmentId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
        return info;
    }

    private static string ClosedPath(FileArticleStorageEngine engine, SegmentId segmentId) =>
        Path.Combine(engine.Segments.RootPath, SegmentFileNames.Format(segmentId, SegmentFileKind.Closed));

    private static string ActivePath(FileArticleStorageEngine engine, SegmentId segmentId) =>
        Path.Combine(engine.Segments.RootPath, SegmentFileNames.Format(segmentId, SegmentFileKind.Active));

    private sealed class TempDir : IDisposable
    {
        private TempDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public ArticleStorageRuntimeOptions Options { get; }

        private string Root { get; }

        public static TempDir Create(int activeSegments = 1)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase24-" + Guid.NewGuid().ToString("N"));
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
                    MaxSegmentSealDelay: TimeSpan.FromHours(1),
                    ActiveSegmentCount: activeSegments));
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
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
