namespace VectorNNTP.StorageServer.Storage.Engine.FileJournal;

/// <summary>
/// Raised when durable journal bytes are corrupt before the final valid frame boundary.
/// </summary>
/// <remarks>
/// Torn or corrupt final-frame tails are truncated and do not use this exception.
/// Mid-journal corruption fails closed via this type.
/// </remarks>
public sealed class ArticleJournalCorruptException : InvalidOperationException
{
    /// <summary>Creates a journal corruption exception.</summary>
    public ArticleJournalCorruptException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a journal corruption exception with an offset.</summary>
    public ArticleJournalCorruptException(string message, long offset)
        : base(message)
    {
        Offset = offset;
    }

    /// <summary>Gets the byte offset where corruption was detected, when known.</summary>
    public long? Offset { get; }
}
