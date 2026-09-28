namespace VectorNNTP.Common.Articles.Processing;

/// <summary>
/// Byte-oriented article identity comparisons used after parse and before retain.
/// </summary>
public static class NntpArticleIdentity
{
    /// <summary>
    /// Compares a parsed article Message-ID to the requested Message-ID without allocating.
    /// </summary>
    /// <param name="articleMessageId">Message-ID header value bytes from the parsed article.</param>
    /// <param name="requestedMessageId">Message-ID requested by the caller.</param>
    /// <returns>
    /// <see langword="true"/> when both identities have the same length and each article byte equals
    /// the corresponding request character's low 8 bits. No case folding or angle-bracket normalization is applied.
    /// </returns>
    public static bool MatchesRequest(ReadOnlySpan<byte> articleMessageId, ReadOnlySpan<char> requestedMessageId)
    {
        if (articleMessageId.Length != requestedMessageId.Length)
        {
            return false;
        }

        for (var i = 0; i < articleMessageId.Length; i++)
        {
            if (articleMessageId[i] != (byte)requestedMessageId[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compares a parsed article Message-ID to requested Message-ID octets without allocating.
    /// </summary>
    /// <param name="articleMessageId">Message-ID header value bytes from the parsed article.</param>
    /// <param name="requestedMessageId">Message-ID requested by the caller (wire octets).</param>
    /// <returns><see langword="true"/> when both spans are identical.</returns>
    public static bool MatchesRequest(ReadOnlySpan<byte> articleMessageId, ReadOnlySpan<byte> requestedMessageId) =>
        articleMessageId.SequenceEqual(requestedMessageId);
}
