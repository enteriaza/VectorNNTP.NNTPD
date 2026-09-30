using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage;

/// <summary>
/// Local article-presence probe used by the <c>cache.requests</c> lookup consumer.
/// </summary>
/// <remarks>
/// <see cref="DurableIndexArticlePresence"/> is the host implementation. It reports
/// Present index entries only. <see cref="NullStorageArticlePresence"/> remains for tests.
/// </remarks>
public interface IStorageArticlePresence
{
    /// <summary>
    /// Returns whether this StorageServer currently holds <paramref name="articleId"/>.
    /// </summary>
    ValueTask<bool> HasArticleAsync(ArticleId articleId, CancellationToken cancellationToken);
}

/// <summary>Always reports that no article is present.</summary>
public sealed class NullStorageArticlePresence : IStorageArticlePresence
{
    /// <summary>Shared singleton instance.</summary>
    public static NullStorageArticlePresence Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<bool> HasArticleAsync(ArticleId articleId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);
}

/// <summary>In-memory presence set for tests and temporary wiring.</summary>
public sealed class InMemoryStorageArticlePresence : IStorageArticlePresence
{
    private readonly HashSet<ArticleId> _articles = [];
    private readonly object _gate = new();

    /// <summary>Adds an article identity to the local presence set.</summary>
    public void Add(ArticleId articleId)
    {
        lock (_gate)
        {
            _articles.Add(articleId);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> HasArticleAsync(ArticleId articleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_articles.Contains(articleId));
        }
    }
}
