namespace VectorNNTP.StorageServer.Storage.Engine.FileSegments;

/// <summary>
/// Raised when segment identity or the active append offset cannot be established.
/// </summary>
public sealed class SegmentStoreCorruptException : InvalidOperationException
{
    /// <summary>Creates a segment-store corruption exception.</summary>
    public SegmentStoreCorruptException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a segment-store corruption exception with a file path.</summary>
    public SegmentStoreCorruptException(string message, string path)
        : base(message)
    {
        Path = path;
    }

    /// <summary>Gets the segment file path when known.</summary>
    public string? Path { get; }
}
