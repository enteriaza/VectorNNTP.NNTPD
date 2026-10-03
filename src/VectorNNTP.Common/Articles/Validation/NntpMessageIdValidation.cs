using System.Runtime.CompilerServices;

namespace VectorNNTP.Common.Articles.Validation
{
    /// <summary>
    /// Validates NNTP Message-ID tokens using INN-compatible dot-atom grammar.
    /// </summary>
    /// <remarks>
    /// The article-path API operates on ASCII bytes. The character-span API is the
    /// original request-boundary implementation and does not allocate.
    /// </remarks>
    internal static class NntpMessageIdValidation
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
        internal static bool IsValidMessageId(ReadOnlySpan<char> messageId, bool stripSpaces = false)
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
        internal static bool IsValidMessageId(ReadOnlySpan<byte> messageId, bool stripSpaces = false)
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
        internal static bool IsValidMessageId(string? messageId, bool stripSpaces = false) =>
            messageId is { Length: > 0 } && IsValidMessageId(messageId.AsSpan(), stripSpaces);

        /// <summary>
        /// Validates the domain side of a bracketed Message-ID, from <paramref name="startIndex"/> through <paramref name="endIndex"/>.
        /// </summary>
        /// <param name="span">Full candidate, already known to be ASCII when called from <see cref="IsValidMessageId(ReadOnlySpan{char}, bool)"/>.</param>
        /// <param name="startIndex">First character of the domain (after <c>@</c>).</param>
        /// <param name="endIndex">Exclusive end of the domain. Callers pass the index of <c>&gt;</c>, which this method does not consume when <paramref name="bracket"/> is false.</param>
        /// <param name="stripSpaces">When <see langword="true"/>, Unicode whitespace after the domain or closing bracket is skipped before the end check.</param>
        /// <param name="bracket">When <see langword="true"/>, require a <c>&gt;</c> immediately after the domain.</param>
        /// <returns>
        /// <see langword="false"/> when the range is empty, the domain is not a dot-atom or a non-empty <c>[...]</c> literal,
        /// a required <c>&gt;</c> is missing, or bytes remain after the accepted domain.
        /// </returns>
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

        /// <summary>
        /// Validates the domain side of a bracketed Message-ID on ASCII bytes.
        /// </summary>
        /// <param name="span">Full candidate bytes.</param>
        /// <param name="startIndex">First byte of the domain (after <c>@</c>).</param>
        /// <param name="endIndex">Exclusive end.</param>
        /// <param name="stripSpaces">When <see langword="true"/>, SP, HTAB, LF, CR, FF, and VT after the domain are skipped before the end check.</param>
        /// <param name="bracket">When <see langword="true"/>, require <c>0x3E</c> immediately after the domain.</param>
        /// <returns>
        /// <see langword="false"/> when the range is empty, the domain is not a dot-atom or a non-empty bracketed literal,
        /// a required closing bracket is missing, or bytes remain after the accepted domain.
        /// </returns>
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

        /// <summary>
        /// Parses one or more atom runs separated by <c>.</c>, stopping before <paramref name="stopChar"/> when it is not <c>\0</c>.
        /// </summary>
        /// <param name="span">Candidate characters.</param>
        /// <param name="startIndex">Inclusive start. Must be less than <paramref name="endIndex"/>.</param>
        /// <param name="endIndex">Exclusive end.</param>
        /// <param name="stopChar"><c>@</c> for the local part, or <c>\0</c> when the sequence must consume through <paramref name="endIndex"/>.</param>
        /// <param name="stopIndex">Index of <paramref name="stopChar"/>, or <paramref name="endIndex"/> when <paramref name="stopChar"/> is <c>\0</c> and the sequence is consumed. Unchanged from <paramref name="startIndex"/> on failure.</param>
        /// <returns>
        /// <see langword="false"/> for an empty atom, a trailing dot, or a character that is neither dot, atom, nor the stop character.
        /// A <c>\0</c> stop succeeds only when the sequence ends exactly at <paramref name="endIndex"/>.
        /// </returns>
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

        /// <summary>
        /// Parses one or more atom runs separated by <c>0x2E</c>, stopping before <paramref name="stopChar"/> when it is not zero.
        /// </summary>
        /// <param name="span">Candidate bytes.</param>
        /// <param name="startIndex">Inclusive start. Must be less than <paramref name="endIndex"/>.</param>
        /// <param name="endIndex">Exclusive end.</param>
        /// <param name="stopChar"><c>0x40</c> for the local part, or <c>0</c> when the sequence must consume through <paramref name="endIndex"/>.</param>
        /// <param name="stopIndex">Index of <paramref name="stopChar"/>, or <paramref name="endIndex"/> when <paramref name="stopChar"/> is zero and the sequence is consumed. Left at <paramref name="startIndex"/> on failure.</param>
        /// <returns>
        /// <see langword="false"/> for an empty atom, a trailing dot, or a byte that is neither dot, atom, nor the stop byte.
        /// </returns>
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

        /// <summary>
        /// Parses a domain literal <c>[</c> … <c>]</c> whose interior characters pass <see cref="NntpMessageIdCharClasses.IsNorm(char)"/>.
        /// </summary>
        /// <param name="span">Candidate characters.</param>
        /// <param name="startIndex">Index of the opening <c>[</c>.</param>
        /// <param name="rangeEndIndex">Exclusive end of the allowed range.</param>
        /// <param name="endIndex">Index just after <c>]</c> on success; otherwise <paramref name="startIndex"/>.</param>
        /// <returns><see langword="false"/> when <c>[</c> is missing, the interior is empty, a non-norm character appears, or <c>]</c> is absent.</returns>
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

        /// <summary>
        /// Parses a domain literal <c>[</c> … <c>]</c> whose interior bytes pass <see cref="NntpMessageIdCharClasses.IsNorm(byte)"/>.
        /// </summary>
        /// <param name="span">Candidate bytes.</param>
        /// <param name="startIndex">Index of <c>0x5B</c>.</param>
        /// <param name="rangeEndIndex">Exclusive end of the allowed range.</param>
        /// <param name="endIndex">Index just after <c>0x5D</c> on success; otherwise <paramref name="startIndex"/>.</param>
        /// <returns><see langword="false"/> when the opening bracket is missing, the interior is empty, a non-norm byte appears, or the closing bracket is absent.</returns>
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
}
