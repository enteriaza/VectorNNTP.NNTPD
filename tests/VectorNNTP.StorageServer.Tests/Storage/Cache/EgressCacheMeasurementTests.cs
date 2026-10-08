using System.Diagnostics;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Cache.Egress;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using Xunit.Abstractions;

namespace VectorNNTP.StorageServer.Tests.Storage.Cache;

/// <summary>
/// Focused timings for the disposable egress cache. Not a production capacity study.
/// </summary>
public sealed class EgressCacheMeasurementTests
{
    private readonly ITestOutputHelper _output;

    public EgressCacheMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Focused_timings()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("VECTORNNTP_EGRESS_MEASURE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        using var dir = TempDir.Create();
        var space = new FixedSpace();
        var cold = await TimeReadsAsync(dir.Options, egress: null, readsPerArticle: 1, articles: 30, prefix: "cold");
        var cached = await TimeCachedAsync(dir.Separate("hot"), space);
        var acceptOff = await TimeAcceptsAsync(dir.Separate("journal-off"), egress: false, space);
        var acceptOn = await TimeAcceptsAsync(dir.Separate("journal-on"), egress: true, space);
        _output.WriteLine($"volume={Path.GetPathRoot(Path.GetFullPath(dir.Options.ControlDir))}");
        _output.WriteLine($"cold_reads={cold.Reads} cold_ms={cold.Elapsed.TotalMilliseconds:F3} cold_us={cold.Micros:F1}");
        _output.WriteLine($"hit_reads={cached.Hits} hit_ms={cached.HitElapsed.TotalMilliseconds:F3} hit_us={cached.HitMicros:F1}");
        _output.WriteLine($"populate={cached.Populated} populate_ms={cached.PopulateElapsed.TotalMilliseconds:F3} hit_rate={cached.HitRate:P1}");
        _output.WriteLine($"entries={cached.Entries} cache_bytes={cached.CacheBytes} alloc_bytes_per_entry={cached.AllocPerEntry}");
        _output.WriteLine($"evictions={cached.Evictions} evict_ms={cached.EvictElapsed.TotalMilliseconds:F3}");
        _output.WriteLine($"queue_depth_cap={ArticleEgressCache.DefaultFillQueueDepth} queue_byte_cap={ArticleEgressCache.DefaultFillQueueMaxBytes} max_depth={cached.MaxDepth}");
        _output.WriteLine($"free_before={cached.FreeBefore} free_after={cached.FreeAfter} delta={cached.FreeBefore - cached.FreeAfter}");
        _output.WriteLine($"accept_off_ms={acceptOff.TotalMilliseconds:F3} accept_during_fill_ms={acceptOn.TotalMilliseconds:F3}");
    }

    private static async Task<ReadSample> TimeReadsAsync(
        ArticleStorageRuntimeOptions options,
        EgressCacheStart? egress,
        int readsPerArticle,
        int articles,
        string prefix)
    {
        await using var engine = Open(options, egress);
        var records = Create(articles, prefix);
        foreach (var record in records)
        {
            await PublishAsync(engine, record);
        }

        var watch = Stopwatch.StartNew();
        var reads = 0;
        foreach (var record in records)
        {
            for (var i = 0; i < readsPerArticle; i++)
            {
                Assert.True(engine.TryRead(record.ArtId, out _));
                reads++;
            }
        }

        watch.Stop();
        return new ReadSample(reads, watch.Elapsed);
    }

    private static async Task<CachedSample> TimeCachedAsync(ArticleStorageRuntimeOptions options, FixedSpace space)
    {
        var freeBefore = Free(options.ControlDir);
        await using var engine = Open(options, Start(options.ControlDir, space, 32L * 1024 * 1024));
        var records = Create(30, "hot");
        foreach (var record in records)
        {
            await PublishAsync(engine, record);
        }

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var maxDepth = 0;
        var populate = Stopwatch.StartNew();
        foreach (var record in records)
        {
            Assert.True(engine.TryRead(record.ArtId, out _));
            Assert.True(engine.TryRead(record.ArtId, out _));
            maxDepth = Math.Max(maxDepth, engine.Egress.FillQueueDepth);
        }

        await engine.Egress.DrainFillsAsync();
        populate.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var hits = 300;
        var hitWatch = Stopwatch.StartNew();
        for (var i = 0; i < hits; i++)
        {
            Assert.True(engine.TryRead(records[i % records.Length].ArtId, out _));
        }

        hitWatch.Stop();
        var evict = await TimeEvictionAsync(options, space);
        var attempts = engine.EgressCacheHitCount + engine.EgressCacheMissCount;
        return new CachedSample(
            hits,
            hitWatch.Elapsed,
            engine.EgressCachePopulateCount,
            populate.Elapsed,
            attempts == 0 ? 0 : (double)engine.EgressCacheHitCount / attempts,
            engine.EgressCacheEntryCount,
            engine.EgressCacheBytes,
            engine.EgressCacheEntryCount == 0 ? 0 : allocated / engine.EgressCacheEntryCount,
            evict.Evictions,
            evict.Elapsed,
            maxDepth,
            freeBefore,
            Free(options.ControlDir));
    }

    private static async Task<(long Evictions, TimeSpan Elapsed)> TimeEvictionAsync(
        ArticleStorageRuntimeOptions options,
        FixedSpace space)
    {
        var evictOptions = new ArticleStorageRuntimeOptions(
            Path.Combine(options.ControlDir, "evict"),
            Path.Combine(options.SegmentDir, "evict"),
            options.JournalSoftLimitBytes,
            options.JournalHardLimitBytes,
            options.SegmentTargetSizeBytes);
        Directory.CreateDirectory(evictOptions.ControlDir);
        Directory.CreateDirectory(evictOptions.SegmentDir);
        await using var engine = Open(evictOptions, Start(evictOptions.ControlDir, space, 8 * 1024, usage: 50, free: 5));
        var watch = Stopwatch.StartNew();
        var records = Create(40, "evict");
        foreach (var record in records)
        {
            await PublishAsync(engine, record);
            Assert.True(engine.TryRead(record.ArtId, out _));
            Assert.True(engine.TryRead(record.ArtId, out _));
            if (engine.EgressCacheEvictionCount > 0)
            {
                break;
            }
        }

        await engine.Egress.DrainFillsAsync();
        watch.Stop();
        return (engine.EgressCacheEvictionCount, watch.Elapsed);
    }

    private static async Task<TimeSpan> TimeAcceptsAsync(
        ArticleStorageRuntimeOptions options,
        bool egress,
        FixedSpace space)
    {
        await using var engine = Open(options, egress ? Start(options.ControlDir, space, 32L * 1024 * 1024) : null);
        var warm = Create(8, egress ? "warm-on" : "warm-off");
        foreach (var record in warm)
        {
            await PublishAsync(engine, record);
        }

        using var stop = new CancellationTokenSource();
        var filler = Task.CompletedTask;
        if (egress)
        {
            foreach (var record in warm)
            {
                Assert.True(engine.TryRead(record.ArtId, out _));
                Assert.True(engine.TryRead(record.ArtId, out _));
            }

            filler = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    foreach (var record in warm)
                    {
                        _ = engine.TryRead(record.ArtId, out _);
                    }
                }
            });
        }

        var records = Create(20, egress ? "acc-on" : "acc-off");
        var watch = Stopwatch.StartNew();
        foreach (var record in records)
        {
            await PublishAsync(engine, record);
            if (egress)
            {
                Assert.True(engine.TryRead(record.ArtId, out _));
                Assert.True(engine.TryRead(record.ArtId, out _));
            }
        }

        if (egress)
        {
            await engine.Egress.DrainFillsAsync();
        }

        watch.Stop();
        await stop.CancelAsync();
        await filler;
        return watch.Elapsed;
    }

    private static FileArticleStorageEngine Open(ArticleStorageRuntimeOptions options, EgressCacheStart? egress) =>
        FileArticleStorageEngine.Open(
            options,
            volumeProbe: null,
            articleCache: new ArticleMemoryCache(0),
            egressCache: egress);

    private static EgressCacheStart Start(string controlDir, FixedSpace space, long capacity, int usage = 80, int free = 5) =>
        new()
        {
            ControlDir = controlDir,
            CapacityBytes = capacity,
            ReserveBytes = 0,
            JournalHardLimitBytes = 0,
            MaximumUtilizationPercent = 100,
            MaximumUsageCapacityPercent = usage,
            FreeCapacityPercent = free,
            Space = space,
        };

    private static async Task PublishAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
    }

    private static ArticleRecord[] Create(int count, string prefix)
    {
        var records = new ArticleRecord[count];
        for (var i = 0; i < count; i++)
        {
            records[i] = CreateRecord($"<{prefix}-{i}@measure.test>", $"body-{prefix}-{i}\r\n");
        }

        return records;
    }

    private static ArticleRecord CreateRecord(string messageId, string body)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        builder.Append("Path: peer.example\r\n");
        builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        builder.Append("Newsgroups: alt.test\r\n");
        builder.Append("From: user@example.test\r\n");
        builder.Append("Subject: egress-measure\r\n");
        builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private static long Free(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
    }

    private sealed class FixedSpace : IEgressVolumeSpace
    {
        public bool TryRead(out long availableBytes, out long totalBytes)
        {
            availableBytes = 1L << 40;
            totalBytes = 1L << 40;
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-egress-measure-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var segment = Path.Combine(root, "segment");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(segment);
            return new TempDir(
                root,
                new ArticleStorageRuntimeOptions(
                    control,
                    segment,
                    ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    ArticleStorageOptions.DefaultJournalHardLimitBytes,
                    ArticleStorageOptions.DefaultSegmentTargetSizeBytes));
        }

        public ArticleStorageRuntimeOptions Separate(string name)
        {
            var control = Path.Combine(Root, name, "control");
            var segment = Path.Combine(Root, name, "segment");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(segment);
            return Options with { ControlDir = control, SegmentDir = segment };
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

    private readonly record struct ReadSample(int Reads, TimeSpan Elapsed)
    {
        public double Micros => Elapsed.TotalMicroseconds / Reads;
    }

    private readonly record struct CachedSample(
        int Hits,
        TimeSpan HitElapsed,
        long Populated,
        TimeSpan PopulateElapsed,
        double HitRate,
        int Entries,
        long CacheBytes,
        long AllocPerEntry,
        long Evictions,
        TimeSpan EvictElapsed,
        int MaxDepth,
        long FreeBefore,
        long FreeAfter)
    {
        public double HitMicros => HitElapsed.TotalMicroseconds / Hits;
    }
}
