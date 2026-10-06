using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Lifecycle invariants: ingress ACK stays readable, bulk publication does not open a gap,
/// and a logical tombstone is not served from leftover cache bytes.
/// </summary>
public sealed class ArticleLifecycleInvariantTests
{
    [Fact]
    public async Task Ack_is_immediately_readable_from_ingress()
    {
        using var dir = TempDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var record = Article("<phase20-ack@seg.test>");
        Assert.False(engine.TryRead(record.ArtId, out _));

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        AssertIngress(engine, record, physicalWritten: false);
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.Equal(0, engine.SegmentArticleReadCount);
        Assert.True(engine.Journal.DurableFlushCount >= 1);
        var omitted = engine.CheckpointTruncateCommitted();
        Assert.Equal(0, omitted);
        AssertIngress(engine, record, physicalWritten: false);
    }

    [Fact]
    public async Task Restart_before_bulk_publication_keeps_ingress_readable()
    {
        using var dir = TempDir.Create();
        var record = Article("<phase20-restart-ingress@seg.test>");
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            AssertIngress(engine, record, physicalWritten: false);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        restarted.SuspendBackgroundPersist = true;
        AssertIngress(restarted, record, physicalWritten: false);

        await restarted.RecoverAsync(CancellationToken.None);
        AssertBulk(restarted, record);
    }

    [Fact]
    public async Task Bulk_publication_keeps_the_article_readable_through_journal_retirement()
    {
        using var dir = TempDir.Create();
        var record = Article("<phase20-bulk@seg.test>");
        StoredArticleLocation location;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            AssertIngress(engine, record, physicalWritten: false);

            engine.SuspendBackgroundPersist = false;
            await engine.DrainPendingAsync(CancellationToken.None);
            AssertBulk(engine, record);
            Assert.True(engine.Index.TryGet(record.ArtId, out var published));
            location = published.Location;
            Assert.True(location.SegmentId.Value != 0);

            _ = engine.CheckpointTruncateCommitted();
            AssertBulk(engine, record);
            Assert.True(engine.Index.TryGet(record.ArtId, out var afterCheckpoint));
            Assert.Equal(location, afterCheckpoint.Location);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        AssertBulk(restarted, record);
        Assert.True(restarted.Index.TryGet(record.ArtId, out var restored));
        Assert.Equal(location, restored.Location);
    }

    [Fact]
    public Task Crash_before_sata_stays_ingress_authoritative() =>
        AssertCrashBoundary(FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend, physicalWritten: false, present: false);

    [Fact]
    public Task Crash_after_sata_before_physical_written_stays_ingress_authoritative() =>
        AssertCrashBoundary(FileArticleStorageEngine.PersistFaultPoint.AfterSataAppend, physicalWritten: false, present: false);

    [Fact]
    public Task Crash_after_physical_written_before_present_stays_readable() =>
        AssertCrashBoundary(FileArticleStorageEngine.PersistFaultPoint.AfterPhysicalWritten, physicalWritten: true, present: false);

    [Fact]
    public Task Crash_after_present_before_index_committed_keeps_ingress_until_recovery() =>
        AssertCrashBoundary(FileArticleStorageEngine.PersistFaultPoint.BeforeIndexCommitted, physicalWritten: true, present: true);

    private static async Task AssertCrashBoundary(
        FileArticleStorageEngine.PersistFaultPoint fault,
        bool physicalWritten,
        bool present)
    {
        using var dir = TempDir.Create();
        var record = Article("<phase20-" + fault + "@seg.test>");
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            engine.TestFaultPoint = fault;
            await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
            AssertBoundary(engine, record, physicalWritten, present);
        }

        await using (var restarted = FileArticleStorageEngine.Open(dir.Options))
        {
            restarted.SuspendBackgroundPersist = true;
            AssertBoundary(restarted, record, physicalWritten, present);
            Assert.Equal(0, restarted.CheckpointTruncateCommitted());
            AssertBoundary(restarted, record, physicalWritten, present);

            await restarted.RecoverAsync(CancellationToken.None);
            AssertBulk(restarted, record);
            _ = restarted.CheckpointTruncateCommitted();
            AssertBulk(restarted, record);
        }

        await using var recovered = FileArticleStorageEngine.Open(dir.Options);
        await recovered.RecoverAsync(CancellationToken.None);
        AssertBulk(recovered, record);
    }

    [Fact]
    public async Task Cache_does_not_define_authority_and_a_tombstone_is_not_served()
    {
        using var dir = TempDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        var record = Article("<phase20-cache@seg.test>");
        await using (var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            Assert.False(cache.TryGet(record.ArtId, out _));
            AssertIngress(engine, record, physicalWritten: false);

            await engine.DrainPendingAsync(CancellationToken.None);
            AssertBulk(engine, record);
            Assert.True(engine.Index.TryGet(record.ArtId, out var beforeHit));
            var hitsBefore = engine.CacheArticleReadCount;
            Assert.True(engine.TryRead(record.ArtId, out _));
            Assert.Equal(hitsBefore + 1, engine.CacheArticleReadCount);
            Assert.True(engine.Index.TryGet(record.ArtId, out var afterHit));
            Assert.Equal(beforeHit.State, afterHit.State);
            Assert.Equal(beforeHit.Location, afterHit.Location);
            Assert.Equal(ArticleStorageState.Present, afterHit.State);

            Assert.True(cache.Remove(record.ArtId));
            Assert.True(engine.TryRead(record.ArtId, out var cold));
            Assert.True(cold.ArtData.Span.SequenceEqual(record.ArtData.Span));
            Assert.Equal(ArticleStorageState.Present, engine.Index.TryGet(record.ArtId, out var afterEvictCache) ? afterEvictCache.State : default);

            Assert.True(engine.TryEvict(record.ArtId));
            Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(in record));
            Assert.True(cache.TryGet(record.ArtId, out _));
            Assert.False(engine.TryRead(record.ArtId, out _));
            Assert.False(cache.TryGet(record.ArtId, out _));
            Assert.True(engine.Index.TryGet(record.ArtId, out var tombstone));
            Assert.Equal(ArticleStorageState.Evicted, tombstone.State);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.False(restarted.TryRead(record.ArtId, out _));
        Assert.True(restarted.Index.TryGet(record.ArtId, out var restored));
        Assert.Equal(ArticleStorageState.Evicted, restored.State);
    }

    private static void AssertIngress(
        FileArticleStorageEngine engine,
        ArticleRecord record,
        bool physicalWritten) =>
        AssertBoundary(engine, record, physicalWritten, present: false);

    private static void AssertBoundary(
        FileArticleStorageEngine engine,
        ArticleRecord record,
        bool physicalWritten,
        bool present)
    {
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(record.ArtId, read.Metadata.ArtId);
        Assert.Equal(record.ArtHash, read.Metadata.ArtHash);
        Assert.Equal(record.ArtSize, read.Metadata.ArtSize);

        var incomplete = Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Equal(record.ArtId, incomplete.Accept.ArtId);
        Assert.Equal(physicalWritten, incomplete.PhysicalWritten is not null);
        Assert.True(engine.Journal.OutstandingRecoverableBytes >= record.ArtSize);
        Assert.Equal(present, engine.Index.TryGet(record.ArtId, out var row) && row.State == ArticleStorageState.Present);
        if (present)
        {
            Assert.Equal(record.ArtHash, row.ArtHash);
            Assert.Equal(record.ArtSize, row.ArtSize);
        }
    }

    private static void AssertBulk(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(record.ArtId, read.Metadata.ArtId);
        Assert.Equal(record.ArtHash, read.Metadata.ArtHash);
        Assert.Equal(record.ArtSize, read.Metadata.ArtSize);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.False(engine.Journal.TryGetOutstanding(record.ArtId, out _));
        Assert.True(engine.Index.TryGet(record.ArtId, out var row));
        Assert.Equal(ArticleStorageState.Present, row.State);
        Assert.Equal(record.ArtHash, row.ArtHash);
        Assert.Equal(record.ArtSize, row.ArtSize);
        Assert.Equal(row.Location, read.Metadata.Location);
    }

    private static ArticleRecord Article(string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase20\r\n\r\nbody\r\n");
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase20-" + Guid.NewGuid().ToString("N"));
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
                    MaxSegmentSealDelay: TimeSpan.FromHours(1)));
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
