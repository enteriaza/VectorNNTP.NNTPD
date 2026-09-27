namespace VectorNNTP.Common.Articles;

/// <summary>
/// Hard protocol and resource boundaries for NNTP article processing.
/// These are safety limits, not runtime configuration settings.
/// </summary>
/// <remarks>
/// The line boundary is 1024 characters by contract and is enforced on ASCII
/// wire bytes because article framing is byte-oriented.
/// </remarks>
public static class ArticleResourceLimits
{
    /// <summary>Maximum accepted ARTICLE payload size in bytes.</summary>
    public const int MaxArticleBytes = 5 * 1024 * 1024;

    /// <summary>Maximum accepted ARTICLE/header line length in characters.</summary>
    public const int MaxArticleLineCharacters = 1024;

    /// <summary>Maximum accepted ARTICLE/header line length in wire bytes.</summary>
    public const int MaxArticleLineBytes = MaxArticleLineCharacters;
}
