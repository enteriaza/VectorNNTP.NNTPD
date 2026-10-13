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
    /// <see langword="false"/> after <see cref="Complete"/> or when the queued-byte budget
    /// would be exceeded. A false result does not queue the article.
    /// </returns>
    bool TryEnqueue(AcceptedArticle article);

    /// <summary>Articles currently in the queue.</summary>
    int QueuedCount { get; }

    /// <summary>Whether <see cref="Complete"/> has been called. Queued articles can still be read.</summary>
    bool IsCompleted { get; }

    /// <summary>
    /// Age of the oldest queued article. This is the enqueue time of the item that is still
    /// at the head of the timestamp queue. An empty queue returns <see cref="TimeSpan.Zero"/>.
    /// </summary>
    /// <param name="time">Clock that understands the timestamps recorded at enqueue.</param>
    TimeSpan OldestAge(TimeProvider time);

    /// <summary>
    /// Removes one article when one is already queued.
    /// Does not wait and does not allocate a cancellation source.
    /// A false result leaves the queue unchanged. A true result releases that article's bytes
    /// from the queue budget and transfers ownership of the article to the caller.
    /// An empty completed queue returns <see langword="false"/>; it does not signal completion.
    /// Call <see cref="DequeueAsync"/> to wait or to observe completion.
    /// </summary>
    /// <param name="delivery">The article when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when the queue is empty, including after <see cref="Complete"/>.</returns>
    bool TryDequeue(out AcceptedArticleDelivery delivery);

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
