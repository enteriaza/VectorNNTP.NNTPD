using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Phase 4A: segment live/dead accounting, reclaimability predicate, and lifecycle fencing.
/// </summary>
public sealed class FileSegmentLifecycleTests
{
    [Fact]
    public async Task A_Append_IncreasesSizeAndLiveByRecordLength()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<life-a@seg.test>");
        await AcceptAndDrainAsync(engine, record);

        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        Assert.Equal(meta.Location.Length, info.SizeBytes);
        Assert.Equal(meta.Location.Length, info.LiveBytes);
        Assert.Equal(0, info.DeadBytes);
        Assert.Equal(info.SizeBytes, info.LiveBytes + info.DeadBytes);
        Assert.False(SegmentLifecycle.IsReclaimable(in info));
    }

    [Fact]
    public async Task B_MultipleAppends_AggregateAccounting()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var a = CreateRecord("<life-b1@seg.test>");
        var b = CreateRecord("<life-b2@seg.test>");
        await AcceptAndDrainAsync(engine, a);
        await AcceptAndDrainAsync(engine, b);

        Assert.True(engine.Index.TryGet(a.ArtId, out var ma));
        Assert.True(engine.Index.TryGet(b.ArtId, out var mb));
        Assert.Equal(ma.Location.SegmentId, mb.Location.SegmentId);
        Assert.True(engine.Segments.TryGetSegmentInfo(ma.Location.SegmentId, out var info));
        Assert.Equal(ma.Location.Length + mb.Location.Length, info.SizeBytes);
        Assert.Equal(info.SizeBytes, info.LiveBytes);
        Assert.Equal(0, info.DeadBytes);
    }

    [Fact]
    public async Task C_LogicalEvict_MovesLiveToDead_FileUnchanged()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<life-c@seg.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var path = Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*").Single();
        var fileBefore = new FileInfo(path).Length;
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var before));

        Assert.True(engine.TryEvict(record.ArtId));

        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var after));
        Assert.Equal(0, after.LiveBytes);
        Assert.Equal(meta.Location.Length, after.DeadBytes);
        Assert.Equal(before.SizeBytes, after.SizeBytes);
        Assert.Equal(fileBefore, new FileInfo(path).Length);
        Assert.Equal(after.SizeBytes, after.LiveBytes + after.DeadBytes);
    }

    [Fact]
    public async Task D_LogicalInvalidate_SameAccountingAsEvict()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<life-d@seg.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var path = Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*").Single();
        var fileBefore = new FileInfo(path).Length;

        Assert.True(engine.TryInvalidate(record.ArtId));

        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var after));
        Assert.Equal(0, after.LiveBytes);
        Assert.Equal(meta.Location.Length, after.DeadBytes);
        Assert.Equal(fileBefore, new FileInfo(path).Length);
    }

    [Fact]
    public async Task E_DuplicateDeath_DoesNotDoubleCount()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<life-e@seg.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var once));

        Assert.True(engine.TryEvict(record.ArtId)); // idempotent same-state
        Assert.False(engine.TryInvalidate(record.ArtId)); // Present→Invalid blocked when Evicted

        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var twice));
        Assert.Equal(once.LiveBytes, twice.LiveBytes);
        Assert.Equal(once.DeadBytes, twice.DeadBytes);
        Assert.Equal(meta.Location.Length, twice.DeadBytes);
    }

    [Fact]
    public async Task F_FailedDeath_LeavesAccountingUnchanged()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<life-f@seg.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var before));

        engine.TestFailNextLogicalDeath = true;
        Assert.False(engine.TryEvict(record.ArtId));

        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var after));
        Assert.Equal(before.LiveBytes, after.LiveBytes);
        Assert.Equal(before.DeadBytes, after.DeadBytes);
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.True(engine.Index.TryGet(record.ArtId, out var still));
        Assert.Equal(ArticleStorageState.Present, still.State);
    }

    [Fact]
    public async Task G_MixedSegment_NotReclaimable()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var keep = CreateRecord("<life-g-live@seg.test>");
        var drop = CreateRecord("<life-g-dead@seg.test>");
        await AcceptAndDrainAsync(engine, keep);
        await AcceptAndDrainAsync(engine, drop);
        Assert.True(engine.Index.TryGet(drop.ArtId, out var dropMeta));
        Assert.True(engine.TryEvict(drop.ArtId));

        Assert.True(engine.Index.TryGet(keep.ArtId, out var keepMeta));
        Assert.True(engine.Segments.TryGetSegmentInfo(keepMeta.Location.SegmentId, out var info));
        Assert.Equal(keepMeta.Location.Length, info.LiveBytes);
        Assert.Equal(dropMeta.Location.Length, info.DeadBytes);
        Assert.Equal(info.SizeBytes, info.LiveBytes + info.DeadBytes);
        Assert.False(SegmentLifecycle.IsReclaimable(in info));
    }

    [Fact]
    public async Task H_FullyDeadClosed_IsReclaimable_FileRemains()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var a = CreateRecord("<life-h1@seg.test>");
        var b = CreateRecord("<life-h2@seg.test>");
        await AcceptAndDrainAsync(engine, a);
        await AcceptAndDrainAsync(engine, b);
        Assert.True(engine.Index.TryGet(a.ArtId, out var ma));
        var segmentId = ma.Location.SegmentId;

        Assert.True(engine.TryEvict(a.ArtId));
        Assert.True(engine.TryInvalidate(b.ArtId));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        Assert.True(engine.Segments.TryGetSegmentInfo(segmentId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
        Assert.Equal(0, info.LiveBytes);
        Assert.Equal(info.SizeBytes, info.DeadBytes);
        Assert.True(SegmentLifecycle.IsReclaimable(in info));
        Assert.True(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.closed").Any());
    }

    [Fact]
    public async Task I_Active_NeverReclaimable_EvenWhenFullyDead()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<life-i@seg.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.TryEvict(record.ArtId));

        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        Assert.Equal(SegmentState.Active, info.State);
        Assert.Equal(0, info.LiveBytes);
        Assert.False(SegmentLifecycle.IsReclaimable(in info));
    }

    [Fact]
    public async Task J_ClosedWithLive_NotReclaimable()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<life-j@seg.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
        Assert.True(info.LiveBytes > 0);
        Assert.False(SegmentLifecycle.IsReclaimable(in info));
    }

    [Fact]
    public async Task K_Retired_NotWritable()
    {
        using var dir = TempStorageDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var id = appender.SegmentId;
        _ = await appender.AppendAsync(CreateArtDataBytes("<life-k@seg.test>"), CancellationToken.None);
        await store.CloseActiveAsync(CancellationToken.None);
        Assert.True(store.TryGetSegmentInfo(id, out var closed));
        Assert.True(store.Catalogue.TryRetire(id, closed.Generation, DateTimeOffset.UtcNow));
        Assert.True(store.TryGetSegmentInfo(id, out var retired));
        Assert.Equal(SegmentState.Retired, retired.State);
        Assert.False(SegmentLifecycle.IsReclaimable(in retired));
        Assert.Throws<InvalidOperationException>(() =>
            store.Catalogue.Upsert(retired with { State = SegmentState.Active }));
    }

    [Fact]
    public async Task L_Restart_RebuildsLiveDeadFromIndex()
    {
        using var dir = TempStorageDir.Create();
        StoredArticleLocation location;
        long sizeBytes;
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            var keep = CreateRecord("<life-l-keep@seg.test>");
            var drop = CreateRecord("<life-l-drop@seg.test>");
            await AcceptAndDrainAsync(engineA, keep);
            await AcceptAndDrainAsync(engineA, drop);
            Assert.True(engineA.Index.TryGet(drop.ArtId, out var dropMeta));
            location = dropMeta.Location;
            Assert.True(engineA.TryEvict(drop.ArtId));
            Assert.True(engineA.Segments.TryGetSegmentInfo(location.SegmentId, out var before));
            sizeBytes = before.SizeBytes;
            await engineA.Segments.CloseActiveAsync(CancellationToken.None);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        Assert.True(engineB.Segments.TryGetSegmentInfo(location.SegmentId, out var after));
        Assert.Equal(sizeBytes, after.SizeBytes);
        Assert.Equal(location.Length, after.DeadBytes);
        Assert.Equal(sizeBytes - location.Length, after.LiveBytes);
        Assert.Equal(after.SizeBytes, after.LiveBytes + after.DeadBytes);
        Assert.False(SegmentLifecycle.IsReclaimable(in after));
    }

    [Fact]
    public async Task M_CacheCoherence_AfterLogicalDeath()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<life-m@seg.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(cache.TryGet(record.ArtId, out _));
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.False(cache.TryGet(record.ArtId, out _));
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task N_Rotation_PreservesAccountingOnClosed()
    {
        using var dir = TempStorageDir.Create(targetSegmentBytes: 512);
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var first = CreateRecord("<life-n1@seg.test>", body: new string('x', 200) + "\r\n");
        var second = CreateRecord("<life-n2@seg.test>", body: new string('y', 200) + "\r\n");
        await AcceptAndDrainAsync(engine, first);
        Assert.True(engine.Index.TryGet(first.ArtId, out var m1));
        var firstSeg = m1.Location.SegmentId;
        await AcceptAndDrainAsync(engine, second);
        Assert.True(engine.Index.TryGet(second.ArtId, out var m2));

        Assert.True(engine.Segments.TryGetSegmentInfo(firstSeg, out var closed));
        if (m2.Location.SegmentId.Value != firstSeg.Value)
        {
            Assert.Equal(SegmentState.Closed, closed.State);
            Assert.Equal(m1.Location.Length, closed.LiveBytes);
            Assert.Equal(closed.SizeBytes, closed.LiveBytes + closed.DeadBytes);
            Assert.True(engine.Segments.TryGetSegmentInfo(m2.Location.SegmentId, out var active));
            Assert.Equal(SegmentState.Active, active.State);
        }
        else
        {
            // Both fit — force close and verify accounting retained.
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.Segments.TryGetSegmentInfo(firstSeg, out closed));
            Assert.Equal(SegmentState.Closed, closed.State);
            Assert.Equal(m1.Location.Length + m2.Location.Length, closed.LiveBytes);
        }
    }

    [Fact]
    public async Task O_Retired_CannotReopenForAppend()
    {
        using var dir = TempStorageDir.Create();
        SegmentId retiredId;
        using (var storeA = FileSegmentStore.Open(dir.Options))
        {
            var appender = await storeA.GetActiveAppenderAsync(CancellationToken.None);
            retiredId = appender.SegmentId;
            _ = await appender.AppendAsync(CreateArtDataBytes("<life-o@seg.test>"), CancellationToken.None);
            await storeA.CloseActiveAsync(CancellationToken.None);
            Assert.True(storeA.TryGetSegmentInfo(retiredId, out var closed));
            Assert.True(storeA.Catalogue.TryRetire(retiredId, closed.Generation, DateTimeOffset.UtcNow));
        }

        using var storeB = FileSegmentStore.Open(dir.Options);
        Assert.True(storeB.TryGetSegmentInfo(retiredId, out var retired));
        Assert.Equal(SegmentState.Retired, retired.State);
        var active = await storeB.GetActiveAppenderAsync(CancellationToken.None);
        Assert.NotEqual(retiredId, active.SegmentId);
        Assert.Throws<InvalidOperationException>(() =>
            storeB.Catalogue.Upsert(retired with { State = SegmentState.Active }));
    }

    private static async Task AcceptAndDrainAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
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
        _ = builder.Append("Subject: lifecycle\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private static byte[] CreateArtDataBytes(string messageId) =>
        CreateRecord(messageId).ArtData.ToArray();

    private sealed class TempStorageDir : IDisposable
    {
        private TempStorageDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string Root { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create(long? targetSegmentBytes = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-seg-life-" + Guid.NewGuid().ToString("N"));
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
                    SegmentTargetSizeBytes: targetSegmentBytes
                        ?? ArticleStorageOptions.DefaultSegmentTargetSizeBytes));
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
