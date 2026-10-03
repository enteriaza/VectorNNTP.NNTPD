using System.Runtime.CompilerServices;

namespace VectorNNTP.Common.Articles.Checksum
{
    /// <summary>
    /// IEEE CRC-32 (polynomial <c>0xEDB88320</c>) used for yEnc trailer validation.
    /// </summary>
    /// <remarks>
    /// This is not Castagnoli CRC32C. Reflected polynomial, initial <c>0xFFFFFFFF</c>,
    /// final XOR <c>0xFFFFFFFF</c>. Coverage is exactly the bytes supplied to
    /// <see cref="Compute"/> or <see cref="Update"/> — callers choose the slice.
    /// </remarks>
    public static class IeeeCrc32
    {
        /// <summary>Reflected IEEE CRC-32 polynomial.</summary>
        public const uint Polynomial = 0xEDB88320u;

        /// <summary>Initial accumulator value before feeding bytes.</summary>
        public const uint InitialAccumulator = 0xFFFFFFFFu;

        private static readonly uint[] Table = CreateTable();

        /// <summary>
        /// Updates a CRC-32 accumulator with additional bytes. Does not apply the final XOR.
        /// </summary>
        /// <param name="crc">Current accumulator (use <see cref="InitialAccumulator"/> to start).</param>
        /// <param name="data">Bytes to fold into the accumulator.</param>
        /// <returns>Updated accumulator.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            ReadOnlySpan<uint> table = Table;
            for (var i = 0; i < data.Length; i++)
            {
                crc = (crc >> 8) ^ table[(int)((crc ^ data[i]) & 0xFF)];
            }

            return crc;
        }

        /// <summary>Applies the final XOR used by IEEE CRC-32 comparison.</summary>
        /// <param name="crc">Accumulator after the last <see cref="Update"/>.</param>
        /// <returns>The finalized CRC-32 value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Finalize(uint crc) => crc ^ 0xFFFFFFFFu;

        /// <summary>Computes the finalized IEEE CRC-32 of <paramref name="data"/>.</summary>
        /// <param name="data">Input bytes.</param>
        /// <returns>Finalized CRC-32.</returns>
        public static uint Compute(ReadOnlySpan<byte> data) =>
            Finalize(Update(InitialAccumulator, data));

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                var value = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) == 0
                        ? value >> 1
                        : (value >> 1) ^ Polynomial;
                }

                table[i] = value;
            }

            return table;
        }
    }
}
