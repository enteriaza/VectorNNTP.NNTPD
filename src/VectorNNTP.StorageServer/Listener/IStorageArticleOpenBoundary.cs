using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Listener;

/// <summary>
/// Boundary between the VATP OPEN path and article storage.
/// </summary>
/// <remarks>
/// Storage is not implemented in the skeleton. Callers must treat a rejected result
/// as the only supported outcome until the article storage engine is wired to VATP OPEN.
/// </remarks>
public interface IStorageArticleOpenBoundary
{
    /// <summary>
    /// Attempts to open a transfer for the given RequestId and ArticleId.
    /// </summary>
    /// <param name="requestId">Client OPEN RequestId.</param>
    /// <param name="articleId">Client OPEN ArticleId.</param>
    /// <returns>Open outcome; skeleton implementations always reject.</returns>
    StorageArticleOpenResult TryOpen(Guid requestId, in ArticleId articleId);
}

/// <summary>Result of an article-open attempt.</summary>
/// <param name="Accepted">Whether storage accepted the OPEN.</param>
/// <param name="Reason">Optional human-readable rejection reason (ASCII-safe, no secrets).</param>
public readonly record struct StorageArticleOpenResult(bool Accepted, string? Reason = null)
{
    /// <summary>Rejected open result.</summary>
    public static StorageArticleOpenResult Rejected(string? reason = null) => new(false, reason);
}

/// <summary>
/// Default open boundary that always rejects OPEN requests.
/// </summary>
public sealed class RejectingStorageArticleOpenBoundary : IStorageArticleOpenBoundary
{
    /// <inheritdoc />
    public StorageArticleOpenResult TryOpen(Guid requestId, in ArticleId articleId)
    {
        _ = requestId;
        _ = articleId;
        return StorageArticleOpenResult.Rejected("storage-not-implemented");
    }
}

/// <summary>
/// Null-object open boundary that always rejects OPEN requests.
/// </summary>
public sealed class NullStorageArticleOpenBoundary : IStorageArticleOpenBoundary
{
    /// <summary>Shared singleton instance.</summary>
    public static NullStorageArticleOpenBoundary Instance { get; } = new();

    /// <inheritdoc />
    public StorageArticleOpenResult TryOpen(Guid requestId, in ArticleId articleId)
    {
        _ = requestId;
        _ = articleId;
        return StorageArticleOpenResult.Rejected("storage-not-implemented");
    }
}
