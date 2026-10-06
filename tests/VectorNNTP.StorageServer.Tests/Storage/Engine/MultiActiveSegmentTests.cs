using System.Text;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Several active segment files share one directory. Each file has its own id and writer.
/// </summary>
public sealed class MultiActiveSegmentTests
{
    [Fact]
    public void Batch_assigns_distinct_segments_and_keeps_each_file_ordered()
    {
        using var dir = TempDir.Create(activeSegments: 2);
        using var store = FileSegmentStore.Open(dir.Options);
        var articles = new[]
        {
            Article("<multi-a@seg.test>"),
            Article("<multi-b@seg.test>"),
            Article("<multi-c@seg.test>"),
            Article("<multi-d@seg.test>"),
        };

        var receipts = store.AppendActiveBatch(articles);
        var ids = receipts.Select(static receipt => receipt.Location.SegmentId.Value).Distinct().ToArray();

        Assert.Equal(2, ids.Length);
        Assert.Equal(2, store.OpenActiveSegmentCount);
        Assert.Equal(0, store.PayloadLocationProofCount);
        Assert.Equal(2, store.AgeSealSegmentIds.Length);
        foreach (var id in ids)
        {
            var onSegment = receipts.Where(receipt => receipt.Location.SegmentId.Value == id).ToArray();
            Assert.Equal(2, onSegment.Length);
            Assert.True(onSegment[1].Location.Offset >= onSegment[0].Location.Offset + onSegment[0].Location.Length);
            Assert.True(store.TryRead(onSegment[0].Location, out var first));
            Assert.True(store.TryRead(onSegment[1].Location, out var second));
            Assert.True(first.Length > 0);
            Assert.True(second.Length > 0);
        }
    }

    [Fact]
    public void Four_writers_allocate_four_distinct_ids()
    {
        using var dir = TempDir.Create(activeSegments: 4);
        using var store = FileSegmentStore.Open(dir.Options);
        var articles = Enumerable.Range(0, 4)
            .Select(index => Article($"<multi-four-{index}@seg.test>"))
            .ToArray();

        var receipts = store.AppendActiveBatch(articles);
        var ids = receipts.Select(static receipt => receipt.Location.SegmentId.Value).Order().ToArray();

        Assert.Equal(4, ids.Distinct().Count());
        Assert.Equal(ids, ids.Distinct().Order().ToArray());
        Assert.Equal(4, store.OpenActiveSegmentCount);
        Assert.Equal(4, Directory.EnumerateFiles(dir.SegmentDir, "seg-*.active").Count());
    }

    [Fact]
    public void Size_rollover_on_one_writer_leaves_the_other_writer_active()
    {
        var sample = Article("<multi-size@seg.test>");
        var oneRecord = SegmentRecordCodec.RecordLengthForArtSize(sample.Length);
        using var dir = TempDir.Create(activeSegments: 2, segmentTarget: oneRecord);
        using var store = FileSegmentStore.Open(dir.Options);
        var articles = Enumerable.Range(0, 4)
            .Select(index => Article($"<multi-roll-{index}@seg.test>"))
            .ToArray();

        var receipts = store.AppendActiveBatch(articles);
        var ids = receipts.Select(static receipt => receipt.Location.SegmentId.Value).Distinct().ToArray();

        Assert.Equal(4, ids.Length);
        Assert.Equal(2, store.OpenActiveSegmentCount);
        Assert.Equal(2, Directory.EnumerateFiles(dir.SegmentDir, "seg-*.closed").Count());
        Assert.Equal(2, Directory.EnumerateFiles(dir.SegmentDir, "seg-*.active").Count());
        for (var i = 0; i < receipts.Length; i++)
        {
            Assert.True(store.TryRead(receipts[i].Location, out var read));
            Assert.Equal(articles[i].Length, read.Length);
        }
    }

    [Fact]
    public void Restart_adopts_every_active_segment()
    {
        using var dir = TempDir.Create(activeSegments: 2);
        StoredArticleLocation[] locations;
        using (var store = FileSegmentStore.Open(dir.Options))
        {
            var receipts = store.AppendActiveBatch(
            [
                Article("<multi-restart-a@seg.test>"),
                Article("<multi-restart-b@seg.test>"),
            ]);
            locations = receipts.Select(static receipt => receipt.Location).ToArray();
        }

        using var restarted = FileSegmentStore.Open(dir.Options);
        Assert.Equal(2, restarted.OpenActiveSegmentCount);
        Assert.Equal(2, restarted.AgeSealSegmentIds.Length);
        foreach (var location in locations)
        {
            Assert.True(restarted.TryRead(location, out _));
        }
    }

    [Fact]
    public async Task Engine_publishes_present_from_each_active_segment()
    {
        using var dir = TempDir.Create(activeSegments: 2, hardLimit: 8L << 20);
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var first = Record("<multi-engine-a@seg.test>", "alpha\r\n");
        var second = Record("<multi-engine-b@seg.test>", "beta\r\n");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(second, CancellationToken.None)).Outcome);
        Assert.True(engine.TryRead(first.ArtId, out var journal));
        Assert.True(journal.ArtData.Span.SequenceEqual(first.ArtData.Span));

        var duplicate = await engine.AcceptAsync(first, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Duplicate, duplicate.Outcome);

        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(0, engine.Segments.PayloadLocationProofCount);
        Assert.True(engine.Index.TryGet(first.ArtId, out var left));
        Assert.True(engine.Index.TryGet(second.ArtId, out var right));
        Assert.Equal(ArticleStorageState.Present, left.State);
        Assert.Equal(ArticleStorageState.Present, right.State);
        Assert.NotEqual(left.Location.SegmentId, right.Location.SegmentId);
        Assert.True(engine.TryRead(first.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(first.ArtData.Span));
        Assert.True(engine.TryRead(second.ArtId, out var readRight));
        Assert.True(readRight.ArtData.Span.SequenceEqual(second.ArtData.Span));
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public void Concurrent_rollover_allocates_increasing_unique_ids()
    {
        var sample = Article("<multi-fence@seg.test>");
        var oneRecord = SegmentRecordCodec.RecordLengthForArtSize(sample.Length);
        using var dir = TempDir.Create(activeSegments: 2, segmentTarget: oneRecord);
        using var store = FileSegmentStore.Open(dir.Options);
        var reserved = new List<ulong>();
        store.ReserveSegmentId = id => reserved.Add(id);
        var articles = Enumerable.Range(0, 4)
            .Select(index => Article($"<multi-fence-{index}@seg.test>"))
            .ToArray();

        _ = store.AppendActiveBatch(articles);

        Assert.Equal(4, reserved.Count);
        Assert.Equal(reserved.Distinct().Count(), reserved.Count);
        Assert.Equal(reserved.Order().ToArray(), reserved.ToArray());
        var catalogueIds = store.Catalogue.Snapshot().Select(static info => info.SegmentId.Value).ToArray();
        Assert.Equal(catalogueIds.Distinct().Count(), catalogueIds.Length);
        Assert.Equal(4, catalogueIds.Length);
        Assert.Equal(2, store.Catalogue.Snapshot().Count(static info => info.State == SegmentState.Active));
        Assert.Equal(2, store.Catalogue.Snapshot().Count(static info => info.State == SegmentState.Closed));
    }

    [Fact]
    public void Torn_tail_on_one_active_segment_leaves_the_other_readable()
    {
        using var dir = TempDir.Create(activeSegments: 2);
        StoredArticleLocation intact;
        string tornPath;
        using (var store = FileSegmentStore.Open(dir.Options))
        {
            var receipts = store.AppendActiveBatch(
            [
                Article("<multi-torn-keep@seg.test>"),
                Article("<multi-torn-cut@seg.test>"),
            ]);
            intact = receipts[0].Location;
            tornPath = Path.Combine(
                dir.SegmentDir,
                SegmentFileNames.Format(receipts[1].Location.SegmentId, SegmentFileKind.Active));
        }

        var bytes = File.ReadAllBytes(tornPath);
        File.WriteAllBytes(tornPath, bytes[..^1]);

        using var restarted = FileSegmentStore.Open(dir.Options);
        Assert.Equal(2, restarted.OpenActiveSegmentCount);
        Assert.Equal(
            restarted.Catalogue.Snapshot().Count,
            restarted.Catalogue.Snapshot().Select(static info => info.SegmentId.Value).Distinct().Count());
        Assert.True(restarted.TryRead(intact, out var read));
        Assert.True(read.Length > 0);
    }

    [Fact]
    public async Task Age_seal_of_one_segment_leaves_the_other_active()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var delay = TimeSpan.FromMinutes(1);
        using var dir = TempDir.Create(activeSegments: 2, sealDelay: delay);
        using var store = FileSegmentStore.Open(dir.Options, timeProvider: time);
        var receipts = store.AppendActiveBatch(
        [
            Article("<multi-age-a@seg.test>"),
            Article("<multi-age-b@seg.test>"),
        ]);
        var left = receipts[0].Location.SegmentId.Value;
        var right = receipts[1].Location.SegmentId.Value;
        Assert.Equal(2, store.AgeSealSegmentIds.Length);
        Assert.Contains(left, store.AgeSealSegmentIds);
        Assert.Contains(right, store.AgeSealSegmentIds);

        time.Advance(delay - TimeSpan.FromTicks(1));
        Assert.False(store.TrySealActiveForAge(left));
        Assert.False(store.TrySealActiveForAge(right));
        Assert.Equal(2, store.OpenActiveSegmentCount);

        var armed = store.AgeSealTasks;
        time.Advance(TimeSpan.FromTicks(1));
        await Task.WhenAll(armed).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, store.SegmentSealByAgeCount);
        Assert.Equal(0, store.OpenActiveSegmentCount);
        Assert.False(store.TrySealActiveForAge(left));
        Assert.False(store.TrySealActiveForAge(right));
        Assert.True(store.Catalogue.TryGet(new SegmentId(left), out var sealedLeft));
        Assert.True(store.Catalogue.TryGet(new SegmentId(right), out var sealedRight));
        Assert.Equal(SegmentState.Closed, sealedLeft.State);
        Assert.Equal(SegmentState.Closed, sealedRight.State);
        Assert.True(store.TryRead(receipts[0].Location, out _));
        Assert.True(store.TryRead(receipts[1].Location, out _));
    }

    [Fact]
    public async Task Armed_age_timers_seal_only_their_own_segments()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var delay = TimeSpan.FromMinutes(1);
        using var dir = TempDir.Create(activeSegments: 4, sealDelay: delay);
        using var store = FileSegmentStore.Open(dir.Options, timeProvider: time);
        var receipts = store.AppendActiveBatch(
        [
            Article("<multi-timer-a@seg.test>"),
            Article("<multi-timer-b@seg.test>"),
        ]);
        var armed = store.AgeSealTasks;
        Assert.Equal(2, armed.Length);
        Assert.Equal(4, store.OpenActiveSegmentCount);

        time.Advance(delay);
        await Task.WhenAll(armed).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, store.SegmentSealByAgeCount);
        Assert.Equal(2, store.OpenActiveSegmentCount);
        Assert.False(store.TrySealActiveForAge(receipts[0].Location.SegmentId.Value));
        Assert.False(store.TrySealActiveForAge(ulong.MaxValue));
        foreach (var receipt in receipts)
        {
            Assert.True(store.TryRead(receipt.Location, out var read));
            Assert.True(read.Length > 0);
        }
    }

    [Fact]
    public async Task Relocation_of_one_closed_segment_keeps_the_other_segment_readable()
    {
        using var dir = TempDir.Create(activeSegments: 2, hardLimit: 8L << 20);
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var first = Record("<multi-reloc-a@seg.test>", "alpha\r\n");
        var second = Record("<multi-reloc-b@seg.test>", "beta\r\n");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(second, CancellationToken.None)).Outcome);
        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(first.ArtId, out var beforeLeft));
        Assert.True(engine.Index.TryGet(second.ArtId, out var beforeRight));
        Assert.NotEqual(beforeLeft.Location.SegmentId, beforeRight.Location.SegmentId);

        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.Equal(1, engine.Segments.OpenActiveSegmentCount);
        Assert.True(engine.Segments.TryGetSegmentInfo(beforeLeft.Location.SegmentId, out var leftInfo));
        Assert.True(engine.Segments.TryGetSegmentInfo(beforeRight.Location.SegmentId, out var rightInfo));
        var closedRecord = leftInfo.State == SegmentState.Closed ? first : second;
        var closedMeta = leftInfo.State == SegmentState.Closed ? beforeLeft : beforeRight;
        var closedInfo = leftInfo.State == SegmentState.Closed ? leftInfo : rightInfo;
        var stayedRecord = leftInfo.State == SegmentState.Active ? first : second;
        var stayedLocation = leftInfo.State == SegmentState.Active ? beforeLeft.Location : beforeRight.Location;
        Assert.Equal(SegmentState.Closed, closedInfo.State);

        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(
                    1,
                    compactionId,
                    closedMeta.Location.SegmentId,
                    closedInfo.Generation),
                CancellationToken.None));
        var relocated = await engine.RelocateArticleAsync(
            compactionId,
            1,
            closedMeta.Location.SegmentId,
            closedInfo.Generation,
            closedRecord.ArtId,
            CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, relocated.Outcome);

        Assert.True(engine.TryRead(closedRecord.ArtId, out var moved));
        Assert.True(moved.ArtData.Span.SequenceEqual(closedRecord.ArtData.Span));
        Assert.True(engine.TryRead(stayedRecord.ArtId, out var kept));
        Assert.True(kept.ArtData.Span.SequenceEqual(stayedRecord.ArtData.Span));
        Assert.True(engine.Index.TryGet(stayedRecord.ArtId, out var stayedMeta));
        Assert.Equal(stayedLocation, stayedMeta.Location);
        var ids = engine.Segments.Catalogue.Snapshot().Select(static info => info.SegmentId.Value).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public async Task Recovered_articles_stay_readable_from_both_segments()
    {
        using var dir = TempDir.Create(activeSegments: 2, hardLimit: 8L << 20);
        var first = Record("<multi-recover-a@seg.test>", "one\r\n");
        var second = Record("<multi-recover-b@seg.test>", "two\r\n");
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(second, CancellationToken.None)).Outcome);
            engine.SuspendBackgroundPersist = false;
            await engine.DrainPendingAsync(CancellationToken.None);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.Equal(2, restarted.Segments.OpenActiveSegmentCount);
        Assert.True(restarted.TryRead(first.ArtId, out var left));
        Assert.True(restarted.TryRead(second.ArtId, out var right));
        Assert.True(left.ArtData.Span.SequenceEqual(first.ArtData.Span));
        Assert.True(right.ArtData.Span.SequenceEqual(second.ArtData.Span));
        Assert.NotEqual(left.Metadata.Location.SegmentId, right.Metadata.Location.SegmentId);
    }

    private static ReadOnlyMemory<byte> Article(string messageId) => Record(messageId, "body\r\n").ArtData.ToArray();

    private static ArticleRecord Record(string messageId, string body)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: multi\r\n\r\n").Append(body);
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
            SegmentDir = options.SegmentDir;
        }

        public string Root { get; }

        public string SegmentDir { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempDir Create(
            int activeSegments,
            long? segmentTarget = null,
            long hardLimit = 32L << 20,
            TimeSpan? sealDelay = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-multi-seg-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            return new TempDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: control,
                    SegmentDir: cache,
                    JournalSoftLimitBytes: Math.Min(8L << 20, hardLimit),
                    JournalHardLimitBytes: hardLimit,
                    SegmentTargetSizeBytes: segmentTarget ?? ArticleStorageOptions.DefaultSegmentTargetSizeBytes,
                    MaxSegmentSealDelay: sealDelay ?? TimeSpan.FromHours(1),
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
