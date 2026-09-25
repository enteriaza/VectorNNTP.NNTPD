using VectorNNTP.NNTPD.Session;

namespace VectorNNTP.NNTPD.Session.Commands.Posting;

/// <summary>Structural syntax checks for Netnews header field values (byte-oriented).</summary>
internal static class PostFieldSyntax
{
    /// <summary>RFC 5536 newsgroup-name: <c>component *( "." component )</c>.</summary>
    public static bool IsNewsgroupName(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty || value.Length > 128)
        {
            return false;
        }

        var componentLength = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var b = value[i];
            if (b == (byte)'.')
            {
                if (componentLength == 0)
                {
                    return false;
                }

                componentLength = 0;
                continue;
            }

            if (!IsComponentChar(b))
            {
                return false;
            }

            componentLength++;
        }

        return componentLength > 0;
    }

    /// <summary>RFC 3977 §3.6 Message-ID with an interior <c>@</c> and NNTP length limit.</summary>
    public static bool IsMessageId(ReadOnlySpan<byte> value)
    {
        if (value.Length is < 5 or > PostingLimits.MaxMessageIdOctets)
        {
            return false;
        }

        if (!NntpMessageId.IsWellFormed(value))
        {
            return false;
        }

        if (value[0] != (byte)'<' || value[^1] != (byte)'>')
        {
            return false;
        }

        var inner = value[1..^1];
        var at = inner.IndexOf((byte)'@');
        if (at <= 0 || at != inner.LastIndexOf((byte)'@') || at == inner.Length - 1)
        {
            return false;
        }

        for (var i = 0; i < inner.Length; i++)
        {
            var b = inner[i];
            if (b is < 33 or > 126 || b == (byte)'<' || b == (byte)'>' || b == (byte)' ')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One or more Message-IDs separated by WSP.</summary>
    public static bool TryParseMessageIdList(
        ReadOnlySpan<byte> value,
        List<ReadOnlyMemory<byte>> ids,
        int maxCount)
    {
        ids.Clear();
        var i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && IsWsp(value[i]))
            {
                i++;
            }

            if (i >= value.Length)
            {
                break;
            }

            if (value[i] != (byte)'<')
            {
                return false;
            }

            var end = value[i..].IndexOf((byte)'>');
            if (end < 0)
            {
                return false;
            }

            var token = value.Slice(i, end + 1);
            if (!IsMessageId(token))
            {
                return false;
            }

            if (ids.Count >= maxCount)
            {
                return false;
            }

            ids.Add(token.ToArray());
            i += end + 1;
        }

        return ids.Count > 0;
    }

    /// <summary>Comma-separated newsgroup names with optional WSP around commas.</summary>
    public static bool TryParseNewsgroupList(
        ReadOnlySpan<byte> value,
        List<string> groups,
        int maxCount,
        bool allowPoster)
    {
        groups.Clear();
        var i = 0;
        var sawToken = false;
        while (i < value.Length)
        {
            while (i < value.Length && IsWsp(value[i]))
            {
                i++;
            }

            if (i >= value.Length)
            {
                break;
            }

            if (value[i] == (byte)',')
            {
                return false;
            }

            var start = i;
            while (i < value.Length && value[i] != (byte)',' && !IsWsp(value[i]))
            {
                i++;
            }

            var token = value[start..i];
            if (token.IsEmpty)
            {
                return false;
            }

            if (allowPoster && EqualsFolded(token, "POSTER"u8))
            {
                if (sawToken || i < value.Length)
                {
                    while (i < value.Length && IsWsp(value[i]))
                    {
                        i++;
                    }

                    if (i < value.Length)
                    {
                        return false;
                    }
                }

                groups.Add("poster");
                return groups.Count == 1;
            }

            if (!IsNewsgroupName(token))
            {
                return false;
            }

            var name = System.Text.Encoding.ASCII.GetString(token);
            foreach (var existing in groups)
            {
                if (existing.Equals(name, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (groups.Count >= maxCount)
            {
                return false;
            }

            groups.Add(name);
            sawToken = true;

            while (i < value.Length && IsWsp(value[i]))
            {
                i++;
            }

            if (i >= value.Length)
            {
                break;
            }

            if (value[i] != (byte)',')
            {
                return false;
            }

            i++;
            if (i >= value.Length)
            {
                return false;
            }
        }

        return groups.Count > 0;
    }

    /// <summary>Requires a mailbox-shaped value containing exactly one <c>@</c> in a printable region.</summary>
    public static bool IsMailbox(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty || value.Length > 512)
        {
            return false;
        }

        var at = -1;
        for (var i = 0; i < value.Length; i++)
        {
            var b = value[i];
            if (b == 0 || (b < 32 && b != (byte)'\t'))
            {
                return false;
            }

            if (b == (byte)'@')
            {
                if (at >= 0)
                {
                    return false;
                }

                at = i;
            }
        }

        return at > 0 && at < value.Length - 1;
    }

    /// <summary>Distribution token: <c>world</c>, <c>local</c>, or a newsgroup-name.</summary>
    public static bool IsDistributionToken(ReadOnlySpan<byte> value) =>
        EqualsFolded(value, "WORLD"u8)
        || EqualsFolded(value, "LOCAL"u8)
        || IsNewsgroupName(value);

    public static bool EqualsFolded(ReadOnlySpan<byte> value, ReadOnlySpan<byte> upperAscii)
    {
        if (value.Length != upperAscii.Length)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var b = value[i];
            if (b >= (byte)'a' && b <= (byte)'z')
            {
                b = (byte)(b - 32);
            }

            if (b != upperAscii[i])
            {
                return false;
            }
        }

        return true;
    }

    public static bool HeaderNamesEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (FoldUpper(left[i]) != FoldUpper(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static byte FoldUpper(byte b) =>
        b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b;

    public static bool IsWsp(byte b) => b is (byte)' ' or (byte)'\t';

    private static bool IsComponentChar(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b is (byte)'+' or (byte)'-' or (byte)'_';
}
