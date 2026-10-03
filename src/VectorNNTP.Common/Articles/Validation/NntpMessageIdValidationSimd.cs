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
        private const int Vector128CharCount = 8;
        private const int Vector128ByteCount = 16;

        private static readonly Vector128<ushort> DigitLoVec128 = Vector128.Create((ushort)'0');
        private static readonly Vector128<ushort> DigitHiVec128 = Vector128.Create((ushort)'9');
        private static readonly Vector128<ushort> UpperLoVec128 = Vector128.Create((ushort)'A');
        private static readonly Vector128<ushort> UpperHiVec128 = Vector128.Create((ushort)'Z');
        private static readonly Vector128<ushort> LowerLoVec128 = Vector128.Create((ushort)'a');
        private static readonly Vector128<ushort> LowerHiVec128 = Vector128.Create((ushort)'z');

        private static readonly Vector128<byte> DigitLoBytes = Vector128.Create((byte)'0');
        private static readonly Vector128<byte> DigitHiBytes = Vector128.Create((byte)'9');
        private static readonly Vector128<byte> UpperLoBytes = Vector128.Create((byte)'A');
        private static readonly Vector128<byte> UpperHiBytes = Vector128.Create((byte)'Z');
        private static readonly Vector128<byte> LowerLoBytes = Vector128.Create((byte)'a');
        private static readonly Vector128<byte> LowerHiBytes = Vector128.Create((byte)'z');
        private static readonly Vector128<byte> HighBitBytes = Vector128.Create((byte)0x80);

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAsciiLetterOrDigit(char value) =>
            (uint)(value - '0') <= 9
            || (uint)(value - 'A') <= 25
            || (uint)(value - 'a') <= 25;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAsciiLetterOrDigit(byte value) =>
            (uint)(value - (byte)'0') <= 9
            || (uint)(value - (byte)'A') <= 25
            || (uint)(value - (byte)'a') <= 25;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsAsciiWhiteSpace(byte value) =>
            value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f' or (byte)'\v';
    }
}
