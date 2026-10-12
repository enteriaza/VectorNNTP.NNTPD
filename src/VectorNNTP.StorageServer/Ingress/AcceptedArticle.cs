using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Ingress;

/// <summary>
/// One Path-rewritten article handed from VATP to in-memory consumers.
/// </summary>
/// <param name="ArtId">Article identity after Path rewrite.</param>
/// <param name="ArtHash">Hash of <paramref name="ArtData"/>.</param>
/// <param name="ArtSize">Length of <paramref name="ArtData"/> in bytes.</param>
/// <param name="ArtData">
/// Canonical article bytes, including the Path hop applied for this server.
/// The queue holds this buffer until a consumer dequeues it. The producer must not
/// mutate the buffer after a successful enqueue. Dequeue transfers that ownership
/// to the consumer. The queue does not copy the bytes and does not write them to disk.
/// </param>
public readonly record struct AcceptedArticle(
    ArticleId ArtId,
    ulong ArtHash,
    int ArtSize,
    ReadOnlyMemory<byte> ArtData);
