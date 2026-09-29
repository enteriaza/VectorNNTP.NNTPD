using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.ArticleIngestion;

/// <summary>OverviewDB intermediate work-queue and publisher-pool contracts.</summary>
public sealed class OverviewDbPublisherStageTests
{
    [Fact]
    public async Task WorkQueue_BackpressuresWhenByteBudgetExhausted()
    {
        var queue = new OverviewDbWorkQueue(memoryLimitBytes: 10);
        var first = new OverviewDbWorkItem(new byte[8], "<a@test>");
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(first, CancellationToken.None));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var second = new OverviewDbWorkItem(new byte[8], "<b@test>");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.EnqueueAsync(second, cts.Token).AsTask());
        Assert.True(queue.WaitingProducerCount >= 0);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task PublisherPool_PublishesAndAwaitsConfirm_WithoutArticleQueue()
    {
        var work = new OverviewDbWorkQueue(memoryLimitBytes: 1024 * 1024);
        var publisher = new RecordingOverviewDbHandoffPublisher();
        var options = new ArticleIngestionOptions
        {
            OverviewDbMinPublisherWorkers = 1,
            OverviewDbMaxPublisherWorkers = 1,
            MaxPublishConcurrency = 1,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new OverviewDbPublisherPool(
            work,
            publisher,
            options,
            NullLogger.Instance);
        var run = pool.RunAsync(cts.Token);

        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await work.EnqueueAsync(new OverviewDbWorkItem("abc"u8.ToArray(), "<m@test>"), CancellationToken.None));

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (publisher.Payloads.Count < 1)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, wait.Token);
        }

        Assert.Equal(1, publisher.AttemptCount);
        Assert.Single(publisher.Payloads);

        work.Complete();
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PublisherPool_RequeuesWorkItemOnPublishFailure()
    {
        var work = new OverviewDbWorkQueue(memoryLimitBytes: 1024 * 1024);
        var publisher = new RecordingOverviewDbHandoffPublisher { RemainingFailures = 1 };
        var options = new ArticleIngestionOptions
        {
            OverviewDbMinPublisherWorkers = 1,
            OverviewDbMaxPublisherWorkers = 1,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new OverviewDbPublisherPool(work, publisher, options, NullLogger.Instance);
        var run = pool.RunAsync(cts.Token);

        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await work.EnqueueAsync(new OverviewDbWorkItem("retry"u8.ToArray(), "<r@test>"), CancellationToken.None));

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (publisher.Payloads.Count < 1)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, wait.Token);
        }

        Assert.Equal(2, publisher.AttemptCount);
        Assert.Single(publisher.Payloads);

        work.Complete();
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PublisherPool_ScalesIndependentlyOfArticleWorkerBounds()
    {
        var work = new OverviewDbWorkQueue(memoryLimitBytes: 1024);
        var options = new ArticleIngestionOptions
        {
            MinWorkers = 1,
            MaxWorkers = 1,
            OverviewDbMinPublisherWorkers = 2,
            OverviewDbMaxPublisherWorkers = 4,
            ScaleUpPressureThreshold = 0.5,
            ScaleDownPressureThreshold = 0.1,
            ScaleUpConsecutiveIntervals = 1,
            ScaleDownConsecutiveIntervals = 1,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new OverviewDbPublisherPool(
            work,
            new RecordingOverviewDbHandoffPublisher(),
            options,
            NullLogger.Instance);
        var run = pool.RunAsync(cts.Token);

        Assert.Equal(2, pool.WorkerCount);
        pool.ApplyScaleDecisionForTests(new IngestionPressureSnapshot(1, 1, 1, 1, 1), cts.Token);
        Assert.Equal(3, pool.WorkerCount);

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PublisherPool_DrainsQueuedWorkOnComplete_BeforeExit()
    {
        var work = new OverviewDbWorkQueue(memoryLimitBytes: 1024 * 1024);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new GatedOverviewDbHandoffPublisher(gate);
        var options = new ArticleIngestionOptions
        {
            OverviewDbMinPublisherWorkers = 1,
            OverviewDbMaxPublisherWorkers = 1,
            ScaleIntervalSeconds = 3600,
        };

        using var cts = new CancellationTokenSource();
        var pool = new OverviewDbPublisherPool(work, publisher, options, NullLogger.Instance);
        var run = pool.RunAsync(cts.Token);

        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await work.EnqueueAsync(new OverviewDbWorkItem("drain-a"u8.ToArray(), "<a@test>"), CancellationToken.None));
        Assert.Equal(
            ArticleEnqueueResult.Accepted,
            await work.EnqueueAsync(new OverviewDbWorkItem("drain-b"u8.ToArray(), "<b@test>"), CancellationToken.None));

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (publisher.Started < 1)
        {
            wait.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, wait.Token);
        }

        work.Complete();
        gate.TrySetResult();
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, publisher.ConfirmCount);
        Assert.Equal(0, work.Count);
    }

    private sealed class GatedOverviewDbHandoffPublisher(TaskCompletionSource gate) : IOverviewDbHandoffPublisher
    {
        private int _started;
        private int _confirms;

        public int Started => Volatile.Read(ref _started);

        public int ConfirmCount => Volatile.Read(ref _confirms);

        public int OutstandingCount => 0;

        public async Task PublishAsync(OverviewDbWorkItem item, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            Interlocked.Increment(ref _started);
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _confirms);
        }

        public bool TryDequeuePublishFailure(out OverviewDbWorkItem item)
        {
            item = null!;
            return false;
        }

        public void AbandonOutstanding()
        {
        }
    }
}
