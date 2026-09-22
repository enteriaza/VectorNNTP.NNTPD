namespace VectorNNTP.NNTPD.Session;

/// <summary>NNTP operating mode (distinct from authentication/authorization).</summary>
public enum NntpSessionMode
{
    /// <summary>No MODE command has successfully selected an operating mode yet.</summary>
    Unspecified = 0,

    /// <summary>Reader mode after a successful <c>MODE READER</c>.</summary>
    Reader = 1,

    /// <summary>Transit/streaming mode after a successful <c>MODE STREAM</c> (future).</summary>
    Stream = 2,
}
