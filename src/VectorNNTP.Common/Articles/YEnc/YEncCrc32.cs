using System.Runtime.CompilerServices;
using VectorNNTP.Common.Articles.Checksum;

namespace VectorNNTP.Common.Articles.YEnc;

/// <summary>
/// IEEE CRC-32 (polynomial <c>0xEDB88320</c>) used by yEnc trailer validation.
/// </summary>
/// <remarks>
/// Delegates to <see cref="IeeeCrc32"/>. This is not Castagnoli CRC32C. Coverage is
/// the decoded yEnc payload, not canonical ArtData.
/// </remarks>
public static class YEncCrc32
{
    /// <summary>Reflected IEEE CRC-32 polynomial.</summary>
    public const uint Polynomial = IeeeCrc32.Polynomial;

    /// <summary>Initial accumulator value before feeding decoded bytes.</summary>
    public const uint InitialAccumulator = IeeeCrc32.InitialAccumulator;

    /// <summary>
    /// Updates a CRC-32 accumulator with additional bytes. Does not apply the final XOR.
    /// </summary>
    /// <param name="crc">Current accumulator (use <see cref="InitialAccumulator"/> to start).</param>
    /// <param name="data">Bytes to fold into the accumulator.</param>
    /// <returns>Updated accumulator.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Update(uint crc, ReadOnlySpan<byte> data) => IeeeCrc32.Update(crc, data);

    /// <summary>Applies the final XOR used by yEnc CRC comparison.</summary>
    /// <param name="crc">Accumulator after the last <see cref="Update"/>.</param>
    /// <returns>The CRC value compared to <c>crc32=</c> / <c>pcrc32=</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Finalize(uint crc) => IeeeCrc32.Finalize(crc);

    /// <summary>Computes the finalized IEEE CRC-32 of <paramref name="data"/>.</summary>
    /// <param name="data">Input bytes.</param>
    /// <returns>Finalized CRC-32.</returns>
    public static uint Compute(ReadOnlySpan<byte> data) => IeeeCrc32.Compute(data);
}
