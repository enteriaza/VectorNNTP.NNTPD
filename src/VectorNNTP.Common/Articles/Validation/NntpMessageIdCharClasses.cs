using System.Runtime.CompilerServices;

namespace VectorNNTP.Common.Articles.Validation
{
    /// <summary>
    /// Allocation-free ASCII character-class tests for NNTP Message-ID validation.
    /// </summary>
    internal static class NntpMessageIdCharClasses
    {
        private const int BitmapWordCount = 8;

        private static readonly uint[] AtomBitmap = CreateBitmap("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!#$%&'*+-/=?^_`{|}~");

        private static readonly uint[] NormBitmap = CreateBitmap("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!#$%&'*+-/=?^_`{|}~\"(),.:;<@");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsAtom(char value)
        {
            uint code = value;
            return code < 128 && (AtomBitmap[code >> 5] & (1u << (int)(code & 31))) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsAtom(byte value) =>
            (AtomBitmap[value >> 5] & (1u << (value & 31))) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsNorm(char value)
        {
            uint code = value;
            return code < 128 && (NormBitmap[code >> 5] & (1u << (int)(code & 31))) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsNorm(byte value) =>
            (NormBitmap[value >> 5] & (1u << (value & 31))) != 0;

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
