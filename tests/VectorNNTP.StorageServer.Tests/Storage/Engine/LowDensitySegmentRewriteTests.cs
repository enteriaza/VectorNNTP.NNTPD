using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Low-density rewrite of a closed segment that still has Present bytes.
/// Eligibility is the existing <see cref="ArticleSegmentPolicy"/> dead-byte floors.
/// A fully dead closed segment is deleted without relocation.
/// </summary>
public sealed class LowDensitySegmentRewriteTests
{
    [Fact]
    public async Task Low_density_closed_segment_is_rewritten_and_the_source_is_reclaimed()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var keep = await PublishAsync(engine, "<keep@example>");
        var drop = await PublishAsync(engine, "<drop@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        var before = Meta(engine, keep.ArtId);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        var sourceId = before.Location.SegmentId;

        var result = await Coordinator(engine, minimumDeadRatio: 10).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.True(result.CompactionAttempted);
        Assert.True(result.CompactionCommitted);
        Assert.True(result.Reclaimed);
        Assert.Equal(sourceId, result.SegmentId);
        Assert.True(result.ReclaimedSegmentSizeBytes > 0);
        Assert.NotNull(result.DestinationSegmentId);
        Assert.False(engine.Catalogue.TryGet(sourceId, out _));
        Assert.False(File.Exists(ClosedPath(engine, sourceId)));
        var after = Meta(engine, keep.ArtId);
        Assert.Equal(keep.ArtId, after.ArtId);
        Assert.Equal(before.ArtHash, after.ArtHash);
        Assert.Equal(before.ArtSize, after.ArtSize);
        Assert.Equal(before.AcceptedUtc, after.AcceptedUtc);
        Assert.Equal(before.LastAccessUtc, after.LastAccessUtc);
        Assert.Equal(ArticleStorageState.Present, after.State);
        Assert.NotEqual(before.Location, after.Location);
        Assert.Equal(result.DestinationSegmentId, after.Location.SegmentId.Value);
        Assert.True(engine.TryRead(keep.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(keep.ArtData.Span));
        Assert.False(engine.Index.TryGet(drop.ArtId, out _));
        Assert.False(engine.TryRead(drop.ArtId, out _));
        Assert.Equal(after.Location.SegmentId, OnlyLiveSegment(engine));
        Assert.True(LiveBytes(engine, after.Location.SegmentId) >= after.Location.Length);
    }

    [Fact]
    public async Task Segment_above_the_density_threshold_is_left_alone()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var records = new[]
        {
            await PublishAsync(engine, "<dense-a@example>"),
            await PublishAsync(engine, "<dense-b@example>"),
            await PublishAsync(engine, "<dense-c@example>"),
            await PublishAsync(engine, "<dense-d@example>"),
        };
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(records[0].ArtId));
        var before = records.Skip(1).Select(record => Meta(engine, record.ArtId)).ToArray();

        var result = await Coordinator(engine, minimumDeadRatio: 90).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.NoWork, result.Outcome);
        Assert.False(result.CompactionAttempted);
        Assert.False(result.Reclaimed);
        Assert.True(result.RewriteDensitySkipCount >= 1);
        Assert.Empty(engine.Journal.EnumerateCompactions());
        foreach (var prior in before)
        {
            var after = Meta(engine, prior.ArtId);
            Assert.Equal(prior.Location, after.Location);
            Assert.Equal(ArticleStorageState.Present, after.State);
            Assert.Equal(prior.AcceptedUtc, after.AcceptedUtc);
        }

        Assert.True(File.Exists(ClosedPath(engine, before[0].Location.SegmentId)));
    }

    [Fact]
    public async Task Fully_dead_segment_is_deleted_without_rewrite()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var record = await PublishAsync(engine, "<empty@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        var sourceId = Meta(engine, record.ArtId).Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));

        var result = await Coordinator(engine, minimumDeadBytes: 0, minimumDeadRatio: 0).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, result.Outcome);
        Assert.False(result.CompactionAttempted);
        Assert.False(result.CompactionCommitted);
        Assert.Null(result.DestinationSegmentId);
        Assert.Empty(engine.Journal.EnumerateCompactions());
        Assert.False(engine.Catalogue.TryGet(sourceId, out _));
        Assert.False(File.Exists(ClosedPath(engine, sourceId)));
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Active_segment_is_not_rewritten()
    {
        using var dir = TempDir.Create(activeSegments: 2);
        await using var engine = Open(dir);
        var articles = new[]
        {
            await PublishAsync(engine, "<active-a@example>"),
            await PublishAsync(engine, "<active-b@example>"),
            await PublishAsync(engine, "<active-c@example>"),
            await PublishAsync(engine, "<active-d@example>"),
        };
        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var closed = engine.Catalogue.Snapshot().Single(info => info.State == SegmentState.Closed);
        var active = engine.Catalogue.Snapshot().Single(info => info.State == SegmentState.Active);
        var onClosed = articles.Where(record => Meta(engine, record.ArtId).Location.SegmentId == closed.SegmentId).ToArray();
        var onActive = articles.Where(record => Meta(engine, record.ArtId).Location.SegmentId == active.SegmentId).ToArray();
        Assert.Equal(2, onClosed.Length);
        Assert.Equal(2, onActive.Length);
        var activeBefore = onActive.Select(record => Meta(engine, record.ArtId)).ToArray();
        Assert.True(engine.TryEvict(onClosed[0].ArtId));
        var survivor = onClosed[1];

        var result = await Coordinator(engine, minimumDeadRatio: 10).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(closed.SegmentId, result.SegmentId);
        Assert.False(engine.Catalogue.TryGet(closed.SegmentId, out _));
        Assert.True(engine.Catalogue.TryGet(active.SegmentId, out var stillActive));
        Assert.Equal(SegmentState.Active, stillActive.State);
        foreach (var prior in activeBefore)
        {
            Assert.Equal(prior.Location, Meta(engine, prior.ArtId).Location);
            Assert.True(engine.TryRead(prior.ArtId, out _));
        }

        var moved = Meta(engine, survivor.ArtId);
        Assert.Equal(ArticleStorageState.Present, moved.State);
        Assert.NotEqual(closed.SegmentId, moved.Location.SegmentId);
        Assert.True(engine.TryRead(survivor.ArtId, out _));
        Assert.False(engine.Index.TryGet(onClosed[0].ArtId, out _));
    }

    [Fact]
    public async Task Read_during_relocation_observes_the_authoritative_article()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var keep = await PublishAsync(engine, "<read@example>");
        var drop = await PublishAsync(engine, "<read-drop@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        engine.TestHookBeforeRelocateArticle = artId =>
        {
            Assert.True(engine.TryRead(artId, out var during));
            Assert.True(during.ArtData.Span.SequenceEqual(keep.ArtData.Span));
        };

        var result = await Coordinator(engine, minimumDeadRatio: 10).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.True(engine.TryRead(keep.ArtId, out var after));
        Assert.True(after.ArtData.Span.SequenceEqual(keep.ArtData.Span));
    }

    [Fact]
    public async Task Expiration_before_relocation_commit_is_not_resurrected()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var keep = await PublishAsync(engine, "<expire-keep@example>");
        var drop = await PublishAsync(engine, "<expire-drop@example>");
        var already = await PublishAsync(engine, "<expire-dead@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(already.ArtId));
        engine.TestHookBeforeRelocateArticle = artId =>
        {
            if (artId == drop.ArtId)
            {
                Assert.True(engine.TryEvict(drop.ArtId));
            }
        };

        var result = await Coordinator(engine, minimumDeadRatio: 10).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(ArticleStorageState.Present, Meta(engine, keep.ArtId).State);
        Assert.True(engine.TryRead(keep.ArtId, out _));
        Assert.False(engine.Index.TryGet(drop.ArtId, out var resurrected) && resurrected.State == ArticleStorageState.Present);
        Assert.False(engine.TryRead(drop.ArtId, out _));
    }

    [Fact]
    public async Task Stale_index_update_does_not_republish_a_departed_article()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var record = await PublishAsync(engine, "<stale-move@example>");
        var drop = await PublishAsync(engine, "<stale-dead@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        engine.TestHookBeforeIndexRelocate = () => Assert.True(engine.TryInvalidate(record.ArtId));

        var result = await Coordinator(engine, minimumDeadRatio: 10).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.False(engine.Index.TryGet(record.ArtId, out var row) && row.State == ArticleStorageState.Present);
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public Task Destination_append_fault_leaves_the_source_authoritative() =>
        RelocationFaultLeavesSourceAuthoritative(
            FileArticleStorageEngine.RelocationFaultPoint.AfterIntentBeforeAppend);

    [Fact]
    public Task Destination_flush_fault_leaves_the_source_authoritative() =>
        RelocationFaultLeavesSourceAuthoritative(
            FileArticleStorageEngine.RelocationFaultPoint.AfterAppendBeforeWritten);

    [Fact]
    public Task Index_update_fault_leaves_the_source_authoritative() =>
        RelocationFaultLeavesSourceAuthoritative(
            FileArticleStorageEngine.RelocationFaultPoint.AfterWrittenBeforeIndex);

    private static async Task RelocationFaultLeavesSourceAuthoritative(
        FileArticleStorageEngine.RelocationFaultPoint fault)
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var record = await PublishAsync(engine, "<fault@example>");
        var drop = await PublishAsync(engine, "<fault-drop@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        var source = Meta(engine, record.ArtId);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        engine.TestRelocationFaultPoint = fault;

        await Assert.ThrowsAsync<IOException>(() =>
            Coordinator(engine, minimumDeadRatio: 10).RunOnceAsync(CancellationToken.None));

        var during = Meta(engine, record.ArtId);
        Assert.Equal(ArticleStorageState.Present, during.State);
        Assert.Equal(source.Location, during.Location);
        Assert.Equal(source.AcceptedUtc, during.AcceptedUtc);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.Catalogue.TryGet(source.Location.SegmentId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
        Assert.True(File.Exists(ClosedPath(engine, source.Location.SegmentId)));

        var recovered = await Coordinator(engine, minimumDeadRatio: 10).RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, recovered.Outcome);
        Assert.NotEqual(source.Location, Meta(engine, record.ArtId).Location);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.False(engine.Catalogue.TryGet(source.Location.SegmentId, out _));
    }

    [Fact]
    public async Task Crash_after_partial_relocation_keeps_one_authoritative_location_per_article()
    {
        using var dir = TempDir.Create();
        var options = dir.Options;
        var firstId = default(ArticleId);
        var secondId = default(ArticleId);
        SegmentId sourceId = default;
        await using (var engine = Open(dir))
        {
            var first = await PublishAsync(engine, "<part-a@example>");
            var second = await PublishAsync(engine, "<part-b@example>");
            var drop = await PublishAsync(engine, "<part-dead@example>");
            await engine.DrainPendingAsync(CancellationToken.None);
            firstId = first.ArtId;
            secondId = second.ArtId;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            sourceId = Meta(engine, first.ArtId).Location.SegmentId;
            Assert.True(engine.TryEvict(drop.ArtId));
            var seen = 0;
            engine.TestHookBeforeRelocateArticle = _ =>
            {
                seen++;
                if (seen == 2)
                {
                    engine.TestRelocationFaultPoint =
                        FileArticleStorageEngine.RelocationFaultPoint.AfterIntentBeforeAppend;
                }
            };

            await Assert.ThrowsAsync<IOException>(() =>
                Coordinator(engine, minimumDeadRatio: 10).RunOnceAsync(CancellationToken.None));

            AssertReadablePresent(engine, firstId);
            AssertReadablePresent(engine, secondId);
            Assert.Equal(1, PresentOn(engine, sourceId));
            Assert.True(File.Exists(ClosedPath(engine, sourceId)));
        }

        await using var restarted = FileArticleStorageEngine.Open(options, logger: NullLogger.Instance);
        AssertReadablePresent(restarted, firstId);
        AssertReadablePresent(restarted, secondId);
        Assert.Equal(1, PresentOn(restarted, sourceId));
        Assert.NotEqual(
            Meta(restarted, firstId).Location.SegmentId,
            Meta(restarted, secondId).Location.SegmentId);

        var recovered = await Coordinator(restarted, minimumDeadRatio: 10).RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, recovered.Outcome);
        AssertReadablePresent(restarted, firstId);
        AssertReadablePresent(restarted, secondId);
        Assert.Equal(0, PresentOn(restarted, sourceId));
        Assert.False(restarted.Catalogue.TryGet(sourceId, out _));
        Assert.False(File.Exists(ClosedPath(restarted, sourceId)));
    }

    [Fact]
    public async Task Restart_after_successful_rewrite_serves_the_destination()
    {
        using var dir = TempDir.Create();
        var options = dir.Options;
        ArticleId artId;
        SegmentId sourceId;
        StoredArticleLocation destination;
        await using (var engine = Open(dir))
        {
            var record = await PublishAsync(engine, "<restart@example>");
            var drop = await PublishAsync(engine, "<restart-drop@example>");
            await engine.DrainPendingAsync(CancellationToken.None);
            artId = record.ArtId;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            sourceId = Meta(engine, artId).Location.SegmentId;
            Assert.True(engine.TryEvict(drop.ArtId));
            var result = await Coordinator(engine, minimumDeadRatio: 10).RunOnceAsync(CancellationToken.None);
            Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
            destination = Meta(engine, artId).Location;
        }

        await using var restarted = FileArticleStorageEngine.Open(options, logger: NullLogger.Instance);
        var restored = Meta(restarted, artId);
        Assert.Equal(destination, restored.Location);
        Assert.Equal(ArticleStorageState.Present, restored.State);
        Assert.True(restarted.TryRead(artId, out _));
        Assert.False(restarted.Catalogue.TryGet(sourceId, out _));
        Assert.Equal(0, PresentOn(restarted, sourceId));
    }

    [Fact]
    public async Task Cancelled_rewrite_does_not_retire_the_source()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir);
        var record = await PublishAsync(engine, "<cancel@example>");
        var drop = await PublishAsync(engine, "<cancel-drop@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        var before = Meta(engine, record.ArtId);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Coordinator(engine, minimumDeadRatio: 10).RunOnceAsync(cancelled.Token));

        Assert.Equal(before.Location, Meta(engine, record.ArtId).Location);
        Assert.True(File.Exists(ClosedPath(engine, before.Location.SegmentId)));
        Assert.True(engine.Catalogue.TryGet(before.Location.SegmentId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
    }

    [Fact]
    public async Task Cache_still_serves_the_article_after_relocation()
    {
        using var dir = TempDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, logger: NullLogger.Instance, articleCache: cache);
        var keep = await PublishAsync(engine, "<cache@example>");
        var drop = await PublishAsync(engine, "<cache-drop@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryRead(keep.ArtId, out var cached));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));

        var result = await Coordinator(engine, minimumDeadRatio: 10).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.True(engine.TryRead(keep.ArtId, out var after));
        Assert.True(after.ArtData.Span.SequenceEqual(cached.ArtData.Span));
        Assert.True(after.ArtData.Span.SequenceEqual(keep.ArtData.Span));
    }

    private static void AssertReadablePresent(FileArticleStorageEngine engine, ArticleId artId)
    {
        var meta = Meta(engine, artId);
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.True(engine.TryRead(artId, out var read));
        Assert.Equal(meta.ArtSize, read.ArtData.Length);
    }

    private static int PresentOn(FileArticleStorageEngine engine, SegmentId segmentId) =>
        engine.Index.Snapshot().Count(row =>
            row.State == ArticleStorageState.Present && row.Location.SegmentId == segmentId);

    private static SegmentId OnlyLiveSegment(FileArticleStorageEngine engine)
    {
        var live = engine.Catalogue.Snapshot().Where(info => info.LiveBytes > 0).ToArray();
        Assert.Single(live);
        return live[0].SegmentId;
    }

    private static long LiveBytes(FileArticleStorageEngine engine, SegmentId segmentId)
    {
        Assert.True(engine.Catalogue.TryGet(segmentId, out var info));
        return info.LiveBytes;
    }

    private static StoredArticleMetadata Meta(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var meta));
        return meta;
    }

    private static StorageMaintenanceCoordinator Coordinator(
        FileArticleStorageEngine engine,
        long minimumDeadBytes = 0,
        int minimumDeadRatio = 10) =>
        new(engine, new ArticleSegmentPolicy(minimumDeadBytes, minimumDeadRatio));

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
        _ = builder.Append("Subject: phase25\r\n\r\nbody\r\n");
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(created.Record, CancellationToken.None)).Outcome);
        return created.Record;
    }

    private static string ClosedPath(FileArticleStorageEngine engine, SegmentId segmentId) =>
        Path.Combine(engine.Segments.RootPath, SegmentFileNames.Format(segmentId, SegmentFileKind.Closed));

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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase25-" + Guid.NewGuid().ToString("N"));
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
