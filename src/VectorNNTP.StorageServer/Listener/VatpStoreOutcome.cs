namespace VectorNNTP.StorageServer.Listener;

/// <summary>VATP RESULT outcome byte written after an article is queued.</summary>
public static class VatpStoreOutcome
{
    /// <summary>
    /// The Path-rewritten article was accepted into the in-memory queue.
    /// This is the historical <c>ArticleAcceptOutcome.Accepted</c> value (<c>1</c>).
    /// </summary>
    public const byte Accepted = 1;
}
