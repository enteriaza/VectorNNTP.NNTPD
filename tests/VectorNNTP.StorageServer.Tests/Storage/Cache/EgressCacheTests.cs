using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Cache.Egress;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Tests.Storage.Cache;

/// <summary>
/// Disposable NVMe egress cache: read path, admission, eviction, and restart.
/// </summary>
public sealed partial class EgressCacheTests
{
    [Fact]
    public async Task Disabled_cache_does_not_create_files_or_change_the_read()
    {
        using var dir = TempDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: new ArticleMemoryCache(0));
        var record = CreateRecord("<egress-off@seg.test>");
        await PublishAsync(engine, record);

        Assert.False(engine.Egress.IsEnabled);
        Assert.False(Directory.Exists(Path.Combine(dir.Options.ControlDir, "egress")));
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, engine.SegmentArticleReadCount);
        Assert.Equal(0, engine.EgressCacheHitCount);
    }

    [Fact]
    public async Task Miss_then_repeated_read_hits_without_another_segment_read()
    {
        using var dir = TempDir.Create();
        await using var engine = OpenEnabled(dir);
        var record = CreateRecord("<egress-hit@seg.test>");
        await PublishAsync(engine, record);

        Assert.True(engine.TryRead(record.ArtId, out _));
        await engine.Egress.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, engine.EgressCachePopulateCount);

        Assert.True(engine.TryRead(record.ArtId, out var filled));
        Assert.True(filled.ArtData.Span.SequenceEqual(record.ArtData.Span));
        await engine.Egress.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, engine.EgressCachePopulateCount);
        Assert.Equal(2, engine.SegmentArticleReadCount);

        var touches = engine.Index.TouchHintCount;
        var uses = engine.Index.UseCount(record.ArtId);
        Assert.True(engine.Index.TryGet(record.ArtId, out var before));
        Assert.True(engine.TryRead(record.ArtId, out var hit));
        Assert.True(hit.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(before.Sequence, hit.Metadata.Sequence);
        Assert.Equal(before.ArtHash, hit.Metadata.ArtHash);
        Assert.Equal(before.Location, hit.Metadata.Location);
        Assert.Equal(2, engine.SegmentArticleReadCount);
        Assert.Equal(1, engine.EgressCacheHitCount);
        Assert.Equal(touches, engine.Index.TouchHintCount);
        Assert.Equal(uses, engine.Index.UseCount(record.ArtId));
        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(before.LastAccessUtc, after.LastAccessUtc);
        Assert.Equal(ArticleStorageState.Present, after.State);
    }

    [Fact]
    public async Task Ram_hit_does_not_populate_egress()
    {
        using var dir = TempDir.Create();
        await using var engine = OpenEnabled(dir, ramBytes: 4L * 1024 * 1024);
        var record = CreateRecord("<egress-ram@seg.test>");
        await PublishAsync(engine, record);

        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        await engine.Egress.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, engine.CacheArticleReadCount);
        Assert.Equal(0, engine.SegmentArticleReadCount);
        Assert.Equal(0, engine.EgressCachePopulateCount);
        Assert.Equal(0, engine.EgressCacheHitCount);
    }

    [Fact]
    public async Task Journal_read_does_not_populate_egress()
    {
        using var dir = TempDir.Create();
        await using var engine = OpenEnabled(dir);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<egress-journal@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        await engine.Egress.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, engine.JournalArticleReadCount);
        Assert.Equal(0, engine.SegmentArticleReadCount);
        Assert.Equal(0, engine.EgressCachePopulateCount);
        Assert.Empty(Directory.EnumerateFiles(Live(dir), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Failed_segment_read_does_not_populate()
    {
        using var dir = TempDir.Create();
        await using var engine = OpenEnabled(dir);
        var record = CreateRecord("<egress-bad-sata@seg.test>");
        await PublishAsync(engine, record);
        engine.TestFailNextIndexedProvenReads = 1;

        Assert.False(engine.TryRead(record.ArtId, out _));
        await engine.Egress.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, engine.EgressCachePopulateCount);
    }

    [Fact]
    public async Task Relocation_stays_a_hit()
    {
        using var dir = TempDir.Create();
        await using var engine = OpenEnabled(dir);
        var record = CreateRecord("<egress-move@seg.test>");
        await PublishAsync(engine, record);
        await WarmAsync(engine, record.ArtId);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var moved = new StoredArticleLocation(meta.Location.SegmentId, meta.Location.Offset + 1, meta.Location.Length);
        Assert.Equal(
            ArticleRelocateOutcome.Relocated,
            engine.Index.TryRelocate(record.ArtId, meta.Location, moved, record.ArtHash, record.ArtSize));

        var segments = engine.SegmentArticleReadCount;
        Assert.True(engine.TryRead(record.ArtId, out var hit));
        Assert.Equal(moved, hit.Metadata.Location);
        Assert.True(hit.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(segments, engine.SegmentArticleReadCount);
        Assert.Equal(ArticleStorageState.Present, hit.Metadata.State);
    }

    [Fact]
    public async Task New_sequence_cannot_use_the_previous_cache_entry()
    {
        using var dir = TempDir.Create();
        await using var engine = OpenEnabled(dir);
        var record = CreateRecord("<egress-incarnation@seg.test>", "same-body\r\n");
        await PublishAsync(engine, record);
        await WarmAsync(engine, record.ArtId);
        Assert.True(engine.Index.TryGet(record.ArtId, out var first));
        Assert.False(engine.Egress.TryCopy(record.ArtId, first.Sequence + 1, first.ArtHash, first.ArtSize, out _));

        Assert.True(engine.TryEvict(record.ArtId));
        await PublishAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var second));
        Assert.NotEqual(first.Sequence, second.Sequence);
        var segments = engine.SegmentArticleReadCount;
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(second.Sequence, read.Metadata.Sequence);
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(engine.SegmentArticleReadCount > segments);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(56)]
    [InlineData(70)]
    public async Task Corrupt_cache_record_falls_back_to_sata(int flipOffset)
    {
        using var dir = TempDir.Create();
        await using var engine = OpenEnabled(dir);
        var record = CreateRecord("<egress-corrupt@seg.test>", "intact-body\r\n");
        await PublishAsync(engine, record);
        await WarmAsync(engine, record.ArtId);
        var slab = Directory.EnumerateFiles(Live(dir), "slab-*.bin").Single();
        using (var stream = new FileStream(slab, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var bytes = new byte[stream.Length];
            _ = stream.Read(bytes, 0, bytes.Length);
            bytes[flipOffset] ^= 0xFF;
            stream.Position = 0;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        var segments = engine.SegmentArticleReadCount;
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(record.ArtHash, read.Metadata.ArtHash);
        Assert.True(engine.SegmentArticleReadCount > segments);
        Assert.True(engine.EgressCacheCorruptionCount + engine.EgressCacheInvalidationCount > 0);
    }

    [Fact]
    public async Task Truncated_cache_record_falls_back_to_sata()
    {
        using var dir = TempDir.Create();
        await using var engine = OpenEnabled(dir);
        var record = CreateRecord("<egress-trunc@seg.test>");
        await PublishAsync(engine, record);
        await WarmAsync(engine, record.ArtId);
        var slab = Directory.EnumerateFiles(Live(dir), "slab-*.bin").Single();
        var info = new FileInfo(slab);
        using (var stream = new FileStream(slab, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.SetLength(Math.Max(0, info.Length - 8));
        }

        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(engine.EgressCacheCorruptionCount > 0);
    }

    [Fact]
    public async Task Full_queue_drops_the_fill_and_still_returns_the_article()
    {
        using var dir = TempDir.Create();
        await using var engine = OpenEnabled(dir, queueBytes: 1);
        var record = CreateRecord("<egress-queue@seg.test>");
        await PublishAsync(engine, record);
        Assert.True(engine.TryRead(record.ArtId, out var first));
        Assert.True(engine.TryRead(record.ArtId, out var second));
        Assert.True(first.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(second.ArtData.Span.SequenceEqual(record.ArtData.Span));
        await engine.Egress.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, engine.EgressCachePopulateCount);
        Assert.True(engine.EgressCachePopulateDroppedCount > 0);
    }

    [Fact]
    public async Task Concurrent_cold_reads_populate_once()
    {
        using var dir = TempDir.Create();
        await using var engine = OpenEnabled(dir);
        var record = CreateRecord("<egress-burst@seg.test>", "burst-body\r\n");
        await PublishAsync(engine, record);

        var reads = await ReadTogetherAsync(engine, record.ArtId, readers: 32);
        Assert.All(reads, read => Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span)));
        await engine.Egress.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(1, engine.SegmentArticleReadCount);
        Assert.Equal(1, engine.EgressCachePopulateCount);
    }

    [Fact]
    public async Task Capacity_eviction_keeps_articles_present()
    {
        using var dir = TempDir.Create();
        await using var engine = OpenEnabled(dir, capacity: 8 * 1024, usage: 50, free: 5);
        var records = new List<ArticleRecord>();
        for (var i = 0; i < 40 && engine.EgressCacheEvictionCount == 0; i++)
        {
            var record = CreateRecord($"<egress-evict-{i}@seg.test>", $"body-{i}\r\n");
            records.Add(record);
            await PublishAsync(engine, record);
            await WarmAsync(engine, record.ArtId);
        }

        Assert.True(engine.EgressCacheEvictionCount > 0);
        foreach (var record in records)
        {
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            Assert.Equal(ArticleStorageState.Present, meta.State);
            Assert.True(engine.TryRead(record.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        }
    }

    [Fact]
    public async Task ControlDir_floor_refuses_admission_and_serves_sata()
    {
        using var dir = TempDir.Create();
        var space = new FakeSpace { Available = 100, Total = 1_000_000 };
        await using var engine = OpenEnabled(dir, space: space, journalHard: 10_000);
        var record = CreateRecord("<egress-floor@seg.test>");
        await PublishAsync(engine, record);
        var uses = engine.Index.UseCount(record.ArtId);
        Assert.True(engine.TryRead(record.ArtId, out var first));
        Assert.True(engine.TryRead(record.ArtId, out var second));
        Assert.True(first.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(second.ArtData.Span.SequenceEqual(record.ArtData.Span));
        await engine.Egress.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, engine.EgressCachePopulateCount);
        Assert.True(engine.EgressCacheAdmissionRejectedCount > 0);
        Assert.Equal(0, engine.EgressCacheEntryCount);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(CreateRecord("<egress-floor-accept@seg.test>"), CancellationToken.None)).Outcome);
        Assert.Equal(uses, engine.Index.UseCount(record.ArtId) - 2);
    }

    [Fact]
    public async Task Restart_discards_cache_bytes_and_still_serves_sata()
    {
        using var dir = TempDir.Create();
        var record = CreateRecord("<egress-restart@seg.test>");
        await using (var engine = OpenEnabled(dir))
        {
            await PublishAsync(engine, record);
            await WarmAsync(engine, record.ArtId);
            Assert.Equal(1, engine.EgressCacheEntryCount);
            File.WriteAllBytes(Path.Combine(Live(dir), "partial.bin"), [1, 2, 3]);
        }

        File.WriteAllBytes(Path.Combine(Live(dir), "corrupt.bin"), "not-a-cache"u8.ToArray());
        await using var restarted = OpenEnabled(dir);
        Assert.Equal(0, restarted.EgressCacheEntryCount);
        Assert.True(restarted.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, restarted.SegmentArticleReadCount);
        Assert.Empty(Directory.EnumerateFiles(Live(dir), "corrupt.bin"));
        Assert.Empty(Directory.EnumerateFiles(Live(dir), "partial.bin"));
    }

    [Fact]
    public void Open_replaces_a_garbage_live_directory_without_reading_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "vectornntp-egress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "egress", "live"));
        File.WriteAllBytes(Path.Combine(root, "egress", "live", "slab-00000001.bin"), [0, 1, 2, 3, 4]);
        var cache = ArticleEgressCache.Open(Start(root, new FakeSpace(), capacity: 1_000_000));
        try
        {
            Assert.True(cache.IsEnabled);
            Assert.Equal(0, cache.EntryCount);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "egress", "live")));
        }
        finally
        {
            cache.Dispose();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task WarmAsync(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.TryRead(artId, out _));
        Assert.True(engine.TryRead(artId, out _));
        await engine.Egress.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(engine.EgressCachePopulateCount > 0);
    }

    private static FileArticleStorageEngine OpenEnabled(
        TempDir dir,
        long capacity = 64L * 1024 * 1024,
        long ramBytes = 0,
        long queueBytes = 0,
        int usage = 80,
        int free = 5,
        long journalHard = 0,
        FakeSpace? space = null)
    {
        space ??= new FakeSpace();
        return FileArticleStorageEngine.Open(
            dir.Options,
            volumeProbe: null,
            capacityReader: null,
            controlCapacityReader: null,
            logger: null,
            timeProvider: null,
            articleCache: new ArticleMemoryCache(ramBytes),
            maxConcurrentPhysicalReads: null,
            egressCache: Start(dir.Options.ControlDir, space, capacity, queueBytes, usage, free, journalHard));
    }

    private static EgressCacheStart Start(
        string controlDir,
        FakeSpace space,
        long capacity,
        long queueBytes = 0,
        int usage = 80,
        int free = 5,
        long journalHard = 0,
        int testFailDeletes = 0) =>
        new()
        {
            ControlDir = controlDir,
            CapacityBytes = capacity,
            ReserveBytes = 0,
            JournalHardLimitBytes = journalHard,
            MaximumUtilizationPercent = 100,
            MaximumUsageCapacityPercent = usage,
            FreeCapacityPercent = free,
            Space = space,
            FillQueueMaxBytes = queueBytes,
            TestFailNextDeletes = testFailDeletes,
        };

    private static string Live(TempDir dir) => Path.Combine(dir.Options.ControlDir, "egress", "live");

    private static async Task PublishAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
    }

    private static async Task<ArticleReadResult[]> ReadTogetherAsync(
        FileArticleStorageEngine engine,
        ArticleId artId,
        int readers)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.TestHookBeforeProvenSegmentRead = () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        engine.TestWhenPhysicalReadWaitersChanged = count =>
        {
            if (count >= readers - 1)
            {
                enough.TrySetResult();
            }
        };

        var slots = Enumerable.Range(0, readers)
            .Select(_ => Task.Run(() =>
            {
                var found = engine.TryRead(artId, out var result);
                return (found, result);
            }))
            .ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await enough.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            release.TrySetResult();
            engine.TestHookBeforeProvenSegmentRead = null;
            engine.TestWhenPhysicalReadWaitersChanged = null;
        }

        var completed = await Task.WhenAll(slots).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.All(completed, slot => Assert.True(slot.found));
        return completed.Select(slot => slot.result).ToArray();
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
        _ = builder.Append("Subject: egress-cache\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class FakeSpace : IEgressVolumeSpace
    {
        public long Available { get; set; } = 1L << 40;

        public long Total { get; set; } = 1L << 40;

        public bool TryRead(out long availableBytes, out long totalBytes)
        {
            availableBytes = Available;
            totalBytes = Total;
            return true;
        }
    }

    private sealed class TempDir : IDisposable
    {
        private TempDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string Root { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-egress-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            return new TempDir(
                root,
                new ArticleStorageRuntimeOptions(
                    control,
                    cache,
                    ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    ArticleStorageOptions.DefaultJournalHardLimitBytes,
                    ArticleStorageOptions.DefaultSegmentTargetSizeBytes));
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
