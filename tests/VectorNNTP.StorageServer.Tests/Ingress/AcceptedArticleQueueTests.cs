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

    private static AcceptedArticle Create(int size)
    {
        var bytes = new byte[size + 1];
        bytes[0] = (byte)(size % 251);
        return new AcceptedArticle(default, (ulong)size, bytes.Length, bytes);
    }
}
