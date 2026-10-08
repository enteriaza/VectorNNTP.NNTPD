using System.IO.Hashing;
using System.Text;
using Microsoft.Extensions.Logging;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Cache.Egress;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Tests.Storage.Cache;

/// <summary>Phase 42 regressions for trash reclamation, oversized admission, and fill-worker faults.</summary>
public sealed partial class EgressCacheTests
{
    [Fact]
    public void Startup_reclaims_stale_trash_and_leaves_unrelated_files()
    {
        using var dir = TempDir.Create();
        var egress = Path.Combine(dir.Options.ControlDir, "egress");
        var journal = Path.Combine(dir.Options.ControlDir, "journal.dat");
        var unrelated = Path.Combine(egress, "not-trash");
        var trash = Path.Combine(egress, "trash-stale");
        Directory.CreateDirectory(Path.Combine(egress, "live"));
        Directory.CreateDirectory(unrelated);
        Directory.CreateDirectory(trash);
        File.WriteAllBytes(journal, "journal"u8.ToArray());
        File.WriteAllBytes(Path.Combine(unrelated, "keep.bin"), "keep"u8.ToArray());
        File.WriteAllBytes(Path.Combine(egress, "readme.txt"), "readme"u8.ToArray());
        File.WriteAllBytes(Path.Combine(trash, "old.bin"), "old"u8.ToArray());
        File.WriteAllBytes(Path.Combine(egress, "live", "previous.bin"), "previous"u8.ToArray());

        var cache = ArticleEgressCache.Open(Start(dir.Options.ControlDir, new FakeSpace(), capacity: 1_000_000));
        try
        {
            Assert.True(cache.IsEnabled);
            Assert.False(Directory.Exists(trash));
            Assert.True(Directory.Exists(Path.Combine(egress, "live")));
            Assert.Equal("keep"u8.ToArray(), File.ReadAllBytes(Path.Combine(unrelated, "keep.bin")));
            Assert.Equal("readme"u8.ToArray(), File.ReadAllBytes(Path.Combine(egress, "readme.txt")));
            Assert.Equal("journal"u8.ToArray(), File.ReadAllBytes(journal));
            Assert.Equal(0, cache.UnreclaimedBytes);
            Assert.Empty(Directory.EnumerateDirectories(egress, "trash-*"));
        }
        finally
        {
            cache.Dispose();
        }
    }

    [Fact]
    public void Failed_trash_delete_stays_accounted_until_a_later_reclaim()
    {
        using var dir = TempDir.Create();
        var egress = Path.Combine(dir.Options.ControlDir, "egress");
        var trash = Path.Combine(egress, "trash-stuck");
        Directory.CreateDirectory(trash);
        var payload = new byte[1000];
        File.WriteAllBytes(Path.Combine(trash, "old.bin"), payload);

        var cache = ArticleEgressCache.Open(
            Start(dir.Options.ControlDir, new FakeSpace(), capacity: 50_000, testFailDeletes: 1));
        try
        {
            Assert.True(Directory.Exists(trash));
            Assert.Equal(payload.Length, cache.UnreclaimedBytes);
            Assert.Equal(0, cache.EntryCount);
            Assert.Equal(0, cache.EvictionCount);

            cache.ReclaimOrphanedTrash();
            Assert.False(Directory.Exists(trash));
            Assert.Equal(0, cache.UnreclaimedBytes);
        }
        finally
        {
            cache.Dispose();
        }
    }

    [Fact]
    public void Restart_after_a_failed_delete_reclaims_orphaned_trash()
    {
        using var dir = TempDir.Create();
        var egress = Path.Combine(dir.Options.ControlDir, "egress");
        var trash = Path.Combine(egress, "trash-restart");
        Directory.CreateDirectory(trash);
        File.WriteAllBytes(Path.Combine(trash, "old.bin"), new byte[32]);

        var first = ArticleEgressCache.Open(
            Start(dir.Options.ControlDir, new FakeSpace(), capacity: 50_000, testFailDeletes: 1));
        Assert.Equal(32, first.UnreclaimedBytes);
        first.Dispose();
        Assert.True(Directory.Exists(trash));

        var second = ArticleEgressCache.Open(Start(dir.Options.ControlDir, new FakeSpace(), capacity: 50_000));
        try
        {
            Assert.False(Directory.Exists(trash));
            Assert.Equal(0, second.UnreclaimedBytes);
            Assert.True(Directory.Exists(Path.Combine(egress, "live")));
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    public void Reclaim_does_not_delete_the_live_cache_directory()
    {
        using var dir = TempDir.Create();
        var egress = Path.Combine(dir.Options.ControlDir, "egress");
        var cache = ArticleEgressCache.Open(Start(dir.Options.ControlDir, new FakeSpace(), capacity: 50_000));
        try
        {
            var marker = Path.Combine(egress, "live", "marker.bin");
            File.WriteAllBytes(marker, "live"u8.ToArray());
            var trash = Path.Combine(egress, "trash-later");
            Directory.CreateDirectory(trash);
            File.WriteAllBytes(Path.Combine(trash, "old.bin"), new byte[8]);

            cache.ReclaimOrphanedTrash();

            Assert.Equal("live"u8.ToArray(), File.ReadAllBytes(marker));
            Assert.False(Directory.Exists(trash));
            Assert.Equal(0, cache.UnreclaimedBytes);
        }
        finally
        {
            cache.Dispose();
        }
    }

    [Fact]
    public async Task Failed_slab_delete_stays_accounted_and_retries()
    {
        using var dir = TempDir.Create();
        const int artSize = 936;
        var recordLength = EgressCacheRecordCodec.RecordLength(artSize);
        var cache = ArticleEgressCache.Open(
            Start(dir.Options.ControlDir, new FakeSpace(), capacity: recordLength, usage: 100, free: 0));
        try
        {
            QueueFill(cache, "<slab-a@seg.test>", artSize);
            await cache.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, cache.EntryCount);
            Assert.Equal(recordLength, cache.CacheBytes);
            Assert.Equal(0, cache.EvictionCount);

            cache.TestFailNextDeletes = 1;
            cache.Drop(ArticleId.FromMessageId("<slab-a@seg.test>"u8));
            Assert.Equal(0, cache.EntryCount);
            Assert.Equal(0, cache.CacheBytes);
            Assert.True(cache.UnreclaimedBytes >= recordLength);
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(dir.Options.ControlDir, "egress", "live"), "slab-*.bin"));

            cache.TestFailNextDeletes = 1;
            var rejected = cache.AdmissionRejectedCount;
            QueueFill(cache, "<slab-b@seg.test>", artSize);
            await cache.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, cache.EntryCount);
            Assert.Equal(0, cache.EvictionCount);
            Assert.True(cache.AdmissionRejectedCount > rejected);
            Assert.True(cache.UnreclaimedBytes >= recordLength);

            cache.TestFailNextDeletes = 0;
            QueueFill(cache, "<slab-c@seg.test>", artSize);
            await cache.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, cache.EntryCount);
            Assert.Equal(0, cache.UnreclaimedBytes);
            Assert.Equal(0, cache.EvictionCount);
        }
        finally
        {
            cache.Dispose();
        }
    }

    [Fact]
    public async Task Record_smaller_than_capacity_is_admitted()
    {
        using var dir = TempDir.Create();
        const int artSize = 936;
        var recordLength = EgressCacheRecordCodec.RecordLength(artSize);
        var cache = ArticleEgressCache.Open(
            Start(dir.Options.ControlDir, new FakeSpace(), capacity: recordLength + 100, usage: 100, free: 0));
        try
        {
            QueueFill(cache, "<small@seg.test>", artSize);
            await cache.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, cache.EntryCount);
            Assert.Equal(recordLength, cache.CacheBytes);
            Assert.Equal(0, cache.EvictionCount);
            Assert.Equal(0, cache.AdmissionRejectedCount);
        }
        finally
        {
            cache.Dispose();
        }
    }

    [Fact]
    public async Task Record_equal_to_capacity_is_admitted()
    {
        using var dir = TempDir.Create();
        const int artSize = 936;
        var recordLength = EgressCacheRecordCodec.RecordLength(artSize);
        var cache = ArticleEgressCache.Open(
            Start(dir.Options.ControlDir, new FakeSpace(), capacity: recordLength, usage: 100, free: 0));
        try
        {
            QueueFill(cache, "<exact@seg.test>", artSize);
            await cache.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, cache.EntryCount);
            Assert.Equal(recordLength, cache.CacheBytes);
            Assert.Equal(0, cache.EvictionCount);
            Assert.Equal(0, cache.AdmissionRejectedCount);
        }
        finally
        {
            cache.Dispose();
        }
    }

    [Fact]
    public async Task Record_larger_than_capacity_is_rejected_without_eviction()
    {
        using var dir = TempDir.Create();
        const int artSize = 936;
        var recordLength = EgressCacheRecordCodec.RecordLength(artSize);
        var cache = ArticleEgressCache.Open(
            Start(dir.Options.ControlDir, new FakeSpace(), capacity: (recordLength * 2) - 1, usage: 100, free: 0));
        try
        {
            QueueFill(cache, "<kept@seg.test>", artSize);
            await cache.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, cache.EntryCount);
            var occupancy = cache.CacheBytes;
            var evictions = cache.EvictionCount;

            QueueFill(cache, "<oversized@seg.test>", 2_000);
            await cache.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, cache.EntryCount);
            Assert.Equal(occupancy, cache.CacheBytes);
            Assert.Equal(evictions, cache.EvictionCount);
            Assert.True(cache.AdmissionRejectedCount > 0);
            Assert.True(cache.TryCopy(
                ArticleId.FromMessageId("<kept@seg.test>"u8),
                1,
                XxHash3.HashToUInt64(new byte[artSize]),
                artSize,
                out _));
        }
        finally
        {
            cache.Dispose();
        }
    }

    [Fact]
    public async Task Undersized_record_still_evicts_when_free_space_is_short()
    {
        using var dir = TempDir.Create();
        const int artSize = 936;
        var recordLength = EgressCacheRecordCodec.RecordLength(artSize);
        var cache = ArticleEgressCache.Open(
            Start(dir.Options.ControlDir, new FakeSpace(), capacity: (recordLength * 2) - 1, usage: 100, free: 0));
        try
        {
            QueueFill(cache, "<evict-a@seg.test>", artSize);
            await cache.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            QueueFill(cache, "<evict-b@seg.test>", artSize);
            await cache.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(cache.EvictionCount > 0);
            Assert.Equal(1, cache.EntryCount);
            Assert.True(cache.CacheBytes <= cache.CapacityBytes);
            Assert.True(cache.CacheBytes > 0);
        }
        finally
        {
            cache.Dispose();
        }
    }

    [Fact]
    public async Task Oversized_record_does_not_change_authoritative_article_state()
    {
        using var dir = TempDir.Create();
        var small = CreateRecord("<egress-fit@seg.test>");
        var large = CreateRecord("<egress-over@seg.test>", new string('x', 400) + "\r\n");
        Assert.True(EgressCacheRecordCodec.RecordLength(large.ArtSize) > EgressCacheRecordCodec.RecordLength(small.ArtSize));
        await using var engine = OpenEnabled(
            dir,
            capacity: EgressCacheRecordCodec.RecordLength(small.ArtSize),
            usage: 100,
            free: 0);
        await PublishAsync(engine, small);
        await PublishAsync(engine, large);
        await WarmAsync(engine, small.ArtId);
        var evictions = engine.EgressCacheEvictionCount;
        var occupancy = engine.Egress.CacheBytes;

        Assert.True(engine.TryRead(large.ArtId, out var read));
        Assert.True(engine.TryRead(large.ArtId, out _));
        Assert.True(read.ArtData.Span.SequenceEqual(large.ArtData.Span));
        await engine.Egress.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(evictions, engine.EgressCacheEvictionCount);
        Assert.Equal(occupancy, engine.Egress.CacheBytes);
        Assert.Equal(1, engine.EgressCacheEntryCount);
        Assert.True(engine.Index.TryGet(large.ArtId, out var largeMeta));
        Assert.Equal(ArticleStorageState.Present, largeMeta.State);
        Assert.True(engine.Index.TryGet(small.ArtId, out var smallMeta));
        Assert.Equal(ArticleStorageState.Present, smallMeta.State);
    }

    [Fact]
    public async Task Unexpected_fill_exception_is_logged_and_the_read_still_succeeds()
    {
        using var dir = TempDir.Create();
        var logs = new CaptureLogger();
        await using var engine = FileArticleStorageEngine.Open(
            dir.Options,
            volumeProbe: null,
            capacityReader: null,
            controlCapacityReader: null,
            logger: logs,
            timeProvider: null,
            articleCache: new ArticleMemoryCache(0),
            maxConcurrentPhysicalReads: null,
            egressCache: Start(dir.Options.ControlDir, new FakeSpace(), capacity: 1_000_000));
        var record = CreateRecord("<egress-fault@seg.test>", "secret-body-phase42\r\n");
        await PublishAsync(engine, record);
        engine.Egress.TestThrowOnPublish = () => throw new InvalidOperationException("egress-fill-fault");

        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        await engine.Egress.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, engine.EgressCachePopulateCount);
        Assert.Contains(logs.Events, entry => entry.EventId == 3442 && entry.Exception is InvalidOperationException);
        Assert.DoesNotContain(logs.Events, entry => entry.Message.Contains("secret-body-phase42", StringComparison.Ordinal));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);

        engine.Egress.TestThrowOnPublish = null;
        Assert.True(engine.TryRead(record.ArtId, out var again));
        Assert.True(again.ArtData.Span.SequenceEqual(record.ArtData.Span));
        await engine.Egress.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, engine.EgressCachePopulateCount);
    }

    [Fact]
    public async Task Repeated_fill_exceptions_do_not_retry_without_bound()
    {
        using var dir = TempDir.Create();
        var cache = ArticleEgressCache.Open(Start(dir.Options.ControlDir, new FakeSpace(), capacity: 1_000_000, usage: 100, free: 0));
        var attempts = 0;
        cache.TestThrowOnPublish = () =>
        {
            attempts++;
            throw new InvalidOperationException("egress-fill-fault");
        };
        try
        {
            QueueFill(cache, "<fault-a@seg.test>", 64);
            QueueFill(cache, "<fault-b@seg.test>", 64);
            QueueFill(cache, "<fault-c@seg.test>", 64);
            await cache.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, attempts);
            Assert.Equal(0, cache.PopulateCount);

            cache.TestThrowOnPublish = null;
            QueueFill(cache, "<fault-d@seg.test>", 64);
            await cache.DrainFillsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, attempts);
            Assert.Equal(1, cache.PopulateCount);
        }
        finally
        {
            cache.Dispose();
        }
    }

    [Fact]
    public void Shutdown_does_not_log_a_fill_fault()
    {
        using var dir = TempDir.Create();
        var logs = new CaptureLogger();
        var cache = ArticleEgressCache.Open(Start(dir.Options.ControlDir, new FakeSpace(), capacity: 50_000), logs);
        cache.Dispose();
        Assert.DoesNotContain(logs.Events, entry => entry.EventId is 3441 or 3442);
    }

    private static void QueueFill(ArticleEgressCache cache, string messageId, int artSize)
    {
        var payload = new byte[artSize];
        cache.ConsiderFill(
            ArticleId.FromMessageId(Encoding.ASCII.GetBytes(messageId)),
            sequence: 1,
            XxHash3.HashToUInt64(payload),
            artSize,
            payload,
            waiterCount: 1);
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<CapturedLog> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Events.Add(new CapturedLog(eventId.Id, formatter(state, exception), exception));
        }
    }

    private readonly record struct CapturedLog(int EventId, string Message, Exception? Exception);
}
