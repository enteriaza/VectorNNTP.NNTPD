using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Newsgroups;
using VectorNNTP.NNTPD.Tests.Fixtures;

namespace VectorNNTP.NNTPD.Tests.ArticleIngestion;

/// <summary>
/// Demonstrates that concurrent workers + bounded publish concurrency lift the
/// serial confirm-latency ceiling (~1 / confirmDelay articles/sec).
/// </summary>
public sealed class IngestionThroughputCeilingTests
{
    private static readonly TimeSpan ConfirmLatency = TimeSpan.FromMilliseconds(18);

    [Fact]
    public async Task ConcurrentWorkers_ExceedSerialConfirmLatencyCeiling()
    {
        const int articleCount = 32;

        var serial = await MeasureDrainAsync(
            articleCount,
            workers: 1,
            publishConcurrency: 1,
            ConfirmLatency);

        var concurrent = await MeasureDrainAsync(
            articleCount,
            workers: 8,
            publishConcurrency: 8,
            ConfirmLatency);

        // Serial model ceiling ≈ 1000/18 ≈ 55.5 arts/s. With 32 articles that is ≥ ~576 ms.
        // Eight concurrent confirms should finish near 4× serial time or better.
        Assert.True(
            concurrent.Elapsed < serial.Elapsed / 2,
            $"Expected concurrent drain ({concurrent.Elapsed.TotalMilliseconds:F0} ms, {concurrent.ArticlesPerSecond:F1}/s) " +
            $"to beat half of serial ({serial.Elapsed.TotalMilliseconds:F0} ms, {serial.ArticlesPerSecond:F1}/s).");

        Assert.True(
            concurrent.ArticlesPerSecond > 100,
            $"Expected concurrent throughput > 100 arts/s; observed {concurrent.ArticlesPerSecond:F1}/s.");

        // Serial remains near the confirm-latency ceiling (tolerance for scheduling jitter).
        Assert.InRange(serial.ArticlesPerSecond, 30, 70);
    }

    private static async Task<DrainResult> MeasureDrainAsync(
        int articleCount,
        int workers,
        int publishConcurrency,
        TimeSpan confirmLatency)
    {
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions { QueueCapacity = articleCount + 8 },
            transitQueueMemoryLimit: 64 * 1024 * 1024);
        var overview = new DelayedOverviewDbHandoffPublisher(confirmLatency);
        var done = new CountdownEvent(articleCount);
        var persister = new CountingPersister(done);

        var writer = new IncomingSpoolWriterService(
            queue,
            persister,
            Options.Create(new NntpdOptions
            {
                ArticleIngestion = new ArticleIngestionOptions
                {
                    MinWorkers = workers,
                    MaxWorkers = workers,
                    MaxPublishConcurrency = publishConcurrency,
                    ScaleIntervalSeconds = 3600,
                },
                Transit = new TransitOptions { WantTrash = true, LogTrash = true },
            }),
            NullLogger<IncomingSpoolWriterService>.Instance,
            catalogue: new StaticNewsgroupCatalogue(
                NewsgroupSnapshot.Create(
                [
                    new NewsgroupDefinition("alt.test", string.Empty, 2, 1, NewsgroupPostingStatus.Allowed),
                ])),
            overviewHandoff: overview);

        await writer.StartAsync(CancellationToken.None);
        for (var i = 0; i < articleCount; i++)
        {
            var inbound = CanonicalArticleText.CreateQueued(
                $"<throughput-{workers}-{i}@example.test>",
                InboundArticleProducer.TakeThis,
                body: "throughput-body\r\n",
                newsgroups: "alt.test");
            Assert.Equal(
                ArticleEnqueueResult.Accepted,
                await queue.EnqueueAsync(inbound, CancellationToken.None));
        }

        var sw = Stopwatch.StartNew();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "Timed out waiting for articles to persist.");
        sw.Stop();

        queue.Complete();
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(articleCount, overview.ConfirmCount);
        Assert.Equal(articleCount, persister.Count);
        return new DrainResult(sw.Elapsed, articleCount);
    }

    private readonly record struct DrainResult(TimeSpan Elapsed, int ArticleCount)
    {
        public double ArticlesPerSecond => ArticleCount / Math.Max(Elapsed.TotalSeconds, 0.001);
    }

    private sealed class DelayedOverviewDbHandoffPublisher(TimeSpan latency) : IOverviewDbHandoffPublisher
    {
        private int _confirms;

        public int ConfirmCount => Volatile.Read(ref _confirms);

        public async Task PublishConfirmedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            await Task.Delay(latency, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _confirms);
        }
    }

    private sealed class CountingPersister(CountdownEvent done) : IIncomingArticlePersister
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task PersistAsync(InboundArticle article, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            done.Signal();
            return Task.CompletedTask;
        }
    }
}
