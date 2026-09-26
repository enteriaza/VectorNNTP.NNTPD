namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Hard NNTP article safety bounds from the old BackFiller acquisition contract.
/// These are not runtime configuration settings.
/// </summary>
public static class ArticleResourceLimits
{
    /// <summary>Maximum destuffed ARTICLE payload in bytes (5 MiB).</summary>
    public const int MaxArticleBytes = 5 * 1024 * 1024;

    /// <summary>Maximum physical article line length in wire bytes.</summary>
    public const int MaxArticleLineBytes = 1024;

    /// <summary>
    /// Maximum NNTP status-line length in bytes excluding CRLF.
    /// Old worker used 16 KiB; RFC 3977 §3.2 caps responses at 512 octets including CRLF.
    /// </summary>
    public const int MaxStatusLineBytes = 16 * 1024;
}
