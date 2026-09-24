namespace VectorNNTP.NNTPD.Session;

/// <summary>Validates NNTP message-id tokens used by transfer commands.</summary>
/// <remarks>
/// <see cref="IsBasicWellFormed(ReadOnlySpan{byte})"/> is the command-parser envelope check
/// (max 250 octets, starts with <c>&lt;</c>, ends with <c>&gt;</c>).
/// <see cref="IsWellFormed(ReadOnlySpan{byte})"/> is the stricter handler-oriented check
/// and is not used by the command parser.
/// </remarks>
public static class NntpMessageId
{
    /// <summary>Maximum Message-ID length accepted by the command parser (octets).</summary>
    public const int MaxBasicLength = 250;

    /// <summary>
    /// Command-parser envelope check: length is 1–250 octets, first byte is <c>&lt;</c>,
    /// last byte is <c>&gt;</c>. Does not inspect interior contents.
    /// </summary>
    public static bool IsBasicWellFormed(ReadOnlySpan<byte> value)
    {
        if (value.Length is < 1 or > MaxBasicLength)
        {
            return false;
        }

        return value[0] == (byte)'<' && value[^1] == (byte)'>';
    }

    /// <summary>
    /// Command-parser envelope check for a string argument. Same rules as the byte overload.
    /// </summary>
    public static bool IsBasicWellFormed(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxBasicLength)
        {
            return false;
        }

        return value[0] == '<' && value[^1] == '>';
    }

    /// <summary>
    /// Returns whether <paramref name="value"/> looks like a usable message-id argument.
    /// </summary>
    public static bool IsWellFormed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Length < 5 || value.Length > 998)
        {
            return false;
        }

        if (value[0] != '<' || value[^1] != '>')
        {
            return false;
        }

        var at = value.IndexOf('@');
        return at > 1 && at < value.Length - 2;
    }

    /// <summary>
    /// Returns whether <paramref name="value"/> looks like a usable message-id argument.
    /// Zero-allocation. Does not allocate or create a string.
    /// </summary>
    public static bool IsWellFormed(ReadOnlySpan<byte> value)
    {
        if (value.Length < 5 || value.Length > 998)
        {
            return false;
        }

        if (value[0] != (byte)'<' || value[^1] != (byte)'>')
        {
            return false;
        }

        int at = value.IndexOf((byte)'@');
        return at > 1 && at < value.Length - 2;
    }
}
