namespace VectorNNTP.NNTPD.RabbitMq.ArticleWork;

/// <summary>
/// Reusable article-work RPC entry point for ARTICLE, HEAD, BODY, and STAT message-id lookups.
/// </summary>
/// <remarks>
/// This boundary converts command-line Message-ID bytes into the JSON application payload.
/// It does not retrieve article bytes or write NNTP success responses.
/// </remarks>
public interface IArticleWorkRpcClient
{
    /// <summary>
    /// Publishes the storage request at T+0, starts the 500ms provider-fan-out grace
    /// concurrently, and returns as soon as the first valid Success arrives or the
    /// 5-second aggregate deadline elapses.
    /// </summary>
    /// <param name="messageId">Command-line Message-ID bytes, including angle brackets.</param>
    /// <param name="cancellationToken">Caller or session cancellation.</param>
    /// <returns>The structured RPC result. Never includes article content.</returns>
    Task<ArticleWorkRpcResult> LookupByMessageIdAsync(
        ReadOnlyMemory<byte> messageId,
        CancellationToken cancellationToken);
}
