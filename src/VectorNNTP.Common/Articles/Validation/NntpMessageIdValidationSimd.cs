using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace VectorNNTP.Common.Articles.Validation
{
    /// <summary>
    /// SIMD and scalar helpers for hot-path Message-ID parsing.
    /// </summary>
    internal static class NntpMessageIdValidationSimd
    {
        /// <summary>UTF-16 lanes in one <see cref="Vector128{T}"/> of <see cref="ushort"/> (16 bytes).</summary>
        private const int Vector128CharCount = 8;

        /// <summary>Byte lanes in one <see cref="Vector128{T}"/> of <see cref="byte"/>.</summary>
        private const int Vector128ByteCount = 16;

        /// <summary>Inclusive ASCII digit lower bound <c>0</c>, broadcast to every UTF-16 lane.</summary>
        private static readonly Vector128<ushort> DigitLoVec128 = Vector128.Create((ushort)'0');

        /// <summary>Inclusive ASCII digit upper bound <c>9</c>, broadcast to every UTF-16 lane.</summary>
        private static readonly Vector128<ushort> DigitHiVec128 = Vector128.Create((ushort)'9');

        /// <summary>Inclusive ASCII upper-case lower bound <c>A</c>, broadcast to every UTF-16 lane.</summary>
        private static readonly Vector128<ushort> UpperLoVec128 = Vector128.Create((ushort)'A');

        /// <summary>Inclusive ASCII upper-case upper bound <c>Z</c>, broadcast to every UTF-16 lane.</summary>
        private static readonly Vector128<ushort> UpperHiVec128 = Vector128.Create((ushort)'Z');

        /// <summary>Inclusive ASCII lower-case lower bound <c>a</c>, broadcast to every UTF-16 lane.</summary>
        private static readonly Vector128<ushort> LowerLoVec128 = Vector128.Create((ushort)'a');

        /// <summary>Inclusive ASCII lower-case upper bound <c>z</c>, broadcast to every UTF-16 lane.</summary>
        private static readonly Vector128<ushort> LowerHiVec128 = Vector128.Create((ushort)'z');

        /// <summary>Inclusive ASCII digit lower bound <c>0x30</c>, broadcast to every byte lane.</summary>
        private static readonly Vector128<byte> DigitLoBytes = Vector128.Create((byte)'0');

        /// <summary>Inclusive ASCII digit upper bound <c>0x39</c>, broadcast to every byte lane.</summary>
        private static readonly Vector128<byte> DigitHiBytes = Vector128.Create((byte)'9');

        /// <summary>Inclusive ASCII upper-case lower bound <c>0x41</c>, broadcast to every byte lane.</summary>
        private static readonly Vector128<byte> UpperLoBytes = Vector128.Create((byte)'A');

        /// <summary>Inclusive ASCII upper-case upper bound <c>0x5A</c>, broadcast to every byte lane.</summary>
        private static readonly Vector128<byte> UpperHiBytes = Vector128.Create((byte)'Z');

        /// <summary>Inclusive ASCII lower-case lower bound <c>0x61</c>, broadcast to every byte lane.</summary>
        private static readonly Vector128<byte> LowerLoBytes = Vector128.Create((byte)'a');

        /// <summary>Inclusive ASCII lower-case upper bound <c>0x7A</c>, broadcast to every byte lane.</summary>
        private static readonly Vector128<byte> LowerHiBytes = Vector128.Create((byte)'z');

        /// <summary>
        /// <c>0x80</c> mask. AND with a byte vector is zero only when every lane is below <c>0x80</c>.
        /// </summary>
        private static readonly Vector128<byte> HighBitBytes = Vector128.Create((byte)0x80);

        /// <summary>
        /// Advances <paramref name="start"/> while <see cref="char.IsWhiteSpace(char)"/> is true.
        /// </summary>
        /// <param name="span">Candidate Message-ID characters.</param>
        /// <param name="start">Inclusive start of the range.</param>
        /// <param name="end">Exclusive end of the range.</param>
        /// <returns>Index of the first non-whitespace character, or <paramref name="end"/> when the range is exhausted.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int TrimLeadingWhitespace(ReadOnlySpan<char> span, int start, int end)
        {
            var index = start;
            while (index < end && char.IsWhiteSpace(span[index]))
            {
                index++;
            }

            return index;
        }

        /// <summary>
        /// Retreats <paramref name="end"/> while the preceding character is <see cref="char.IsWhiteSpace(char)"/>.
        /// </summary>
        /// <param name="span">Candidate Message-ID characters.</param>
        /// <param name="start">Inclusive bound that the result does not pass.</param>
        /// <param name="end">Exclusive end of the range.</param>
        /// <returns>Exclusive end after trailing Unicode whitespace is removed.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int TrimTrailingWhitespace(ReadOnlySpan<char> span, int start, int end)
        {
            var index = end;
            while (index > start && char.IsWhiteSpace(span[index - 1]))
            {
                index--;
            }

            return index;
        }

        /// <summary>
        /// Advances <paramref name="start"/> while <see cref="IsAsciiWhiteSpace"/> is true.
        /// </summary>
        /// <param name="span">Candidate Message-ID bytes.</param>
        /// <param name="start">Inclusive start of the range.</param>
        /// <param name="end">Exclusive end of the range.</param>
        /// <returns>Index of the first byte that is not SP, HTAB, LF, CR, FF, or VT, or <paramref name="end"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int TrimLeadingAsciiWhitespace(ReadOnlySpan<byte> span, int start, int end)
        {
            var index = start;
            while (index < end && IsAsciiWhiteSpace(span[index]))
            {
                index++;
            }

            return index;
        }

        /// <summary>
        /// Retreats <paramref name="end"/> while the preceding byte is <see cref="IsAsciiWhiteSpace"/>.
        /// </summary>
        /// <param name="span">Candidate Message-ID bytes.</param>
        /// <param name="start">Inclusive bound that the result does not pass.</param>
        /// <param name="end">Exclusive end of the range.</param>
        /// <returns>Exclusive end after trailing SP, HTAB, LF, CR, FF, and VT bytes are removed.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int TrimTrailingAsciiWhitespace(ReadOnlySpan<byte> span, int start, int end)
        {
            var index = end;
            while (index > start && IsAsciiWhiteSpace(span[index - 1]))
            {
                index--;
            }

            return index;
        }

        /// <summary>
        /// Returns whether every UTF-16 unit in <c>[start, end)</c> is at most <c>0x7F</c>.
        /// </summary>
        /// <param name="span">Candidate Message-ID characters.</param>
        /// <param name="start">Inclusive start of the range.</param>
        /// <param name="end">Exclusive end of the range.</param>
        /// <returns>
        /// <see langword="true"/> when the range is empty or every unit has a zero high byte.
        /// A full <see cref="Vector128{T}"/> is rejected as soon as any lane is above <c>0x7F</c>.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsAllAscii(ReadOnlySpan<char> span, int start, int end)
        {
            var index = start;
            ref var searchRef = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(span));

            if (Vector128.IsHardwareAccelerated)
            {
                var zero = Vector128<ushort>.Zero;
                var simdEnd = end - Vector128CharCount;
                while (index <= simdEnd)
                {
                    var chunk = Vector128.LoadUnsafe(ref searchRef, (nuint)index);
                    if (!Vector128.EqualsAll(Vector128.ShiftRightLogical(chunk, 8), zero))
                    {
                        return false;
                    }

                    index += Vector128CharCount;
                }
            }

            for (; index < end; index++)
            {
                if (span[index] > 127)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns whether every byte in <c>[start, end)</c> is below <c>0x80</c>.
        /// </summary>
        /// <param name="span">Candidate Message-ID bytes.</param>
        /// <param name="start">Inclusive start of the range.</param>
        /// <param name="end">Exclusive end of the range.</param>
        /// <returns>
        /// <see langword="true"/> when the range is empty or no byte has the <see cref="HighBitBytes"/> bit set.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsAllAscii(ReadOnlySpan<byte> span, int start, int end)
        {
            var index = start;
            ref var searchRef = ref MemoryMarshal.GetReference(span);

            if (Vector128.IsHardwareAccelerated)
            {
                var simdEnd = end - Vector128ByteCount;
                while (index <= simdEnd)
                {
                    var chunk = Vector128.LoadUnsafe(ref searchRef, (nuint)index);
                    if (!Vector128.EqualsAll(Vector128.BitwiseAnd(chunk, HighBitBytes), Vector128<byte>.Zero))
                    {
                        return false;
                    }

                    index += Vector128ByteCount;
                }
            }

            for (; index < end; index++)
            {
                if (span[index] > 127)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Counts a leading run of Message-ID atom characters in <c>[start, end)</c>.
        /// </summary>
        /// <param name="span">Candidate characters.</param>
        /// <param name="start">Inclusive start.</param>
        /// <param name="end">Exclusive end.</param>
        /// <returns>
        /// Number of characters consumed. Alphanumeric runs use <see cref="ConsumeAlphanumericPrefix(ReadOnlySpan{char}, int, int)"/>;
        /// other characters must pass <see cref="NntpMessageIdCharClasses.IsAtom(char)"/>. Stops at the first rejection.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int ConsumeAtomCharacters(ReadOnlySpan<char> span, int start, int end)
        {
            var index = start;
            while (index < end)
            {
                var alnumRun = ConsumeAlphanumericPrefix(span, index, end);
                if (alnumRun > 0)
                {
                    index += alnumRun;
                    continue;
                }

                if (!NntpMessageIdCharClasses.IsAtom(span[index]))
                {
                    break;
                }

                index++;
            }

            return index - start;
        }

        /// <summary>
        /// Counts a leading run of Message-ID atom bytes in <c>[start, end)</c>.
        /// </summary>
        /// <param name="span">Candidate bytes.</param>
        /// <param name="start">Inclusive start.</param>
        /// <param name="end">Exclusive end.</param>
        /// <returns>
        /// Number of bytes consumed. Alphanumeric runs use <see cref="ConsumeAlphanumericPrefix(ReadOnlySpan{byte}, int, int)"/>;
        /// other bytes must pass <see cref="NntpMessageIdCharClasses.IsAtom(byte)"/>. Stops at the first rejection.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int ConsumeAtomCharacters(ReadOnlySpan<byte> span, int start, int end)
        {
            var index = start;
            while (index < end)
            {
                var alnumRun = ConsumeAlphanumericPrefix(span, index, end);
                if (alnumRun > 0)
                {
                    index += alnumRun;
                    continue;
                }

                if (!NntpMessageIdCharClasses.IsAtom(span[index]))
                {
                    break;
                }

                index++;
            }

            return index - start;
        }

        /// <summary>
        /// Counts a leading ASCII letter-or-digit run in <c>[start, end)</c>.
        /// </summary>
        /// <param name="span">Candidate characters.</param>
        /// <param name="start">Inclusive start.</param>
        /// <param name="end">Exclusive end.</param>
        /// <returns>
        /// Number of characters consumed. A vector step is taken only when all
        /// <see cref="Vector128CharCount"/> lanes are ASCII and in <c>0-9</c>, <c>A-Z</c>, or <c>a-z</c>.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int ConsumeAlphanumericPrefix(ReadOnlySpan<char> span, int start, int end)
        {
            var index = start;
            if (!Vector128.IsHardwareAccelerated)
            {
                while (index < end && IsAsciiLetterOrDigit(span[index]))
                {
                    index++;
                }

                return index - start;
            }

            ref var searchRef = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(span));
            var zero = Vector128<ushort>.Zero;
            var simdEnd = end - Vector128CharCount;

            while (index <= simdEnd)
            {
                var chunk = Vector128.LoadUnsafe(ref searchRef, (nuint)index);
                if (!Vector128.EqualsAll(Vector128.ShiftRightLogical(chunk, 8), zero))
                {
                    break;
                }

                var isDigit = Vector128.BitwiseAnd(
                    Vector128.GreaterThanOrEqual(chunk, DigitLoVec128),
                    Vector128.LessThanOrEqual(chunk, DigitHiVec128));
                var isUpper = Vector128.BitwiseAnd(
                    Vector128.GreaterThanOrEqual(chunk, UpperLoVec128),
                    Vector128.LessThanOrEqual(chunk, UpperHiVec128));
                var isLower = Vector128.BitwiseAnd(
                    Vector128.GreaterThanOrEqual(chunk, LowerLoVec128),
                    Vector128.LessThanOrEqual(chunk, LowerHiVec128));
                var valid = Vector128.BitwiseOr(isDigit, Vector128.BitwiseOr(isUpper, isLower));
                if (!Vector128.EqualsAll(valid, Vector128<ushort>.AllBitsSet))
                {
                    break;
                }

                index += Vector128CharCount;
            }

            while (index < end && IsAsciiLetterOrDigit(span[index]))
            {
                index++;
            }

            return index - start;
        }

        /// <summary>
        /// Counts a leading ASCII letter-or-digit run in <c>[start, end)</c>.
        /// </summary>
        /// <param name="span">Candidate bytes.</param>
        /// <param name="start">Inclusive start.</param>
        /// <param name="end">Exclusive end.</param>
        /// <returns>
        /// Number of bytes consumed. A vector step is taken only when all
        /// <see cref="Vector128ByteCount"/> lanes are below <c>0x80</c> and in <c>0-9</c>, <c>A-Z</c>, or <c>a-z</c>.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int ConsumeAlphanumericPrefix(ReadOnlySpan<byte> span, int start, int end)
        {
            var index = start;
            if (!Vector128.IsHardwareAccelerated)
            {
                while (index < end && IsAsciiLetterOrDigit(span[index]))
                {
                    index++;
                }

                return index - start;
            }

            ref var searchRef = ref MemoryMarshal.GetReference(span);
            var simdEnd = end - Vector128ByteCount;

            while (index <= simdEnd)
            {
                var chunk = Vector128.LoadUnsafe(ref searchRef, (nuint)index);
                if (!Vector128.EqualsAll(Vector128.BitwiseAnd(chunk, HighBitBytes), Vector128<byte>.Zero))
                {
                    break;
                }

                var isDigit = Vector128.BitwiseAnd(
                    Vector128.GreaterThanOrEqual(chunk, DigitLoBytes),
                    Vector128.LessThanOrEqual(chunk, DigitHiBytes));
                var isUpper = Vector128.BitwiseAnd(
                    Vector128.GreaterThanOrEqual(chunk, UpperLoBytes),
                    Vector128.LessThanOrEqual(chunk, UpperHiBytes));
                var isLower = Vector128.BitwiseAnd(
                    Vector128.GreaterThanOrEqual(chunk, LowerLoBytes),
                    Vector128.LessThanOrEqual(chunk, LowerHiBytes));
                var valid = Vector128.BitwiseOr(isDigit, Vector128.BitwiseOr(isUpper, isLower));
                if (!Vector128.EqualsAll(valid, Vector128<byte>.AllBitsSet))
                {
                    break;
                }

                index += Vector128ByteCount;
            }

            while (index < end && IsAsciiLetterOrDigit(span[index]))
            {
                index++;
            }

            return index - start;
        }

        /// <summary>Returns whether <paramref name="value"/> is ASCII <c>0-9</c>, <c>A-Z</c>, or <c>a-z</c>.</summary>
        /// <param name="value">Character to test.</param>
        /// <returns><see langword="true"/> for those 62 characters only.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAsciiLetterOrDigit(char value) =>
            (uint)(value - '0') <= 9
            || (uint)(value - 'A') <= 25
            || (uint)(value - 'a') <= 25;

        /// <summary>Returns whether <paramref name="value"/> is ASCII <c>0-9</c>, <c>A-Z</c>, or <c>a-z</c>.</summary>
        /// <param name="value">Byte to test.</param>
        /// <returns><see langword="true"/> for those 62 bytes only.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAsciiLetterOrDigit(byte value) =>
            (uint)(value - (byte)'0') <= 9
            || (uint)(value - (byte)'A') <= 25
            || (uint)(value - (byte)'a') <= 25;

        /// <summary>
        /// Returns whether <paramref name="value"/> is SP, HTAB, LF, CR, FF, or VT.
        /// </summary>
        /// <param name="value">Byte to test.</param>
        /// <returns><see langword="true"/> for those six bytes. Other Unicode whitespace is not included.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAsciiWhiteSpace(byte value) =>
            value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f' or (byte)'\v';
    }
}
