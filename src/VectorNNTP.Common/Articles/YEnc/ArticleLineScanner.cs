using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace VectorNNTP.Common.Articles.YEnc
{
    /// <summary>
    /// Byte-level line scanning used by yEnc control-line recognition.
    /// </summary>
    /// <remarks>
    /// Control lines are recognized on CRLF and standalone LF only. A lone CR is payload.
    /// </remarks>
    public static class ArticleLineScanner
    {
        private const byte CR = (byte)'\r';
        private const byte LF = (byte)'\n';

        private static readonly Vector128<byte> PrevByteShuffleIndices = Vector128.Create(
            0xFF, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14);

        /// <summary>
        /// Finds the first CRLF or standalone LF terminator at or after <paramref name="startOffset"/>.
        /// </summary>
        /// <param name="span">Article body bytes.</param>
        /// <param name="startOffset">Search start offset.</param>
        /// <returns>
        /// Index of the CR in a CRLF pair or the LF in a standalone LF terminator, or -1 when none is found.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int IndexOfCrLf(ReadOnlySpan<byte> span, int startOffset)
        {
            if ((uint)startOffset >= (uint)span.Length)
            {
                return -1;
            }

            var i = startOffset;
            var n = span.Length;
            ref var b = ref MemoryMarshal.GetReference(span);

            if (Vector128.IsHardwareAccelerated && i + 17 <= n)
            {
                var crVec = Vector128.Create(CR);
                var lfVec = Vector128.Create(LF);
                var allOnes = Vector128<byte>.AllBitsSet;

                while (i + 17 <= n)
                {
                    var v0 = Vector128.LoadUnsafe(ref b, (nuint)i);
                    var v1 = Vector128.LoadUnsafe(ref b, (nuint)(i + 1));
                    var crlf = Vector128.BitwiseAnd(
                        Vector128.Equals(v0, crVec),
                        Vector128.Equals(v1, lfVec));

                    var prevBytes = i > startOffset
                        ? Vector128.LoadUnsafe(ref b, (nuint)(i - 1))
                        : Vector128.Shuffle(v0, PrevByteShuffleIndices);

                    var relStart = startOffset - i;
                    var atStart = Vector128<byte>.Zero;
                    if ((uint)relStart < 16)
                    {
                        atStart = Vector128.WithElement(atStart, relStart, (byte)0xFF);
                    }

                    var lf = Vector128.Equals(v0, lfVec);
                    var prevNotCr = Vector128.AndNot(allOnes, Vector128.Equals(prevBytes, crVec));
                    var standaloneLf = Vector128.BitwiseAnd(
                        lf,
                        Vector128.BitwiseOr(atStart, prevNotCr));

                    var hit = Vector128.BitwiseOr(crlf, standaloneLf);
                    var bits = Vector128.ExtractMostSignificantBits(hit);
                    if (bits != 0)
                    {
                        return i + BitOperations.TrailingZeroCount(bits);
                    }

                    i += 16;
                }
            }

            return IndexOfCrLfScalar(ref b, n, i, startOffset);
        }

        /// <summary>
        /// Advances from a terminator index returned by <see cref="IndexOfCrLf"/> to the next line start.
        /// </summary>
        /// <param name="span">Article body bytes.</param>
        /// <param name="lineEndIndex">Index returned by <see cref="IndexOfCrLf"/>.</param>
        /// <returns>
        /// Index immediately after a CRLF pair or standalone LF, or <c>span.Length</c> when out of range.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int AdvancePastLineTerminator(ReadOnlySpan<byte> span, int lineEndIndex) =>
            (uint)lineEndIndex >= (uint)span.Length
                ? span.Length
                : span[lineEndIndex] == CR && lineEndIndex + 1 < span.Length && span[lineEndIndex + 1] == LF
                    ? lineEndIndex + 2
                    : lineEndIndex + 1;

        /// <summary>
        /// Finds the next line beginning with <paramref name="prefix"/> at or after <paramref name="startOffset"/>.
        /// </summary>
        /// <param name="span">Article body bytes.</param>
        /// <param name="startOffset">Search start offset.</param>
        /// <param name="prefix">Byte prefix matched only at line start.</param>
        /// <returns>Line start offset, or -1 when no matching line exists.</returns>
        public static int FindLineStartingWith(ReadOnlySpan<byte> span, int startOffset, ReadOnlySpan<byte> prefix)
        {
            if ((uint)startOffset >= (uint)span.Length)
            {
                return -1;
            }

            if (prefix.IsEmpty)
            {
                return startOffset;
            }

            var lineStart = startOffset;
            while (lineStart < span.Length)
            {
                var lineEnd = IndexOfCrLf(span, lineStart);
                var lineContentEnd = lineEnd >= 0 ? lineEnd : span.Length;
                var line = span[lineStart..lineContentEnd];
                if (line.StartsWith(prefix))
                {
                    return lineStart;
                }

                if (lineEnd < 0)
                {
                    return -1;
                }

                lineStart = AdvancePastLineTerminator(span, lineEnd);
            }

            return -1;
        }

        private static int IndexOfCrLfScalar(ref byte b, int n, int i, int startOffset)
        {
            for (; i < n; i++)
            {
                if (Unsafe.Add(ref b, (nint)(uint)i) == CR
                    && i + 1 < n
                    && Unsafe.Add(ref b, (nint)(uint)(i + 1)) == LF)
                {
                    return i;
                }

                if (Unsafe.Add(ref b, (nint)(uint)i) == LF
                    && (i == startOffset || Unsafe.Add(ref b, (nint)(uint)(i - 1)) != CR))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
