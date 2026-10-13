using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Ingress;

namespace VectorNNTP.StorageServer.Tests.Ingress;

public sealed class AcceptedArticleQueueTests
{
    [Fact]
    public async Task Concurrent_producers_and_consumers_deliver_each_article_once()
    {
        var queue = new AcceptedArticleQueue();
        const int count = 200;
        var produced = Enumerable.Range(0, count).Select(Create).ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var producers = produced.Select(async article =>
        {
            await gate.Task;
            Assert.True(queue.TryEnqueue(article));
        }).ToArray();

        var seen = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        var consumers = Enumerable.Range(0, 4).Select(async _ =>
        {
            await gate.Task;
            while (seen.Count < count)
            {
                var delivery = await queue.DequeueAsync(CancellationToken.None);
                if (!delivery.HasArticle)
                {
                    return;
                }

                Assert.True(seen.TryAdd(delivery.Article.ArtSize, 0));
            }
        }).ToArray();

        gate.TrySetResult();
        await Task.WhenAll(producers);
        queue.Complete();
        await Task.WhenAll(consumers);
        Assert.Equal(count, seen.Count);
    }

    [Fact]
    public async Task Counters_track_enqueue_and_dequeue_without_copying_the_buffer()
    {
        var bytes = new byte[] { 9, 8, 7 };
        var queue = new AcceptedArticleQueue();
        Assert.Equal(0, queue.EnqueuedCount);
        Assert.Equal(0, queue.DequeuedCount);
        Assert.Equal(0, queue.QueuedBytes);

        Assert.True(queue.TryEnqueue(new AcceptedArticle(default, 1, bytes.Length, bytes)));
        Assert.Equal(1, queue.EnqueuedCount);
        Assert.Equal(0, queue.DequeuedCount);
        Assert.Equal(bytes.Length, queue.QueuedBytes);

        var delivery = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(delivery.HasArticle);
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(delivery.Article.ArtData, out var segment));
        Assert.Same(bytes, segment.Array);
        Assert.Equal(1, queue.EnqueuedCount);
        Assert.Equal(1, queue.DequeuedCount);
        Assert.Equal(0, queue.QueuedBytes);

        queue.Complete();
        Assert.False(queue.TryEnqueue(new AcceptedArticle(default, 1, bytes.Length, bytes)));
        Assert.Equal(1, queue.EnqueuedCount);
        var empty = await queue.DequeueAsync(CancellationToken.None);
        Assert.False(empty.HasArticle);
        Assert.Equal(1, queue.DequeuedCount);
    }

    [Fact]
    public async Task Dequeue_returns_the_same_buffer_the_producer_enqueued()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var queue = new AcceptedArticleQueue();
        Assert.True(queue.TryEnqueue(new AcceptedArticle(default, 9, bytes.Length, bytes)));
        var delivery = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(delivery.HasArticle);
        Assert.True(delivery.Article.ArtData.Span.SequenceEqual(bytes));
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(delivery.Article.ArtData, out var segment));
        Assert.Same(bytes, segment.Array);
    }

    [Fact]
    public async Task Complete_unblocks_a_waiting_consumer_and_rejects_later_enqueue()
    {
        var queue = new AcceptedArticleQueue();
        var waiting = queue.DequeueAsync(CancellationToken.None);
        queue.Complete();
        var delivery = await waiting;
        Assert.False(delivery.HasArticle);
        Assert.False(queue.TryEnqueue(Create(1)));
    }

    [Fact]
    public async Task Cancelling_a_dequeue_does_not_lose_a_later_article()
    {
        var queue = new AcceptedArticleQueue();
        using var cts = new CancellationTokenSource();
        var waiting = queue.DequeueAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        var article = Create(7);
        Assert.True(queue.TryEnqueue(article));
        var delivery = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(delivery.HasArticle);
        Assert.Equal(article.ArtSize, delivery.Article.ArtSize);
        Assert.Equal(article.ArtHash, delivery.Article.ArtHash);
    }

    [Fact]
    public async Task Byte_budget_rejects_an_enqueue_until_a_dequeue_frees_space()
    {
        var queue = new AcceptedArticleQueue(10, TimeProvider.System);
        var first = new AcceptedArticle(default, 1, 6, new byte[6]);
        var second = new AcceptedArticle(default, 1, 6, new byte[6]);
        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(second));
        Assert.Equal(6, queue.QueuedBytes);
        Assert.Equal(1, queue.QueuedCount);

        var delivery = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(delivery.HasArticle);
        Assert.True(queue.TryEnqueue(second));
        Assert.Equal(6, queue.QueuedBytes);
    }

    [Fact]
    public void TryDequeue_releases_the_byte_budget_and_does_not_wait()
    {
        var queue = new AcceptedArticleQueue(10, TimeProvider.System);
        var first = new AcceptedArticle(default, 1, 6, new byte[6]);
        var second = new AcceptedArticle(default, 1, 6, new byte[6]);
        Assert.False(queue.TryDequeue(out _));
        Assert.Equal(0, queue.DequeuedCount);

        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(second));
        Assert.True(queue.TryDequeue(out var delivery));
        Assert.True(delivery.HasArticle);
        Assert.Equal(6, delivery.Article.ArtSize);
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(1, queue.DequeuedCount);
        Assert.True(queue.TryEnqueue(second));
        Assert.Equal(6, queue.QueuedBytes);

        queue.Complete();
        Assert.True(queue.TryDequeue(out var drained));
        Assert.True(drained.HasArticle);
        Assert.False(queue.TryDequeue(out _));
        Assert.Equal(0, queue.QueuedBytes);
    }

    [Fact]
    public async Task TryDequeue_and_DequeueAsync_do_not_lose_articles_under_concurrency()
    {
        var queue = new AcceptedArticleQueue();
        const int count = 500;
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        using var started = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumers = Enumerable.Range(0, 4).Select(async index =>
        {
            while (seen.Count < count && !started.IsCancellationRequested)
            {
                if ((index & 1) == 0 && queue.TryDequeue(out var ready))
                {
                    Assert.True(seen.TryAdd(ready.Article.ArtSize, 0));
                    continue;
                }

                try
                {
                    var delivery = await queue.DequeueAsync(started.Token);
                    if (delivery.HasArticle)
                    {
                        Assert.True(seen.TryAdd(delivery.Article.ArtSize, 0));
                    }
                }
                catch (OperationCanceledException) when (started.IsCancellationRequested)
                {
                    return;
                }
            }
        }).ToArray();

        for (var i = 0; i < count; i++)
        {
            Assert.True(queue.TryEnqueue(Create(i)));
        }

        queue.Complete();
        await Task.WhenAll(consumers);
        Assert.Equal(count, seen.Count);
        Assert.Equal(0, queue.QueuedBytes);
        Assert.Equal(count, queue.DequeuedCount);
    }

    private static AcceptedArticle Create(int size)
    {
        var bytes = new byte[size + 1];
        bytes[0] = (byte)(size % 251);
        return new AcceptedArticle(default, (ulong)size, bytes.Length, bytes);
    }
}
