using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// A reclaimed SegmentId stays reserved across restart. The next file, including a compaction
/// destination, receives a higher id. Evicted and Invalid rows for that segment are dropped.
/// </summary>
public sealed class SegmentIdAllocationTests
{
    [Fact]
    public async Task Restart_after_reclaiming_the_highest_segment_allocates_a_higher_id()
    {
        using var dir = TempStorageDir.Create();
        var kept = CreateRecord("<sid-keep@seg.test>");
        var highest = CreateRecord("<sid-high@seg.test>");
        ulong reclaimed;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            _ = await AcceptAndCloseAsync(engine, kept);
            reclaimed = await AcceptAndCloseAsync(engine, highest);
            Assert.True(engine.TryEvict(highest.ArtId));
            await ReclaimClosedAsync(engine, reclaimed);
            Assert.False(engine.Segments.TryGetSegmentInfo(new SegmentId(reclaimed), out _));
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        var next = await AcceptAndCloseAsync(restarted, CreateRecord("<sid-next@seg.test>"));
        Assert.True(next > reclaimed);
    }

    [Fact]
    public async Task Historical_rows_do_not_block_accounting_of_the_segment_allocated_after_restart()
    {
        using var dir = TempStorageDir.Create();
        var evicted = CreateRecord("<sid-evicted@seg.test>");
        var invalid = CreateRecord("<sid-invalid@seg.test>");
        ulong reclaimed;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            reclaimed = await AcceptBothAndCloseAsync(engine, evicted, invalid);
            Assert.True(engine.TryEvict(evicted.ArtId));
            Assert.True(engine.TryInvalidate(invalid.ArtId));
            await ReclaimClosedAsync(engine, reclaimed);
            Assert.False(engine.Index.TryGet(evicted.ArtId, out _));
            Assert.False(engine.Index.TryGet(invalid.ArtId, out _));
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        Assert.False(restarted.Index.TryGet(evicted.ArtId, out _));
        Assert.False(restarted.Index.TryGet(invalid.ArtId, out _));

        var fresh = CreateRecord("<sid-fresh@seg.test>");
        var freshId = await AcceptAndCloseAsync(restarted, fresh);
        Assert.True(freshId > reclaimed);
        restarted.CompleteUnreferencedExtentAccounting();
        Assert.True(restarted.Segments.TryGetSegmentInfo(new SegmentId(freshId), out var accounted));
        Assert.True(accounted.ExtentAccountingComplete);

        Assert.True(restarted.TryEvict(fresh.ArtId));
        await ReclaimClosedAsync(restarted, freshId);
        Assert.False(restarted.Index.TryGet(evicted.ArtId, out _));
        Assert.False(restarted.Index.TryGet(invalid.ArtId, out _));
        Assert.False(restarted.Index.TryGet(fresh.ArtId, out _));
    }

    [Fact]
    public async Task Restart_with_no_segment_files_continues_above_the_reclaimed_id()
    {
        using var dir = TempStorageDir.Create();
        var only = CreateRecord("<sid-only@seg.test>");
        ulong reclaimed;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            reclaimed = await AcceptAndCloseAsync(engine, only);
            Assert.True(engine.TryEvict(only.ArtId));
            await ReclaimClosedAsync(engine, reclaimed);
            Assert.Empty(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*"));
            Assert.True(engine.CheckpointTruncateCommitted() > 0);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        var next = await AcceptAndCloseAsync(restarted, CreateRecord("<sid-after-empty@seg.test>"));
        Assert.True(next > reclaimed);
    }

    [Fact]
    public async Task Restart_after_a_durable_reservation_does_not_reuse_the_uncreated_id()
    {
        using var dir = TempStorageDir.Create();
        ulong reserved = 0;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            engine.Segments.TestHookAfterSegmentIdReserved = id =>
            {
                reserved = id;
                throw new IOException("crash-before-segment-create");
            };

            var crash = await Assert.ThrowsAsync<IOException>(() =>
                engine.Segments.GetActiveAppenderAsync(CancellationToken.None).AsTask());
            Assert.Equal("crash-before-segment-create", crash.Message);
            Assert.Equal(1UL, reserved);
            Assert.Equal(reserved + 1, engine.Journal.NextSegmentId);
            Assert.Empty(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*"));
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        var appender = await restarted.Segments.GetActiveAppenderAsync(CancellationToken.None);
        Assert.True(appender.SegmentId.Value > reserved);
        Assert.Empty(Directory.EnumerateFiles(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(new SegmentId(reserved), SegmentFileKind.Active)));
    }

    [Fact]
    public async Task Compaction_destination_after_restart_does_not_reuse_the_reclaimed_id()
    {
        using var dir = TempStorageDir.Create();
        var live = CreateRecord("<sid-live@seg.test>");
        var doomed = CreateRecord("<sid-doomed@seg.test>");
        ulong source;
        ulong reclaimed;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            source = await AcceptAndCloseAsync(engine, live);
            reclaimed = await AcceptAndCloseAsync(engine, doomed);
            Assert.True(reclaimed > source);
            Assert.True(engine.TryEvict(doomed.ArtId));
            await ReclaimClosedAsync(engine, reclaimed);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        var compact = await restarted.CompactClosedSegmentAsync(new SegmentId(source), CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.True(restarted.Index.TryGet(live.ArtId, out var moved));
        Assert.Equal(ArticleStorageState.Present, moved.State);
        Assert.True(moved.Location.SegmentId.Value > reclaimed);
        Assert.NotEqual(source, moved.Location.SegmentId.Value);
    }

    private static async Task<ulong> AcceptAndCloseAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        return meta.Location.SegmentId.Value;
    }

    private static async Task<ulong> AcceptBothAndCloseAsync(
        FileArticleStorageEngine engine,
        ArticleRecord first,
        ArticleRecord second)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(second, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(first.ArtId, out var meta));
        Assert.True(engine.Index.TryGet(second.ArtId, out var other));
        Assert.Equal(meta.Location.SegmentId, other.Location.SegmentId);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        return meta.Location.SegmentId.Value;
    }

    private static async Task ReclaimClosedAsync(FileArticleStorageEngine engine, ulong segmentId)
    {
        var id = new SegmentId(segmentId);
        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.Segments.TryGetSegmentInfo(id, out var accounted));
        Assert.True(accounted.ExtentAccountingComplete);
        var compact = await engine.CompactClosedSegmentAsync(id, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        var retired = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retired.Outcome);
        var reclaimed = await engine.ReclaimRetiredSegmentAsync(id, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimed.Outcome);
        Assert.False(engine.Segments.TryGetSegmentInfo(id, out _));
    }

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: segment-id\r\n");
        _ = builder.Append("\r\nline1\r\nline2\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-segment-id-" + Guid.NewGuid().ToString("N"));
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
