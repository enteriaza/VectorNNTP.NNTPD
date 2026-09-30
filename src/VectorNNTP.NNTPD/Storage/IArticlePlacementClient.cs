using VectorNNTP.Common.Articles;

namespace VectorNNTP.NNTPD.Storage;

/// <summary>Sends one CanonicalV1 article to one StorageServer over a dedicated STORE connection.</summary>
public interface IArticlePlacementClient
{
    /// <summary>Places <paramref name="record"/> on <paramref name="target"/>. One attempt.</summary>
    ValueTask<ArticlePlacementResult> PlaceAsync(
        ArticleRecord record,
        StorageServerFleetEntry target,
        CancellationToken cancellationToken);
}
