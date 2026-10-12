namespace VectorNNTP.StorageServer.Ingress;

/// <summary>
/// Processes one dequeued article. The production implementation rewrites Path and discards the result.
/// </summary>
internal interface IAcceptedArticleProcessor
{
    /// <summary>
    /// Captures the server Path identity. Called once when the worker starts, after shared configuration is published.
    /// </summary>
    void EnsureReady();

    /// <summary>
    /// Processes <paramref name="article"/> once.
    /// </summary>
    /// <param name="article">Dequeued article. The caller does not use it again.</param>
    /// <returns>
    /// <see langword="false"/> when this article cannot be processed and the worker should continue
    /// with the next article. <see langword="true"/> when processing finished, including a discard.
    /// </returns>
    bool TryProcess(AcceptedArticle article);
}
