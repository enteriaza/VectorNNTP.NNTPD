using System.Runtime.CompilerServices;

namespace VectorNNTP.Common.Articles.YEnc;

/// <summary>
/// Parses strict one-to-eight digit hexadecimal ASCII values for yEnc CRC metadata.
/// </summary>
public static class HexUInt32Parser
{
    /// <summary>
    /// Parses hexadecimal ASCII bytes into a <see cref="uint"/>.
    /// </summary>
    /// <param name="hexBytes">One to eight ASCII hex digits with no prefix, sign, separator, or whitespace.</param>
    /// <param name="value">Parsed value when the method returns <see langword="true"/>; otherwise zero.</param>
    /// <returns><see langword="true"/> when the span is consumed as one to eight hex digits.</returns>
    public static bool TryParseHexUInt32(ReadOnlySpan<byte> hexBytes, out uint value)
    {
        value = 0;

        if (hexBytes.IsEmpty || hexBytes.Length > 8)
        {
            return false;
        }

        for (var i = 0; i < hexBytes.Length; i++)
        {
            var nibble = HexByteToNibble(hexBytes[i]);
            if (nibble < 0)
            {
                value = 0;
                return false;
            }

            value = (value << 4) | (uint)nibble;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HexByteToNibble(byte b) =>
        (uint)(b - (byte)'0') <= 9
            ? b - (byte)'0'
            : (uint)(b - (byte)'a') <= 5
                ? b - (byte)'a' + 10
                : (uint)(b - (byte)'A') <= 5
                    ? b - (byte)'A' + 10
                    : -1;
}
