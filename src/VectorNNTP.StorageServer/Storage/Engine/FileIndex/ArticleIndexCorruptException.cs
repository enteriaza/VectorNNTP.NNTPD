namespace VectorNNTP.StorageServer.Storage.Engine.FileIndex;

/// <summary>
/// Raised when durable article-index bytes are corrupt before the final valid frame boundary.
/// </summary>
public sealed class ArticleIndexCorruptException : InvalidOperationException
{
    /// <summary>Creates an index corruption exception.</summary>
    public ArticleIndexCorruptException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an index corruption exception with an offset.</summary>
    public ArticleIndexCorruptException(string message, long offset)
        : base(message)
    {
        Offset = offset;
    }

    /// <summary>Gets the byte offset where corruption was detected, when known.</summary>
    public long? Offset { get; }
}
