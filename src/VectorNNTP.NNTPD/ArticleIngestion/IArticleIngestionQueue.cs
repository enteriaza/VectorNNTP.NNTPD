namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Bounded in-memory ingestion boundary between NNTP receive handlers and the spool writer.
/// </summary>
/// <remarks>
/// Enqueue completes when capacity is available (or fails when the queue is completed).
/// Callers must not perform disk I/O; persistence is owned by the background spool writer.
/// </remarks>
public interface IArticleIngestionQueue
{
    /// <summary>Gets the configured maximum number of queued articles.</summary>
    int Capacity { get; }

    /// <summary>Gets the maximum accepted article payload size in bytes.</summary>
    int MaxArticleBytes { get; }

    /// <summary>Gets the approximate number of articles currently queued.</summary>
    int Count { get; }

    /// <summary>Gets a value indicating whether the queue is still accepting articles.</summary>
    bool IsAccepting { get; }

    /// <summary>
    /// Enqueues an accepted article, waiting for capacity when the queue is full.
    /// </summary>
    /// <returns>
    /// <see cref="ArticleEnqueueResult.Accepted"/> when queued;
    /// <see cref="ArticleEnqueueResult.Unavailable"/> when the queue is completed/shutting down.
    /// </returns>
    ValueTask<ArticleEnqueueResult> EnqueueAsync(InboundArticle article, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to enqueue without waiting. Returns <see langword="false"/> when full or unavailable.
    /// </summary>
    bool TryEnqueue(InboundArticle article);

    /// <summary>
    /// Completes the writer side so no further articles are accepted.
    /// Already-queued articles remain readable until drained.
    /// </summary>
    void Complete();

    /// <summary>
    /// Reads the next queued article, or <see langword="null"/> when the queue is completed and empty.
    /// </summary>
    ValueTask<InboundArticle?> DequeueAsync(CancellationToken cancellationToken);
}
