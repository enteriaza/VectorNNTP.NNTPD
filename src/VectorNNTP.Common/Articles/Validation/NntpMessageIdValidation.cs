using System.Runtime.CompilerServices;

namespace VectorNNTP.Common.Articles.Validation;

/// <summary>
/// Validates NNTP Message-ID tokens using INN-compatible dot-atom grammar.
/// </summary>
/// <remarks>
/// The article-path API operates on ASCII bytes. The character-span API is the
/// original request-boundary implementation and does not allocate.
/// </remarks>
public static class NntpMessageIdValidation
{
    /// <summary>Maximum Message-ID length in octets.</summary>
    public const int MaxMessageIdLength = 250;

    /// <summary>Minimum Message-ID length in octets after optional whitespace strip.</summary>
    public const int MinMessageIdLength = 3;

    /// <summary>
    /// Determines whether one Message-ID token is syntactically valid.
    /// </summary>
    /// <param name="messageId">Candidate including angle brackets.</param>
    /// <param name="stripSpaces">When <see langword="true"/>, leading and trailing whitespace is trimmed first.</param>
    /// <returns><see langword="true"/> when the token is ASCII, bracketed, and satisfies the grammar.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidMessageId(ReadOnlySpan<char> messageId, bool stripSpaces = false)
    {
        var length = messageId.Length;
        if (length is 0 or > MaxMessageIdLength)
        {
            return false;
        }

        var start = 0;
        var end = length;
        if (stripSpaces)
        {
            start = NntpMessageIdValidationSimd.TrimLeadingWhitespace(messageId, start, end);
            end = NntpMessageIdValidationSimd.TrimTrailingWhitespace(messageId, start, end);
        }

        if (end - start < MinMessageIdLength)
        {
            return false;
        }

        if (!NntpMessageIdValidationSimd.IsAllAscii(messageId, start, end))
        {
            return false;
        }

        if (messageId[start] != '<')
        {
            return false;
        }

        if (!TryParseDotAtomSequence(messageId, start + 1, end, '@', out var atIndex))
        {
            return false;
        }

        var domainStart = atIndex + 1;
        var closeIndex = end - 1;
        return domainStart < closeIndex
            && messageId[closeIndex] == '>'
            && IsValidRightPartMessageId(messageId, domainStart, closeIndex, stripSpaces: false, bracket: false);
    }

    /// <summary>
    /// Determines whether one Message-ID token is syntactically valid.
    /// </summary>
    /// <param name="messageId">Candidate including angle brackets as ASCII bytes.</param>
    /// <param name="stripSpaces">When <see langword="true"/>, leading and trailing ASCII whitespace is trimmed first.</param>
    /// <returns><see langword="true"/> when the token is 7-bit ASCII, bracketed, and satisfies the grammar.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidMessageId(ReadOnlySpan<byte> messageId, bool stripSpaces = false)
    {
        var length = messageId.Length;
        if (length is 0 or > MaxMessageIdLength)
        {
            return false;
        }

        var start = 0;
        var end = length;
        if (stripSpaces)
        {
            start = NntpMessageIdValidationSimd.TrimLeadingAsciiWhitespace(messageId, start, end);
            end = NntpMessageIdValidationSimd.TrimTrailingAsciiWhitespace(messageId, start, end);
        }

        if (end - start < MinMessageIdLength)
        {
            return false;
        }

        if (!NntpMessageIdValidationSimd.IsAllAscii(messageId, start, end))
        {
            return false;
        }

        if (messageId[start] != (byte)'<')
        {
            return false;
        }

        if (!TryParseDotAtomSequence(messageId, start + 1, end, (byte)'@', out var atIndex))
        {
            return false;
        }

        var domainStart = atIndex + 1;
        var closeIndex = end - 1;
        return domainStart < closeIndex
            && messageId[closeIndex] == (byte)'>'
            && IsValidRightPartMessageId(messageId, domainStart, closeIndex, stripSpaces: false, bracket: false);
    }

    /// <summary>
    /// Determines whether one Message-ID token is syntactically valid.
    /// </summary>
    /// <param name="messageId">Candidate string.</param>
    /// <param name="stripSpaces">When <see langword="true"/>, leading and trailing whitespace is trimmed first.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidMessageId(string? messageId, bool stripSpaces = false) =>
        messageId is { Length: > 0 } && IsValidMessageId(messageId.AsSpan(), stripSpaces);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidRightPartMessageId(
        ReadOnlySpan<char> span,
        int startIndex,
        int endIndex,
        bool stripSpaces,
        bool bracket)
    {
        if (startIndex >= endIndex)
        {
            return false;
        }

        int index;
        if (span[startIndex] == '[')
        {
            if (!TryParseDomainLiteral(span, startIndex, endIndex, out index))
            {
                return false;
            }
        }
        else if (!TryParseDotAtomSequence(span, startIndex, endIndex, '\0', out index))
        {
            return false;
        }

        if (bracket)
        {
            if (index >= endIndex || span[index] != '>')
            {
                return false;
            }

            index++;
        }

        if (stripSpaces)
        {
            index = NntpMessageIdValidationSimd.TrimLeadingWhitespace(span, index, endIndex);
        }

        return index == endIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidRightPartMessageId(
        ReadOnlySpan<byte> span,
        int startIndex,
        int endIndex,
        bool stripSpaces,
        bool bracket)
    {
        if (startIndex >= endIndex)
        {
            return false;
        }

        int index;
        if (span[startIndex] == (byte)'[')
        {
            if (!TryParseDomainLiteral(span, startIndex, endIndex, out index))
            {
                return false;
            }
        }
        else if (!TryParseDotAtomSequence(span, startIndex, endIndex, 0, out index))
        {
            return false;
        }

        if (bracket)
        {
            if (index >= endIndex || span[index] != (byte)'>')
            {
                return false;
            }

            index++;
        }

        if (stripSpaces)
        {
            index = NntpMessageIdValidationSimd.TrimLeadingAsciiWhitespace(span, index, endIndex);
        }

        return index == endIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseDotAtomSequence(
        ReadOnlySpan<char> span,
        int startIndex,
        int endIndex,
        char stopChar,
        out int stopIndex)
    {
        stopIndex = startIndex;
        if (startIndex >= endIndex)
        {
            return false;
        }

        var parsedAtom = false;
        var index = startIndex;
        while (index < endIndex)
        {
            var consumed = NntpMessageIdValidationSimd.ConsumeAtomCharacters(span, index, endIndex);
            if (consumed == 0)
            {
                return false;
            }

            parsedAtom = true;
            index += consumed;

            if (index >= endIndex)
            {
                stopIndex = index;
                return parsedAtom && stopChar == '\0';
            }

            if (stopChar != '\0' && span[index] == stopChar)
            {
                stopIndex = index;
                return parsedAtom;
            }

            if (span[index] != '.')
            {
                return false;
            }

            index++;
            if (index >= endIndex)
            {
                return false;
            }
        }

        stopIndex = index;
        return parsedAtom && stopChar == '\0';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseDotAtomSequence(
        ReadOnlySpan<byte> span,
        int startIndex,
        int endIndex,
        byte stopChar,
        out int stopIndex)
    {
        stopIndex = startIndex;
        if (startIndex >= endIndex)
        {
            return false;
        }

        var parsedAtom = false;
        var index = startIndex;
        while (index < endIndex)
        {
            var consumed = NntpMessageIdValidationSimd.ConsumeAtomCharacters(span, index, endIndex);
            if (consumed == 0)
            {
                return false;
            }

            parsedAtom = true;
            index += consumed;

            if (index >= endIndex)
            {
                stopIndex = index;
                return parsedAtom && stopChar == 0;
            }

            if (stopChar != 0 && span[index] == stopChar)
            {
                stopIndex = index;
                return parsedAtom;
            }

            if (span[index] != (byte)'.')
            {
                return false;
            }

            index++;
            if (index >= endIndex)
            {
                return false;
            }
        }

        stopIndex = index;
        return parsedAtom && stopChar == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseDomainLiteral(ReadOnlySpan<char> span, int startIndex, int rangeEndIndex, out int endIndex)
    {
        endIndex = startIndex;
        if (startIndex >= rangeEndIndex || span[startIndex] != '[')
        {
            return false;
        }

        var index = startIndex + 1;
        while (index < rangeEndIndex)
        {
            var current = span[index];
            if (current == ']')
            {
                endIndex = index + 1;
                return index > startIndex + 1;
            }

            if (!NntpMessageIdCharClasses.IsNorm(current))
            {
                return false;
            }

            index++;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseDomainLiteral(ReadOnlySpan<byte> span, int startIndex, int rangeEndIndex, out int endIndex)
    {
        endIndex = startIndex;
        if (startIndex >= rangeEndIndex || span[startIndex] != (byte)'[')
        {
            return false;
        }

        var index = startIndex + 1;
        while (index < rangeEndIndex)
        {
            var current = span[index];
            if (current == (byte)']')
            {
                endIndex = index + 1;
                return index > startIndex + 1;
            }

            if (!NntpMessageIdCharClasses.IsNorm(current))
            {
                return false;
            }

            index++;
        }

        return false;
    }
}
