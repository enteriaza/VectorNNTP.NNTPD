namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Validates Article Work Message-ID tokens without changing their wire identity.
/// </summary>
/// <remarks>
/// A valid token is <c>&lt;local@domain&gt;</c> with length 5–250. The exact
/// accepted string is preserved for later <c>ARTICLE</c> retrieval. This is the
/// NNTPD article-work envelope (<c>IsWellFormed</c>) capped at the NNTP command
/// maximum of 250. Old BackFiller's stricter INN/dot-atom grammar is not imported.
/// </remarks>
public static class ArticleWorkMessageId
{
    /// <summary>Minimum accepted length (<c>&lt;a@b&gt;</c>).</summary>
    public const int MinimumLength = 5;

    /// <summary>Maximum accepted length (NNTP command Message-ID envelope).</summary>
    public const int MaximumLength = 250;

    /// <summary>
    /// Returns whether <paramref name="messageId"/> is a usable Article Work Message-ID.
    /// </summary>
    /// <param name="messageId">Candidate including angle brackets. Must not be trimmed by the caller after acceptance.</param>
    /// <returns><see langword="true"/> when the exact token is acceptable.</returns>
    public static bool IsWellFormed(string? messageId)
    {
        if (string.IsNullOrEmpty(messageId)
            || messageId.Length < MinimumLength
            || messageId.Length > MaximumLength)
        {
            return false;
        }

        if (messageId[0] != '<' || messageId[^1] != '>')
        {
            return false;
        }

        var at = messageId.IndexOf('@');
        if (at <= 1 || at >= messageId.Length - 2)
        {
            return false;
        }

        return true;
    }
}
