namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// INN <c>news</c> log disposition codes implemented for v1 ingress.
/// </summary>
/// <remarks>
/// Values match innd(8) LOGGING / newslog(5) where VectorNNTP actually
/// produces the event: <c>+</c> accepted, <c>j</c> accepted and filed as junk,
/// <c>-</c> deliberately rejected. <c>m</c> is a VectorNNTP disposition for a
/// POST accepted for moderation (not an INN innd code). Cancel processing
/// (<c>c</c>) is not produced until a cancel path exists. INN's informational
/// <c>?</c> (isolated CR/LF) is not emitted.
/// </remarks>
public enum NewsLogDisposition : byte
{
    /// <summary>Accepted article (<c>+</c>).</summary>
    Accepted = (byte)'+',

    /// <summary>Accepted article classified as junk (<c>j</c>).</summary>
    Junk = (byte)'j',

    /// <summary>Deliberately rejected article (<c>-</c>).</summary>
    Rejected = (byte)'-',

    /// <summary>POST accepted for moderation (<c>m</c>).</summary>
    Moderated = (byte)'m',
}
