namespace VectorNNTP.Common.Articles.Parsing;

/// <summary>
/// Bounded unfolding of folded header values for semantic validation.
/// Raw article slices are not modified.
/// </summary>
public static class NntpArticleHeaderValueUnfolder
{
    /// <summary>
    /// Unfolds one raw header-value slice into a semantic byte view.
    /// </summary>
    /// <param name="rawValue">Raw header-value bytes, including continuation terminators and leading continuation whitespace.</param>
    /// <param name="destination">Bounded destination receiving unfolded semantic bytes.</param>
    /// <param name="bytesWritten">Semantic bytes written when unfolding succeeds.</param>
    /// <returns>
    /// <see langword="true"/> when unfolding succeeds within bounds and every embedded
    /// line break is a valid fold (CRLF/CR/LF followed by SP or HTAB).
    /// </returns>
    /// <remarks>
    /// Ordinary bytes are copied exactly. Each valid fold boundary becomes one ASCII space.
    /// </remarks>
    public static bool TryUnfold(ReadOnlySpan<byte> rawValue, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;

        for (var i = 0; i < rawValue.Length; i++)
        {
            var b = rawValue[i];
            if (b == (byte)'\r' || b == (byte)'\n')
            {
                var continuationStart = i + 1;
                if (b == (byte)'\r' && continuationStart < rawValue.Length && rawValue[continuationStart] == (byte)'\n')
                {
                    continuationStart++;
                }

                if (continuationStart >= rawValue.Length)
                {
                    return false;
                }

                var continuationPrefix = rawValue[continuationStart];
                if (continuationPrefix is not (byte)' ' and not (byte)'\t')
                {
                    return false;
                }

                if (bytesWritten >= destination.Length)
                {
                    return false;
                }

                destination[bytesWritten++] = (byte)' ';
                i = continuationStart;
                continue;
            }

            if (bytesWritten >= destination.Length)
            {
                return false;
            }

            destination[bytesWritten++] = b;
        }

        return true;
    }
}
