using System.Net;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Networking.Proxy;

namespace VectorNNTP.NNTPD.Tests.ArticleIngestion;

public sealed class ArticleIngestionQueueTests
{
    [Fact]
    public void DefaultMemoryLimit_IsOneGibibyte()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions());
        Assert.Equal(NntpdOptions.DefaultTransitQueueMemoryLimit, queue.MemoryLimitBytes);
        Assert.Equal(1_073_741_824L, queue.MemoryLimitBytes);
    }

    [Fact]
    public void ConfiguredMemoryLimit_IsHonoured()
    {
        var options = new NntpdOptions
        {
            TransitQueueMemoryLimit = 4096,
            ArticleIngestion = new ArticleIngestionOptions(),
        };
        var queue = new ArticleIngestionQueue(Options.Create(options));
        Assert.Equal(4096, queue.MemoryLimitBytes);
    }

    [Fact]
    public async Task ArticleSmallerThanBudget_IsAdmitted()
    {
        var queue = CreateQueue(32);
        var article = Article("<small@ex.com>", 8, InboundArticleProducer.IHave);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(article, CancellationToken.None));
        Assert.Equal(8, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task MultipleArticles_ConsumeBudget()
    {
        var queue = CreateQueue(12);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(Article("<a@ex.com>", 4), CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(Article("<b@ex.com>", 4), CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(Article("<c@ex.com>", 4), CancellationToken.None));
        Assert.Equal(12, queue.QueuedBytes);
        Assert.Equal(3, queue.Count);
        Assert.Equal(12, queue.PeakQueuedBytes);
        Assert.Equal(3, queue.PeakCount);
    }

    [Fact]
    public async Task Admission_BlocksWhenBudgetExhausted_AndResumesAfterRelease()
    {
        var queue = CreateQueue(10);
        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await queue.EnqueueAsync(Article("<full@ex.com>", 10), CancellationToken.None));

        var blocked = queue.EnqueueAsync(Article("<wait@ex.com>", 1), CancellationToken.None);
        await WaitForWaitersAsync(queue, 1);

        Assert.False(blocked.IsCompleted);
        Assert.Equal(10, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);

        var first = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("<full@ex.com>", first!.MessageId);

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleEnqueueResult.Accepted, await blocked.AsTask().WaitAsync(safety.Token));
        Assert.Equal(1, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);
        Assert.Equal("<wait@ex.com>", (await queue.DequeueAsync(CancellationToken.None))!.MessageId);
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task ConcurrentProducers_CannotOversubscribeBudget()
    {
        const int size = 8;
        const long limit = 32;
        const int producers = 16;
        var queue = CreateQueue(limit);
        var results = new ArticleEnqueueResult[producers];
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task[producers];
        for (var i = 0; i < producers; i++)
        {
            var index = i;
            tasks[index] = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                results[index] = await queue
                    .EnqueueAsync(Article($"<{index}@c>", size), CancellationToken.None)
                    .ConfigureAwait(false);
            });
        }

        start.SetResult();

        await WaitUntilAsync(
            () => queue.Count == 4 && queue.DebugWaiterCount == 12,
            TimeSpan.FromSeconds(5));

        Assert.Equal(limit, queue.QueuedBytes);
        Assert.Equal(4, queue.Count);
        Assert.True(queue.QueuedBytes <= limit);
        Assert.Equal(4, tasks.Count(static t => t.IsCompleted));

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (var i = 0; i < producers; i++)
        {
            Assert.NotNull(await queue.DequeueAsync(safety.Token));
        }

        await Task.WhenAll(tasks).WaitAsync(safety.Token);
        Assert.All(results, static r => Assert.Equal(ArticleEnqueueResult.Accepted, r));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);
    }

    [Fact]
    public async Task ExactBoundaryArticle_Fits()
    {
        var queue = CreateQueue(16);
        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await queue.EnqueueAsync(Article("<edge@ex.com>", 16), CancellationToken.None));
        Assert.Equal(16, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(Article("<zero@ex.com>", 0), CancellationToken.None));
        Assert.Equal(16, queue.QueuedBytes);
    }

    [Fact]
    public async Task ArticleExceedingBudget_IsRejected()
    {
        var queue = CreateQueue(16);
        Assert.Equal(
            ArticleEnqueueResult.Rejected,
            await queue.EnqueueAsync(Article("<big@ex.com>", 17), CancellationToken.None));
        Assert.False(queue.TryEnqueue(Article("<big2@ex.com>", 17)));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);
    }

    [Fact]
    public async Task FailedEnqueueAfterReservation_ReleasesBytes()
    {
        var queue = CreateQueue(32);
        queue.CompleteChannelWithoutMarkingUnavailableForTests();
        Assert.Equal(
            ArticleEnqueueResult.Unavailable,
            await queue.EnqueueAsync(Article("<fail@ex.com>", 8), CancellationToken.None));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Cancellation_ReleasesReservation()
    {
        var queue = CreateQueue(10);
        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await queue.EnqueueAsync(Article("<held@ex.com>", 10), CancellationToken.None));

        using var cts = new CancellationTokenSource();
        var blocked = queue.EnqueueAsync(Article("<cancel@ex.com>", 1), cts.Token);
        await WaitForWaitersAsync(queue, 1);
        cts.Cancel();

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleEnqueueResult.Unavailable, await blocked.AsTask().WaitAsync(safety.Token));
        Assert.Equal(10, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);

        Assert.Equal("<held@ex.com>", (await queue.DequeueAsync(CancellationToken.None))!.MessageId);
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task ShutdownDrain_DoesNotLeakReservations()
    {
        var queue = CreateQueue(30);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(Article("<a@ex.com>", 10), CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(Article("<b@ex.com>", 10), CancellationToken.None));

        var blocked = queue.EnqueueAsync(Article("<c@ex.com>", 11), CancellationToken.None);
        await WaitForWaitersAsync(queue, 1);

        queue.Complete();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleEnqueueResult.Unavailable, await blocked.AsTask().WaitAsync(safety.Token));
        Assert.Equal(
            ArticleEnqueueResult.Unavailable,
            await queue.EnqueueAsync(Article("<d@ex.com>", 1), CancellationToken.None));

        Assert.Equal("<a@ex.com>", (await queue.DequeueAsync(CancellationToken.None))!.MessageId);
        Assert.Equal("<b@ex.com>", (await queue.DequeueAsync(CancellationToken.None))!.MessageId);
        Assert.Null(await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.DebugWaiterCount);
        Assert.False(queue.IsAccepting);
    }

    [Fact]
    public async Task QueuedBytes_ReturnToZeroAfterConsume()
    {
        var queue = CreateQueue(20);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(Article("<a@ex.com>", 6), CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(Article("<b@ex.com>", 7), CancellationToken.None));
        Assert.Equal(13, queue.QueuedBytes);
        Assert.NotNull(await queue.DequeueAsync(CancellationToken.None));
        Assert.NotNull(await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(0, queue.Count);
        Assert.Equal(13, queue.PeakQueuedBytes);
    }

    [Fact]
    public void TryProbeCapacity_IsFalseWhenBudgetIsExhausted()
    {
        var queue = CreateQueue(8);
        Assert.True(queue.TryProbeCapacity());
        Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(Article("<full@ex.com>", 8)));
        Assert.False(queue.TryProbeCapacity());
        Assert.Equal(ArticleEnqueueResult.Full, queue.TryAdmit(Article("<next@ex.com>", 1)));
        Assert.Equal(8, queue.QueuedBytes);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void TryAdmit_DoesNotWait_WhenBudgetIsExhausted()
    {
        var queue = CreateQueue(8);
        Assert.Equal(ArticleEnqueueResult.Accepted, queue.TryAdmit(Article("<held@ex.com>", 8)));
        var started = Environment.TickCount64;
        Assert.Equal(ArticleEnqueueResult.Full, queue.TryAdmit(Article("<nowait@ex.com>", 1)));
        Assert.True(Environment.TickCount64 - started < 250);
        Assert.Equal(0, queue.DebugWaiterCount);
        Assert.Equal(8, queue.QueuedBytes);
    }

    [Fact]
    public void TryAdmit_RejectedArticle_DoesNotReserve()
    {
        var queue = CreateQueue(8);
        Assert.Equal(ArticleEnqueueResult.Rejected, queue.TryAdmit(Article("<big@ex.com>", 9)));
        Assert.Equal(0, queue.QueuedBytes);
        Assert.True(queue.TryProbeCapacity());
    }

    [Fact]
    public void TryAdmit_ConcurrentProducers_CannotOversubscribe()
    {
        const int size = 8;
        const long limit = 32;
        const int producers = 16;
        var queue = CreateQueue(limit);
        var results = new ArticleEnqueueResult[producers];
        Parallel.For(0, producers, i =>
        {
            results[i] = queue.TryAdmit(Article($"<{i}@c>", size));
        });

        Assert.Equal(4, results.Count(static r => r == ArticleEnqueueResult.Accepted));
        Assert.Equal(12, results.Count(static r => r == ArticleEnqueueResult.Full));
        Assert.Equal(limit, queue.QueuedBytes);
        Assert.Equal(4, queue.Count);
        Assert.True(queue.QueuedBytes <= limit);
    }

    [Fact]
    public async Task ProducerIdentityAndOrder_RemainIntact()
    {
        var queue = CreateQueue(64);
        var first = Article("<ihave@ex.com>", 4, InboundArticleProducer.IHave);
        var second = Article("<takethis@ex.com>", 8, InboundArticleProducer.TakeThis);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(first, CancellationToken.None));
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(second, CancellationToken.None));

        var dequeuedFirst = await queue.DequeueAsync(CancellationToken.None);
        var dequeuedSecond = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("<ihave@ex.com>", dequeuedFirst!.MessageId);
        Assert.Equal(InboundArticleProducer.IHave, dequeuedFirst.Producer);
        Assert.Equal("<takethis@ex.com>", dequeuedSecond!.MessageId);
        Assert.Equal(InboundArticleProducer.TakeThis, dequeuedSecond.Producer);
        Assert.Equal(0, queue.QueuedBytes);
    }

    private static ArticleIngestionQueue CreateQueue(long memoryLimit) =>
        new(new ArticleIngestionOptions(), memoryLimit);

    private static InboundArticle Article(
        string messageId,
        int bytes,
        InboundArticleProducer producer = InboundArticleProducer.TakeThis) =>
        new(
            messageId,
            new byte[bytes],
            ConnectionClientIdentity.Direct(new IPEndPoint(IPAddress.Loopback, 119)),
            DateTimeOffset.UtcNow,
            structured: null,
            producer);

    private static Task WaitForWaitersAsync(ArticleIngestionQueue queue, int count) =>
        WaitUntilAsync(() => queue.DebugWaiterCount >= count, TimeSpan.FromSeconds(5));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(1, cts.Token);
        }
    }
}
