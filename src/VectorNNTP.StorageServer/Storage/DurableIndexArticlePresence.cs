using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Storage;

/// <summary>
/// Reports whether this StorageServer can serve the requested article.
/// </summary>
/// <remarks>
/// <para>
/// NNTPD fetches an article with VATP OPEN only after a positive <c>cache.requests</c>
/// reply. Presence is therefore the same serveable condition as
/// <see cref="IArticleStorageEngine.TryRead"/>: an index row in
/// <see cref="ArticleStorageState.Present"/>, or a durable outstanding journal Accept
/// whose payload proves ArtId, ArtHash, and ArtSize. A journal-only Accept is included
/// so an ACKed article is visible to the fleet before SATA publication. Evicted and
/// Invalid rows stay absent unless a newer outstanding Accept exists.
/// </para>
/// <para>
/// Does not consult the article cache or mutate journal, index, or catalogue state.
/// A Present row is not re-read from its segment; the outstanding-Accept check copies
/// and proves the journal payload and discards that copy.
/// </para>
/// </remarks>
public sealed class DurableIndexArticlePresence : IStorageArticlePresence
{
    private readonly StorageEngineApplicationService _engine;

    /// <summary>Initializes presence against the hosted storage engine.</summary>
    /// <param name="engine">Engine owner. Presence is false until <see cref="StorageEngineApplicationService.IsReady"/>.</param>
    public DurableIndexArticlePresence(StorageEngineApplicationService engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <inheritdoc />
    public ValueTask<bool> HasArticleAsync(ArticleId articleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_engine.IsReady)
        {
            return ValueTask.FromResult(false);
        }

        try
        {
            var present = _engine.Engine.HasDurableServeableArticle(articleId);
            return ValueTask.FromResult(present);
        }
        catch (InvalidOperationException) when (!_engine.IsReady)
        {
            // Engine was unpublished or the index was disposed between the readiness check and TryGet.
            // ObjectDisposedException derives from InvalidOperationException. A published engine does not swallow this.
            return ValueTask.FromResult(false);
        }
    }
}
