using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace VectorNNTP.Common.Articles.DateParser
{
    /// <summary>
    /// Validates that code units stay within printable ASCII using vector instructions when available.
    /// </summary>
    internal static class PrintableAsciiSimd
    {
        /// <summary>UTF-16 lanes in one <see cref="Vector256{T}"/> of <see cref="ushort"/> (32 bytes).</summary>
        private const int Vector256UShortCount = 16;

        /// <summary>UTF-16 lanes in one <see cref="Vector128{T}"/> of <see cref="ushort"/> (16 bytes).</summary>
        private const int Vector128UShortCount = 8;

        /// <summary>Byte lanes in one <see cref="Vector256{T}"/> of <see cref="byte"/>.</summary>
        private const int Vector256ByteCount = 32;

        /// <summary>Byte lanes in one <see cref="Vector128{T}"/> of <see cref="byte"/>.</summary>
        private const int Vector128ByteCount = 16;

        /// <summary>Printable-ASCII floor <c>0x20</c>, subtracted from each UTF-16 lane before the range test.</summary>
        private static readonly Vector256<ushort> PrintableLoVec256 = Vector256.Create((ushort)0x20);

        /// <summary>
        /// Maximum <c>unit - 0x20</c> still inside printable ASCII. Together with <see cref="PrintableLoVec256"/> this is the inclusive window <c>0x20..0x7E</c>.
        /// </summary>
        private static readonly Vector256<ushort> PrintableRangeVec256 = Vector256.Create((ushort)0x5E);

        /// <summary>Printable-ASCII floor <c>0x20</c> for the 128-bit UTF-16 path.</summary>
        private static readonly Vector128<ushort> PrintableLoVec128 = Vector128.Create((ushort)0x20);

        /// <summary>
        /// Maximum <c>unit - 0x20</c> still inside printable ASCII on the 128-bit UTF-16 path (<c>0x20..0x7E</c>).
        /// </summary>
        private static readonly Vector128<ushort> PrintableRangeVec128 = Vector128.Create((ushort)0x5E);

        /// <summary>Inclusive printable-ASCII floor <c>0x20</c> for 256-bit byte compares.</summary>
        private static readonly Vector256<byte> PrintableLoBytes256 = Vector256.Create((byte)0x20);

        /// <summary>Inclusive printable-ASCII ceiling <c>0x7E</c> for 256-bit byte compares. DEL (<c>0x7F</c>) fails.</summary>
        private static readonly Vector256<byte> PrintableHiBytes256 = Vector256.Create((byte)0x7E);

        /// <summary><c>0x80</c> mask. Any set bit rejects the 256-bit block before the <c>0x20..0x7E</c> test.</summary>
        private static readonly Vector256<byte> HighBitBytes256 = Vector256.Create((byte)0x80);

        /// <summary>Inclusive printable-ASCII floor <c>0x20</c> for 128-bit byte compares.</summary>
        private static readonly Vector128<byte> PrintableLoBytes128 = Vector128.Create((byte)0x20);

        /// <summary>Inclusive printable-ASCII ceiling <c>0x7E</c> for 128-bit byte compares.</summary>
        private static readonly Vector128<byte> PrintableHiBytes128 = Vector128.Create((byte)0x7E);

        /// <summary><c>0x80</c> mask. Any set bit rejects the 128-bit block before the <c>0x20..0x7E</c> test.</summary>
        private static readonly Vector128<byte> HighBitBytes128 = Vector128.Create((byte)0x80);

        /// <summary>
        /// Returns whether every UTF-16 unit in <paramref name="span"/> is printable ASCII (<c>0x20..0x7E</c>).
        /// </summary>
        /// <param name="span">Input span.</param>
        /// <returns><see langword="true"/> when empty or every character is in range.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsAllPrintableAscii(ReadOnlySpan<char> span) =>
            span.Length == 0
            || IsAllInRange(
                span,
                PrintableLoVec256,
                PrintableRangeVec256,
                PrintableLoVec128,
                PrintableRangeVec128,
                0x20,
                0x5E);

        /// <summary>
        /// Returns whether every byte in <paramref name="span"/> is printable ASCII (<c>0x20..0x7E</c>).
        /// </summary>
        /// <param name="span">Input span.</param>
        /// <returns><see langword="true"/> when empty or every byte is in range.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsAllPrintableAscii(ReadOnlySpan<byte> span) =>
            span.Length == 0 || IsAllInRange(span);

        /// <summary>
        /// Returns whether every UTF-16 unit satisfies <c>(uint)(unit - scalarLo) &lt;= scalarRange</c>.
        /// </summary>
        /// <param name="span">Non-empty character span. Callers assert this.</param>
        /// <param name="loVec256">Value subtracted from each 256-bit lane before the range compare.</param>
        /// <param name="rangeVec256">Maximum accepted <c>unit - lo</c> on the 256-bit path. Greater-than rejects the block.</param>
        /// <param name="loVec128">Value subtracted from each 128-bit lane.</param>
        /// <param name="rangeVec128">Maximum accepted <c>unit - lo</c> on the 128-bit path.</param>
        /// <param name="scalarLo">Scalar window floor, matching the vector floors.</param>
        /// <param name="scalarRange">Scalar maximum of <c>unit - scalarLo</c>.</param>
        /// <returns><see langword="false"/> at the first unit outside the window; otherwise <see langword="true"/>.</returns>
        private static bool IsAllInRange(
            ReadOnlySpan<char> span,
            Vector256<ushort> loVec256,
            Vector256<ushort> rangeVec256,
            Vector128<ushort> loVec128,
            Vector128<ushort> rangeVec128,
            ushort scalarLo,
            ushort scalarRange)
        {
            Debug.Assert(span.Length > 0, "IsAllInRange requires non-empty span.");

            var i = 0;
            ref var searchRef = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(span));

            if (Vector256.IsHardwareAccelerated)
            {
                var simd256End = span.Length - Vector256UShortCount;
                while (i <= simd256End)
                {
                    var chunk = Vector256.LoadUnsafe(ref searchRef, (nuint)i);
                    var adjusted = Vector256.Subtract(chunk, loVec256);
                    var outOfRange = Vector256.GreaterThan(adjusted, rangeVec256);
                    if (!Vector256.EqualsAll(outOfRange, Vector256<ushort>.Zero))
                    {
                        return false;
                    }

                    i += Vector256UShortCount;
                }
            }

            if (Vector128.IsHardwareAccelerated)
            {
                var simd128End = span.Length - Vector128UShortCount;
                while (i <= simd128End)
                {
                    var chunk = Vector128.LoadUnsafe(ref searchRef, (nuint)i);
                    var adjusted = Vector128.Subtract(chunk, loVec128);
                    var outOfRange = Vector128.GreaterThan(adjusted, rangeVec128);
                    if (!Vector128.EqualsAll(outOfRange, Vector128<ushort>.Zero))
                    {
                        return false;
                    }

                    i += Vector128UShortCount;
                }
            }

            for (; i < span.Length; i++)
            {
                if ((uint)(span[i] - scalarLo) > scalarRange)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns whether every byte is printable ASCII <c>0x20..0x7E</c>.
        /// </summary>
        /// <param name="span">Non-empty byte span. Callers assert this.</param>
        /// <returns>
        /// <see langword="false"/> when any byte has bit <c>0x80</c> set or lies outside <c>0x20..0x7E</c>.
        /// </returns>
        private static bool IsAllInRange(ReadOnlySpan<byte> span)
        {
            Debug.Assert(span.Length > 0, "IsAllInRange requires non-empty span.");

            var i = 0;
            ref var searchRef = ref MemoryMarshal.GetReference(span);

            if (Vector256.IsHardwareAccelerated)
            {
                var simd256End = span.Length - Vector256ByteCount;
                while (i <= simd256End)
                {
                    var chunk = Vector256.LoadUnsafe(ref searchRef, (nuint)i);
                    if (!IsPrintableAsciiBlock(chunk, HighBitBytes256, PrintableLoBytes256, PrintableHiBytes256))
                    {
                        return false;
                    }

                    i += Vector256ByteCount;
                }
            }

            if (Vector128.IsHardwareAccelerated)
            {
                var simd128End = span.Length - Vector128ByteCount;
                while (i <= simd128End)
                {
                    var chunk = Vector128.LoadUnsafe(ref searchRef, (nuint)i);
                    if (!IsPrintableAsciiBlock(chunk, HighBitBytes128, PrintableLoBytes128, PrintableHiBytes128))
                    {
                        return false;
                    }

                    i += Vector128ByteCount;
                }
            }

            for (; i < span.Length; i++)
            {
                if ((uint)(span[i] - 0x20) > 0x5E)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns whether every lane of <paramref name="chunk"/> is in <c>[lo, hi]</c> and has bit <c>0x80</c> clear.
        /// </summary>
        /// <param name="chunk">32 input bytes.</param>
        /// <param name="highBit"><c>0x80</c> mask applied before the range test.</param>
        /// <param name="lo">Inclusive lower bound.</param>
        /// <param name="hi">Inclusive upper bound.</param>
        /// <returns><see langword="true"/> only when all 32 lanes pass both tests.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPrintableAsciiBlock(
            Vector256<byte> chunk,
            Vector256<byte> highBit,
            Vector256<byte> lo,
            Vector256<byte> hi)
        {
            if (!Vector256.EqualsAll(Vector256.BitwiseAnd(chunk, highBit), Vector256<byte>.Zero))
            {
                return false;
            }

            var inRange = Vector256.BitwiseAnd(
                Vector256.GreaterThanOrEqual(chunk, lo),
                Vector256.LessThanOrEqual(chunk, hi));
            return Vector256.EqualsAll(inRange, Vector256<byte>.AllBitsSet);
        }

        /// <summary>
        /// Returns whether every lane of <paramref name="chunk"/> is in <c>[lo, hi]</c> and has bit <c>0x80</c> clear.
        /// </summary>
        /// <param name="chunk">16 input bytes.</param>
        /// <param name="highBit"><c>0x80</c> mask applied before the range test.</param>
        /// <param name="lo">Inclusive lower bound.</param>
        /// <param name="hi">Inclusive upper bound.</param>
        /// <returns><see langword="true"/> only when all 16 lanes pass both tests.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPrintableAsciiBlock(
            Vector128<byte> chunk,
            Vector128<byte> highBit,
            Vector128<byte> lo,
            Vector128<byte> hi)
        {
            if (!Vector128.EqualsAll(Vector128.BitwiseAnd(chunk, highBit), Vector128<byte>.Zero))
            {
                return false;
            }

            var inRange = Vector128.BitwiseAnd(
                Vector128.GreaterThanOrEqual(chunk, lo),
                Vector128.LessThanOrEqual(chunk, hi));
            return Vector128.EqualsAll(inRange, Vector128<byte>.AllBitsSet);
        }
    }
}
