using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Storage;

/// <summary>
/// Reports article presence from the published durable article index.
/// </summary>
/// <remarks>
/// True only when the index entry for the requested <see cref="ArticleId"/> is
/// <see cref="ArticleStorageState.Present"/>. Does not read segments, consult the
/// article cache, or mutate journal, index, or catalogue state.
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
            var present = _engine.Engine.Index.TryGet(articleId, out var metadata)
                && metadata.State == ArticleStorageState.Present;
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
