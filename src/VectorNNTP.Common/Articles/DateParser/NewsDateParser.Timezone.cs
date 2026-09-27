using System.Runtime.CompilerServices;

namespace VectorNNTP.Common.Articles.DateParser;

/// <content>
/// Trailing timezone-abbreviation detection and numeric-offset substitution on byte buffers.
/// </content>
public static partial class NewsDateParser
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAsciiLetter(byte value) => (uint)((value | 0x20) - (byte)'a') <= 'z' - 'a';

    private static bool TryGetTrailingAbbreviation(ReadOnlySpan<byte> input, out int abbrStart, out int abbrLength)
    {
        abbrStart = 0;
        abbrLength = 0;
        if (input.IsEmpty || !IsAsciiLetter(input[^1]))
        {
            return false;
        }

        var end = input.Length;
        var start = end - 1;
        while (start > 0 && IsAsciiLetter(input[start - 1]))
        {
            start--;
        }

        abbrLength = end - start;
        if (abbrLength is < 2 or > 8)
        {
            return false;
        }

        if (start == 0 || input[start - 1] is not ((byte)' ' or (byte)'\t'))
        {
            return false;
        }

        abbrStart = start;
        return true;
    }

    private static bool TryGetUnknownTrailingAbbreviation(ReadOnlySpan<byte> cleaned, out int abbreviationLength)
    {
        abbreviationLength = 0;
        if (!TryGetTrailingAbbreviation(cleaned, out var start, out var length))
        {
            return false;
        }

        if (TryFindTimezoneOffset(cleaned.Slice(start, length), out _))
        {
            return false;
        }

        abbreviationLength = length;
        return true;
    }

    private static int SubstituteTimezoneAbbreviation(Span<byte> buffer, int length, int maxLength)
    {
        if (!TryGetTrailingAbbreviation(buffer[..length], out var start, out var abbrLength))
        {
            return length;
        }

        if (!TryFindTimezoneOffset(buffer.Slice(start, abbrLength), out var offset))
        {
            return length;
        }

        if (start + offset.Length > maxLength)
        {
            return length;
        }

        offset.CopyTo(buffer[start..]);
        return start + offset.Length;
    }

    private static bool TryFindTimezoneOffset(ReadOnlySpan<byte> abbreviation, out ReadOnlySpan<byte> offset)
    {
        var table = TimezoneMappings;
        for (var i = 0; i < table.Length; i++)
        {
            var entry = table[i];
            if (AsciiEqualsIgnoreCase(abbreviation, entry.Abbreviation))
            {
                offset = entry.Offset;
                return true;
            }
        }

        offset = default;
        return false;
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (ToLowerAscii(left[i]) != ToLowerAscii(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToLowerAscii(byte value)
        => (uint)(value - (byte)'A') <= 'Z' - 'A' ? (byte)(value + 32) : value;
}
