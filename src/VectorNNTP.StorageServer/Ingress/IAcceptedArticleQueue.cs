namespace VectorNNTP.StorageServer.Ingress;

/// <summary>
/// Result of waiting for one queued article.
/// </summary>
/// <param name="HasArticle"><see langword="false"/> when the queue is completed and empty.</param>
/// <param name="Article">The dequeued article when <paramref name="HasArticle"/> is true.</param>
public readonly record struct AcceptedArticleDelivery(bool HasArticle, AcceptedArticle Article);

/// <summary>
/// In-memory handoff of accepted VATP articles. Multiple sessions may enqueue,
/// and multiple consumers may dequeue. The queue does not persist articles.
/// </summary>
public interface IAcceptedArticleQueue
{
    /// <summary>
    /// Enqueues <paramref name="article"/> if the queue is still accepting.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the article is visible to consumers.
    /// <see langword="false"/> after <see cref="Complete"/>; the article is not queued.
    /// </returns>
    bool TryEnqueue(AcceptedArticle article);

    /// <summary>
    /// Waits for the next article. <see cref="AcceptedArticleDelivery.HasArticle"/> is
    /// <see langword="false"/> when the queue is completed and empty. Cancellation throws
    /// and does not remove an article.
    /// </summary>
    ValueTask<AcceptedArticleDelivery> DequeueAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops further enqueues and unblocks dequeue waiters once the queue is empty.
    /// Articles already queued remain available. Repeated calls are safe.
    /// </summary>
    void Complete();
}
