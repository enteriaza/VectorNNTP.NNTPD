using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Ingress;
using VectorNNTP.StorageServer.Storage.ArticleIndex;
using VectorNNTP.StorageServer.Storage.SegmentPools;
using VectorNNTP.StorageServer.Storage.SpoolPersistence;
using VectorNNTP.StorageServer.Storage.SpoolShards;

namespace VectorNNTP.StorageServer.Tests.Storage;

public sealed class ArticleIndexPersistenceTests
{
    [Fact]
    public async Task A_stored_article_is_indexed_after_the_file_exists_with_one_timestamp_and_the_spool_root()
    {
        var spool = ArticleIndexTestSupport.NewDirectory();
        var database = ArticleIndexTestSupport.NewDirectory();
        var catalog = Catalog(spool);
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(2026, 10, 13, 7, 0, 0, TimeSpan.Zero));
        var expected = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var monitor = Monitor(catalog, spool);
        await monitor.StartAsync(CancellationToken.None);
        var index = await ArticleIndexTestSupport.StartAsync(database, catalog);
        try
        {
            await WaitForSpaceAsync(monitor, spool);
            var processor = Processor(catalog, monitor, index, clock);
            var article = Build("<indexed@example.test>");
            Assert.False(index.Contains(article.ArtId));
            Assert.Equal(AcceptedArticleProcessResult.Completed, await processor.ProcessAsync(article, CancellationToken.None));
            var path = SpoolArticlePath.CombineArticle(spool, article.ArtId);
            Assert.True(File.Exists(path));
            Assert.True(index.Contains(article.ArtId));
            Assert.True(index.TryGetIndexedRecord(article.ArtId, out var record));
            Assert.Equal(spool, record.SpoolDirectory);
            Assert.Equal(expected, record.ReceivedAtUnixMilliseconds);
            Assert.Equal(expected, record.LastAccessedAtUnixMilliseconds);
            Assert.True(index.TryGetRetrievableArticle(article.ArtId, out var retrieved));
            Assert.Equal(path, retrieved);

            Assert.Equal(AcceptedArticleProcessResult.Completed, await processor.ProcessAsync(article, CancellationToken.None));
            Assert.True(index.TryGetIndexedRecord(article.ArtId, out var duplicate));
            Assert.Equal(expected, duplicate.ReceivedAtUnixMilliseconds);
            Assert.Equal(spool, duplicate.SpoolDirectory);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            await ArticleIndexTestSupport.StopAsync(index);
            ArticleIndexTestSupport.Delete(spool);
            ArticleIndexTestSupport.Delete(database);
        }
    }

    [Fact]
    public async Task A_rejected_publish_does_not_index_and_a_failed_commit_is_repaired_from_the_file_name()
    {
        var spool = ArticleIndexTestSupport.NewDirectory();
        var database = ArticleIndexTestSupport.NewDirectory();
        var catalog = Catalog(spool);
        var probe = new StaticFreeSpaceProbe();
        var monitor = Monitor(catalog, probe);
        await monitor.StartAsync(CancellationToken.None);
        var failing = await ArticleIndexTestSupport.StartAsync(database, catalog, new ArticleIndexSettings
        {
            BatchSize = 1,
            FlushIntervalMilliseconds = 10,
            QueueCapacity = 8,
            SummaryIntervalMilliseconds = 60_000,
            BeforeCommit = () => throw new InvalidOperationException("index unavailable"),
        });
        ArticleIndexService? repaired = null;
        try
        {
            probe.Set(spool, total: 100_000_000_000, available: 80_000_000_000);
            await WaitForSpaceAsync(monitor, spool);
            var processor = Processor(catalog, monitor, failing, TimeProvider.System);
            var article = Build("<repair@example.test>");
            Assert.False(failing.Contains(article.ArtId));
            Assert.Equal(AcceptedArticleProcessResult.Failed, await processor.ProcessAsync(article, CancellationToken.None));
            Assert.True(File.Exists(SpoolArticlePath.CombineArticle(spool, article.ArtId)));
            Assert.False(failing.Contains(article.ArtId));
            await ArticleIndexTestSupport.StopAsync(failing);

            repaired = await ArticleIndexTestSupport.StartAsync(database, catalog);
            var report = await repaired.Reconciliation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(report.MissingQueued >= 1);
            Assert.True(repaired.Contains(article.ArtId));
            Assert.True(repaired.TryGetRetrievableArticle(article.ArtId, out _));
            Assert.True(repaired.TryGetIndexedRecord(article.ArtId, out var record));
            Assert.Equal(spool, record.SpoolDirectory);
            await ArticleIndexTestSupport.StopAsync(repaired);
            repaired = null;
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            if (failing.Execution is not null)
            {
                await failing.StopAsync(CancellationToken.None);
            }

            if (repaired is not null)
            {
                await repaired.StopAsync(CancellationToken.None);
            }

            ArticleIndexTestSupport.Delete(spool);
            ArticleIndexTestSupport.Delete(database);
        }
    }

    private static SegmentPoolCatalog Catalog(string spool)
    {
        var catalog = new SegmentPoolCatalog();
        catalog.Publish([new SegmentPoolDevice("block:test", [spool])]);
        return catalog;
    }

    private static BlockDeviceMonitorService Monitor(SegmentPoolCatalog catalog, StaticFreeSpaceProbe probe) =>
        new(
            catalog,
            new RisingCounters(),
            NullLogger<BlockDeviceMonitorService>.Instance,
            TimeSpan.FromHours(1),
            TimeProvider.System,
            probe);

    private static BlockDeviceMonitorService Monitor(SegmentPoolCatalog catalog, string spool)
    {
        var probe = new StaticFreeSpaceProbe();
        probe.Set(spool, total: 100_000_000_000, available: 80_000_000_000);
        return Monitor(catalog, probe);
    }

    private static SpoolArticlePersistenceProcessor Processor(
        SegmentPoolCatalog catalog,
        BlockDeviceMonitorService monitor,
        IArticleIndex index,
        TimeProvider time)
    {
        return new SpoolArticlePersistenceProcessor(
            "cache01.usenet.ninja",
            catalog,
            monitor,
            new SpoolWriteGate(),
            new SpoolArticleIdentityGate(),
            new SpoolHealth(time),
            new SpoolPressure(),
            new SpoolArticlePublisher(new FileSpoolArticleFiles()),
            new SpoolPersistenceCounters(),
            new StorageServerOptions
            {
                SegmentPools = new SegmentPoolsOptions { CriticalPercent = 85, HighPercent = 79, LowPercent = 75 },
                SpoolPersistence = new SpoolPersistenceOptions(),
            },
            NullLogger.Instance,
            time,
            index);
    }

    private static AcceptedArticle Build(string messageId)
    {
        var parser = new NntpArticleParser("nntpd01.usenet.ninja");
        var text = "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: "
            + messageId
            + "\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: vatp\r\n\r\nbody-line\r\n";
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(text));
        Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
        return new AcceptedArticle(created.Record.ArtId, created.Record.ArtHash, created.Record.ArtSize, created.Record.ArtData);
    }

    private static async Task WaitForSpaceAsync(BlockDeviceMonitorService monitor, string spool)
    {
        InvokeSample(monitor);
        var deadline = TimeProvider.System.GetTimestamp();
        while (!monitor.Observations.Space.ContainsKey(spool))
        {
            if (TimeProvider.System.GetElapsedTime(deadline) > TimeSpan.FromSeconds(2))
            {
                throw new TimeoutException("spool space was not sampled");
            }

            await Task.Delay(10);
        }
    }

    private static void InvokeSample(BlockDeviceMonitorService monitor) =>
        monitor.GetType().GetMethod("Sample", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(monitor, null);

    private sealed class StaticFreeSpaceProbe : ISpoolFreeSpaceProbe
    {
        private readonly Dictionary<string, (long Total, long Available)> _space = new(StringComparer.Ordinal);

        public void Set(string path, long total, long available) => _space[path] = (total, available);

        public bool TryRead(string spoolRoot, out long totalBytes, out long availableBytes)
        {
            if (!_space.TryGetValue(spoolRoot, out var sample))
            {
                totalBytes = 0;
                availableBytes = 0;
                return false;
            }

            totalBytes = sample.Total;
            availableBytes = sample.Available;
            return true;
        }
    }

    private sealed class RisingCounters : IBlockDeviceCounterSource
    {
        public bool TryRead(string deviceId, out BlockDeviceCounters counters, out string? error)
        {
            counters = new BlockDeviceCounters(1000, 1000, 1000, 1000, 1000, 1000, LatencyAvailable: true);
            error = null;
            return true;
        }
    }
}
