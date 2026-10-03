using System.Runtime.CompilerServices;

namespace VectorNNTP.Common.Articles.Validation
{
    /// <summary>
    /// Allocation-free ASCII character-class tests for NNTP Message-ID validation.
    /// </summary>
    internal static class NntpMessageIdCharClasses
    {
        /// <summary>Eight <see cref="uint"/> words: 256 bits, one bit per byte value <c>0..255</c>.</summary>
        private const int BitmapWordCount = 8;

        /// <summary>
        /// Bit set for RFC 5322 <c>atext</c>: ASCII letters, digits, and <c>!#$%&amp;'*+-/=?^_`{|}~</c>.
        /// </summary>
        private static readonly uint[] AtomBitmap = CreateBitmap("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!#$%&'*+-/=?^_`{|}~");

        /// <summary>
        /// <see cref="AtomBitmap"/> plus <c>&quot;(),.:;&lt;@</c>, the extra bytes accepted inside a domain literal.
        /// </summary>
        private static readonly uint[] NormBitmap = CreateBitmap("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!#$%&'*+-/=?^_`{|}~\"(),.:;<@");

        /// <summary>
        /// Returns whether <paramref name="value"/> is an atom character.
        /// </summary>
        /// <param name="value">Candidate character.</param>
        /// <returns><see langword="false"/> when the code unit is <c>128</c> or above or its <see cref="AtomBitmap"/> bit is clear.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsAtom(char value)
        {
            uint code = value;
            return code < 128 && (AtomBitmap[code >> 5] & (1u << (int)(code & 31))) != 0;
        }

        /// <summary>
        /// Returns whether <paramref name="value"/> is an atom byte.
        /// </summary>
        /// <param name="value">Candidate byte. Values <c>128..255</c> have no bits set in <see cref="AtomBitmap"/>.</param>
        /// <returns><see langword="true"/> when the corresponding <see cref="AtomBitmap"/> bit is set.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsAtom(byte value) =>
            (AtomBitmap[value >> 5] & (1u << (value & 31))) != 0;

        /// <summary>
        /// Returns whether <paramref name="value"/> may appear inside a Message-ID domain literal.
        /// </summary>
        /// <param name="value">Candidate character.</param>
        /// <returns><see langword="false"/> when the code unit is <c>128</c> or above or its <see cref="NormBitmap"/> bit is clear.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsNorm(char value)
        {
            uint code = value;
            return code < 128 && (NormBitmap[code >> 5] & (1u << (int)(code & 31))) != 0;
        }

        /// <summary>
        /// Returns whether <paramref name="value"/> may appear inside a Message-ID domain literal.
        /// </summary>
        /// <param name="value">Candidate byte.</param>
        /// <returns><see langword="true"/> when the corresponding <see cref="NormBitmap"/> bit is set.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsNorm(byte value) =>
            (NormBitmap[value >> 5] & (1u << (value & 31))) != 0;

        /// <summary>
        /// Builds a 256-bit class table with one bit set per character in <paramref name="characters"/>.
        /// </summary>
        /// <param name="characters">ASCII code units below <c>128</c>. The built-in tables pass only those.</param>
        /// <returns>An array of <see cref="BitmapWordCount"/> words. Bit <c>code &amp; 31</c> of word <c>code &gt;&gt; 5</c> is set.</returns>
        private static uint[] CreateBitmap(ReadOnlySpan<char> characters)
        {
            var bitmap = new uint[BitmapWordCount];
            foreach (var value in characters)
            {
                var code = value;
                bitmap[code >> 5] |= 1u << (code & 31);
            }

            return bitmap;
        }
    }
}
