using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Evicted and Invalid index rows stay while their segment file can still be recovered.
/// They leave memory only after that segment has been physically reclaimed.
/// </summary>
public sealed class HistoricalIndexRowTests
{
    [Fact]
    public async Task Present_to_evicted_keeps_the_row_until_the_segment_is_reclaimed()
    {
        using var dir = TempDir.Create();
        var record = Record("<hist-evict@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await AcceptAsync(engine, record);
        Assert.True(engine.TryRead(record.ArtId, out _));

        Assert.True(engine.TryEvict(record.ArtId));
        Assert.True(engine.Index.TryGet(record.ArtId, out var evicted));
        Assert.Equal(ArticleStorageState.Evicted, evicted.State);
        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.Equal(1, engine.Index.Snapshot().Count(row => row.ArtId == record.ArtId));
    }

    [Fact]
    public async Task Reclaim_removes_evicted_rows_and_leaves_a_live_article()
    {
        using var dir = TempDir.Create();
        var dead = Record("<hist-dead@seg.test>");
        var live = Record("<hist-live@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await AcceptAsync(engine, dead);
        Assert.True(engine.Index.TryGet(dead.ArtId, out var deadMeta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        await AcceptAsync(engine, live);
        engine.Index.TouchHint(dead.ArtId, DateTimeOffset.UtcNow);
        Assert.True(engine.Index.UseCount(dead.ArtId) >= 1);
        Assert.True(engine.TryEvict(dead.ArtId));
        engine.Index.TouchHint(live.ArtId, DateTimeOffset.UtcNow);
        Assert.True(engine.Index.UseCount(live.ArtId) >= 1);
        var useBefore = engine.Index.UseCountEntryCount;

        await ReclaimClosedAsync(engine, deadMeta.Location.SegmentId);

        Assert.False(engine.Index.TryGet(dead.ArtId, out _));
        Assert.Equal(0, engine.Index.UseCount(dead.ArtId));
        Assert.True(engine.Index.TryGet(live.ArtId, out var liveRow));
        Assert.Equal(ArticleStorageState.Present, liveRow.State);
        Assert.True(engine.TryRead(live.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(live.ArtData.Span));
        Assert.True(engine.Index.UseCount(live.ArtId) >= 1);
        Assert.True(engine.Index.UseCountEntryCount < useBefore);
        Assert.False(engine.Segments.TryGetSegmentInfo(deadMeta.Location.SegmentId, out _));
    }

    [Fact]
    public async Task Reclaim_of_the_source_keeps_a_relocated_article()
    {
        using var dir = TempDir.Create();
        var record = Record("<hist-move@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await AcceptAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var source));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        var compact = await engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var moved));
        Assert.NotEqual(source.Location.SegmentId, moved.Location.SegmentId);
        var retired = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retired.Outcome);

        var reclaimed = await engine.ReclaimRetiredSegmentAsync(source.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimed.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Present, after.State);
        Assert.Equal(moved.Location.SegmentId, after.Location.SegmentId);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.False(engine.Segments.TryGetSegmentInfo(source.Location.SegmentId, out _));
    }

    [Fact]
    public async Task Present_reference_is_not_removed_when_reclaim_is_refused()
    {
        using var dir = TempDir.Create();
        var record = Record("<hist-present@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await AcceptAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        Assert.True(engine.Catalogue.TryRetire(meta.Location.SegmentId, info.Generation, DateTimeOffset.UtcNow));

        var result = await engine.ReclaimRetiredSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.RejectedPresentRemain, result.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Present, after.State);
    }

    [Fact]
    public async Task Same_article_can_be_accepted_again_after_reclamation()
    {
        using var dir = TempDir.Create();
        var record = Record("<hist-again@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await AcceptAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.True(engine.TryEvict(record.ArtId));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        await ReclaimClosedAsync(engine, meta.Location.SegmentId);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));

        var again = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, again.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var present));
        Assert.Equal(ArticleStorageState.Present, present.State);
        Assert.NotEqual(meta.Location.SegmentId, present.Location.SegmentId);
        Assert.Equal(1, engine.Index.Snapshot().Count(row => row.ArtId == record.ArtId));
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Invalid_row_remains_until_its_segment_is_reclaimed()
    {
        using var dir = TempDir.Create();
        var record = Record("<hist-invalid@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await AcceptAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.True(engine.TryInvalidate(record.ArtId));
        Assert.True(engine.Index.TryGet(record.ArtId, out var invalid));
        Assert.Equal(ArticleStorageState.Invalid, invalid.State);
        Assert.False(engine.TryRead(record.ArtId, out _));

        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        await ReclaimClosedAsync(engine, meta.Location.SegmentId);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task Evicted_row_survives_restart_while_the_segment_file_exists()
    {
        using var dir = TempDir.Create();
        var record = Record("<hist-keep@seg.test>");
        SegmentId segmentId;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            await AcceptAsync(engine, record);
            Assert.True(engine.TryEvict(record.ArtId));
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            segmentId = meta.Location.SegmentId;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.True(restarted.Index.TryGet(record.ArtId, out var row));
        Assert.Equal(ArticleStorageState.Evicted, row.State);
        Assert.Equal(segmentId, row.Location.SegmentId);
        Assert.True(restarted.Segments.TryGetSegmentInfo(segmentId, out _));
    }

    [Fact]
    public async Task Delete_without_catalogue_removal_keeps_the_row_until_restart()
    {
        using var dir = TempDir.Create();
        var record = Record("<hist-mid@seg.test>");
        SegmentId segmentId;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            segmentId = await EvictCloseRetireAsync(engine, record);
            engine.TestReclamationFaultPoint =
                FileArticleStorageEngine.ReclamationFaultPoint.AfterDeleteBeforeCatalogueRemove;
            await Assert.ThrowsAsync<IOException>(() =>
                engine.ReclaimRetiredSegmentAsync(segmentId, CancellationToken.None));
            Assert.True(engine.Index.TryGet(record.ArtId, out var mid));
            Assert.Equal(ArticleStorageState.Evicted, mid.State);
            Assert.True(engine.Segments.TryGetSegmentInfo(segmentId, out _));
            Assert.False(File.Exists(RetiredPath(dir, segmentId)));
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.False(restarted.Index.TryGet(record.ArtId, out _));
        Assert.False(restarted.Segments.TryGetSegmentInfo(segmentId, out _));
        Assert.Equal(0, restarted.Index.UseCountEntryCount);
    }

    [Fact]
    public async Task Restart_after_reclaim_does_not_resurrect_the_row_before_checkpoint()
    {
        using var dir = TempDir.Create();
        var record = Record("<hist-replay@seg.test>");
        var kept = Record("<hist-replay-kept@seg.test>");
        SegmentId segmentId;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            await AcceptAsync(engine, record);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            segmentId = meta.Location.SegmentId;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            await AcceptAsync(engine, kept);
            Assert.True(engine.TryEvict(record.ArtId));
            await ReclaimClosedAsync(engine, segmentId);
            Assert.False(engine.Index.TryGet(record.ArtId, out _));
            Assert.True(engine.Index.CopyIndexBytes().Length > ArticleIndexRecordCodec.RecordLength);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.False(restarted.Index.TryGet(record.ArtId, out _));
        Assert.True(restarted.Index.TryGet(kept.ArtId, out var live));
        Assert.Equal(ArticleStorageState.Present, live.State);
        Assert.True(restarted.TryRead(kept.ArtId, out _));
        Assert.False(restarted.Segments.TryGetSegmentInfo(segmentId, out _));
    }

    [Fact]
    public async Task Checkpoint_after_reclaim_omits_the_row_across_restart()
    {
        using var dir = TempDir.Create();
        var record = Record("<hist-ckpt@seg.test>");
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            await AcceptAsync(engine, record);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            Assert.True(engine.TryEvict(record.ArtId));
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            await ReclaimClosedAsync(engine, meta.Location.SegmentId);
            Assert.True(engine.CheckpointIndex() > 0);
            Assert.False(engine.Index.TryGet(record.ArtId, out _));
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.False(restarted.Index.TryGet(record.ArtId, out _));
        Assert.DoesNotContain(restarted.Index.Snapshot(), row => row.ArtId == record.ArtId);
    }

    [Fact]
    public async Task Many_reclaimed_rows_do_not_remain_in_the_index()
    {
        using var dir = TempDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var records = new ArticleRecord[32];
        for (var i = 0; i < records.Length; i++)
        {
            records[i] = Record($"<hist-bulk-{i}@seg.test>");
            await AcceptAsync(engine, records[i]);
            engine.Index.TouchHint(records[i].ArtId, DateTimeOffset.UtcNow);
        }

        Assert.True(engine.Index.TryGet(records[0].ArtId, out var meta));
        foreach (var record in records)
        {
            Assert.True(engine.TryEvict(record.ArtId));
        }

        var kept = Record("<hist-bulk-kept@seg.test>");
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        await AcceptAsync(engine, kept);
        await ReclaimClosedAsync(engine, meta.Location.SegmentId);

        foreach (var record in records)
        {
            Assert.False(engine.Index.TryGet(record.ArtId, out _));
        }

        Assert.True(engine.Index.TryGet(kept.ArtId, out _));
        Assert.Equal(1, engine.Index.Snapshot().Count(row => row.State == ArticleStorageState.Present));
        Assert.Equal(0, engine.Index.UseCountEntryCount);
    }

    private static async Task ReclaimClosedAsync(FileArticleStorageEngine engine, SegmentId segmentId)
    {
        engine.CompleteUnreferencedExtentAccounting();
        var compact = await engine.CompactClosedSegmentAsync(segmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        var retired = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retired.Outcome);
        var reclaimed = await engine.ReclaimRetiredSegmentAsync(segmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimed.Outcome);
    }

    private static async Task<SegmentId> EvictCloseRetireAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        await AcceptAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        engine.Index.TouchHint(record.ArtId, DateTimeOffset.UtcNow);
        Assert.True(engine.TryEvict(record.ArtId));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        var compact = await engine.CompactClosedSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        var retired = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retired.Outcome);
        return meta.Location.SegmentId;
    }

    private static string RetiredPath(TempDir dir, SegmentId segmentId) =>
        Path.Combine(dir.Options.SegmentDir, SegmentFileNames.Format(segmentId, SegmentFileKind.Retired));

    private static async Task AcceptAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
    }

    private static ArticleRecord Record(string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase15\r\n\r\nbody\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase15-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            return new TempDir(
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
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
