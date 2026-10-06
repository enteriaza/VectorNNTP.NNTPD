using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// One maintenance pass classifies the article index once, then proves each Closed segment.
/// </summary>
public sealed class ClosedSegmentAccountingSnapshotTests
{
    [Fact]
    public async Task Several_closed_segments_are_accounted_from_one_index_classification()
    {
        using var dir = TempDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var locations = new StoredArticleLocation[3];
        for (var i = 0; i < locations.Length; i++)
        {
            locations[i] = await AcceptAndCloseAsync(engine, Record($"<acct16-{i}@seg.test>"));
        }

        var snapshotsBefore = engine.Index.SnapshotCallCount;
        var copiesBefore = engine.Index.ClosedAccountingIndexCopies;
        engine.CompleteUnreferencedExtentAccounting();

        Assert.Equal(snapshotsBefore, engine.Index.SnapshotCallCount);
        Assert.Equal(copiesBefore + 1, engine.Index.ClosedAccountingIndexCopies);
        Assert.True(engine.IsUnreferencedExtentAccountingComplete);
        foreach (var location in locations)
        {
            Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
            Assert.Equal(SegmentState.Closed, info.State);
            Assert.True(info.ExtentAccountingComplete);
            Assert.Equal(location.Length, info.LiveBytes);
            Assert.Equal(0, info.DeadBytes);
            Assert.Equal(info.SizeBytes, info.LiveBytes + info.DeadBytes);
        }
    }

    [Fact]
    public async Task Evicted_and_invalid_rows_count_as_dead_until_reclamation()
    {
        using var dir = TempDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var evicted = Record("<acct16-evicted@seg.test>");
        var invalid = Record("<acct16-invalid@seg.test>");
        var evictedLocation = await AcceptAndCloseAsync(engine, evicted);
        var invalidLocation = await AcceptAndCloseAsync(engine, invalid);
        Assert.True(engine.TryEvict(evicted.ArtId));
        Assert.True(engine.TryInvalidate(invalid.ArtId));

        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Index.TryGet(evicted.ArtId, out var evictedRow));
        Assert.Equal(ArticleStorageState.Evicted, evictedRow.State);
        Assert.True(engine.Index.TryGet(invalid.ArtId, out var invalidRow));
        Assert.Equal(ArticleStorageState.Invalid, invalidRow.State);
        AssertClosedDead(engine, evictedLocation);
        AssertClosedDead(engine, invalidLocation);
    }

    [Fact]
    public async Task Relocated_article_is_live_on_the_destination_and_dead_on_the_source()
    {
        using var dir = TempDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = Record("<acct16-move@seg.test>");
        var source = await AcceptAndCloseAsync(engine, record);
        var compact = await engine.CompactClosedSegmentAsync(source.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var moved));
        Assert.NotEqual(source.SegmentId, moved.Location.SegmentId);
        Assert.Equal(ArticleStorageState.Present, moved.State);

        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Segments.TryGetSegmentInfo(source.SegmentId, out var sourceInfo));
        Assert.Equal(SegmentState.Closed, sourceInfo.State);
        Assert.True(sourceInfo.ExtentAccountingComplete);
        Assert.Equal(0, sourceInfo.LiveBytes);
        Assert.Equal(sourceInfo.SizeBytes, sourceInfo.DeadBytes);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(moved.Location.SegmentId, read.Metadata.Location.SegmentId);
    }

    [Fact]
    public async Task A_failed_proof_does_not_account_that_segment_or_its_sibling()
    {
        using var dir = TempDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var good = await AcceptAndCloseAsync(engine, Record("<acct16-good@seg.test>"));
        var bad = await AcceptAndCloseAsync(engine, Record("<acct16-bad@seg.test>"));
        var calls = 0;
        engine.Segments.TestHookDuringClosedExtentScan = () =>
        {
            calls++;
            if (calls == 1)
            {
                throw new IOException("closed-segment-unreadable");
            }
        };

        var copiesBefore = engine.Index.ClosedAccountingIndexCopies;
        engine.CompleteUnreferencedExtentAccounting();

        Assert.Equal(copiesBefore + 1, engine.Index.ClosedAccountingIndexCopies);
        Assert.Equal(1, CountAccounted(engine));
        Assert.Equal(1, CountClosed(engine) - CountAccounted(engine));
        Assert.True(engine.Segments.TryGetSegmentInfo(good.SegmentId, out var goodInfo));
        Assert.True(engine.Segments.TryGetSegmentInfo(bad.SegmentId, out var badInfo));
        Assert.NotEqual(goodInfo.ExtentAccountingComplete, badInfo.ExtentAccountingComplete);
        Assert.False(engine.IsUnreferencedExtentAccountingComplete);
    }

    [Fact]
    public async Task Missing_closed_file_is_not_marked_accounted()
    {
        using var dir = TempDir.Create();
        var missingRecord = Record("<acct16-missing@seg.test>");
        var goodRecord = Record("<acct16-present@seg.test>");
        StoredArticleLocation missing;
        StoredArticleLocation good;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            missing = await AcceptAndCloseAsync(engine, missingRecord);
            good = await AcceptAndCloseAsync(engine, goodRecord);
        }

        File.Delete(Path.Combine(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(missing.SegmentId, SegmentFileKind.Closed)));

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        var copiesBefore = restarted.Index.ClosedAccountingIndexCopies;
        restarted.CompleteUnreferencedExtentAccounting();

        Assert.False(restarted.Segments.TryGetSegmentInfo(missing.SegmentId, out _));
        Assert.True(restarted.Index.TryGet(missingRecord.ArtId, out var missingRow));
        Assert.Equal(ArticleStorageState.Invalid, missingRow.State);
        Assert.False(restarted.TryRead(missingRecord.ArtId, out _));
        Assert.Equal(copiesBefore + 1, restarted.Index.ClosedAccountingIndexCopies);
        Assert.True(restarted.Segments.TryGetSegmentInfo(good.SegmentId, out var goodInfo));
        Assert.True(goodInfo.ExtentAccountingComplete);
        Assert.Equal(good.Length, goodInfo.LiveBytes);
        Assert.Equal(0, goodInfo.DeadBytes);
    }

    [Fact]
    public async Task Corrupt_tail_is_not_marked_accounted()
    {
        using var dir = TempDir.Create();
        StoredArticleLocation location;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            location = await AcceptAndCloseAsync(engine, Record("<acct16-corrupt@seg.test>"));
        }

        var path = Path.Combine(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(location.SegmentId, SegmentFileKind.Closed));
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write([0x01, 0x02, 0x03, 0x04]);
            stream.Flush(flushToDisk: true);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        restarted.CompleteUnreferencedExtentAccounting();
        Assert.True(restarted.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.False(info.ExtentAccountingComplete);
        Assert.True(info.SizeBytes > info.LiveBytes + info.DeadBytes);
    }

    [Fact]
    public async Task Restart_clears_the_accounted_bit_and_the_next_pass_balances_again()
    {
        using var dir = TempDir.Create();
        var record = Record("<acct16-restart@seg.test>");
        StoredArticleLocation location;
        long live;
        long dead;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            location = await AcceptAndCloseAsync(engine, record);
            Assert.True(engine.TryEvict(record.ArtId));
            engine.CompleteUnreferencedExtentAccounting();
            Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var accounted));
            Assert.True(accounted.ExtentAccountingComplete);
            live = accounted.LiveBytes;
            dead = accounted.DeadBytes;
            Assert.Equal(0, live);
            Assert.Equal(accounted.SizeBytes, dead);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.True(restarted.Segments.TryGetSegmentInfo(location.SegmentId, out var before));
        Assert.False(before.ExtentAccountingComplete);
        Assert.Equal(0, before.LiveBytes);
        restarted.CompleteUnreferencedExtentAccounting();
        Assert.True(restarted.Segments.TryGetSegmentInfo(location.SegmentId, out var after));
        Assert.True(after.ExtentAccountingComplete);
        Assert.Equal(live, after.LiveBytes);
        Assert.Equal(dead, after.DeadBytes);
        Assert.True(restarted.Index.TryGet(record.ArtId, out var row));
        Assert.Equal(ArticleStorageState.Evicted, row.State);
    }

    [Fact]
    public async Task Overlapping_accounting_passes_do_not_double_count_dead_bytes()
    {
        using var dir = TempDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var location = await AcceptAndCloseAsync(engine, Record("<acct16-overlap@seg.test>"));
        var first = Task.Run(() => engine.CompleteUnreferencedExtentAccounting());
        var second = Task.Run(() => engine.CompleteUnreferencedExtentAccounting());
        await Task.WhenAll(first, second);

        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.True(info.ExtentAccountingComplete);
        Assert.Equal(location.Length, info.LiveBytes);
        Assert.Equal(0, info.DeadBytes);
        Assert.Equal(info.SizeBytes, info.LiveBytes + info.DeadBytes);
    }

    private static void AssertClosedDead(FileArticleStorageEngine engine, StoredArticleLocation location)
    {
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.True(info.ExtentAccountingComplete);
        Assert.Equal(0, info.LiveBytes);
        Assert.Equal(location.Length, info.DeadBytes);
        Assert.Equal(info.SizeBytes, info.LiveBytes + info.DeadBytes);
    }

    private static int CountAccounted(FileArticleStorageEngine engine) =>
        engine.Catalogue.Snapshot().Count(info => info.State == SegmentState.Closed && info.ExtentAccountingComplete);

    private static int CountClosed(FileArticleStorageEngine engine) =>
        engine.Catalogue.Snapshot().Count(info => info.State == SegmentState.Closed);

    private static async Task<StoredArticleLocation> AcceptAndCloseAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        return meta.Location;
    }

    private static ArticleRecord Record(string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase16\r\n\r\nbody\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase16-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "control"));
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            return new TempDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: Path.Combine(root, "control"),
                    SegmentDir: Path.Combine(root, "cache"),
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
