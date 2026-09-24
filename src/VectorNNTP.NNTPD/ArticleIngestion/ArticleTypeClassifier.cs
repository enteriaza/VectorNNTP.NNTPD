namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Deterministic Diablo-style article classification from header and body-prefix bytes.
/// </summary>
/// <remarks>
/// Mapped from Diablo <c>lib/arttype.c</c> <c>ArticleType()</c> / <c>ArtTypeConv()</c>.
/// Does not decode yEnc, BASE64, or uuencode. Does not scan the whole body when a
/// header or prefix marker is sufficient.
/// </remarks>
public static class ArticleTypeClassifier
{
    /// <summary>Maximum destuffed body prefix examined for markers (8 KiB).</summary>
    public const int MaxPrefixBytes = 8 * 1024;

    /// <summary>Classifies from destuffed headers and an optional destuffed body prefix.</summary>
    public static ArticleType Classify(ReadOnlySpan<byte> headers, ReadOnlySpan<byte> bodyPrefix)
    {
        var type = ArticleType.None;
        ScanLines(headers, inHeader: true, ref type);
        var prefix = bodyPrefix.Length > MaxPrefixBytes ? bodyPrefix[..MaxPrefixBytes] : bodyPrefix;
        ScanLines(prefix, inHeader: false, ref type);
        if (type == ArticleType.None || type == ArticleType.Default)
        {
            return ArticleType.Default;
        }

        return type & ~ArticleType.Default;
    }

    /// <summary>Applies one destuffed line (no CRLF) to <paramref name="type"/>.</summary>
    public static void ObserveLine(ReadOnlySpan<byte> line, bool inHeader, ref ArticleType type)
    {
        if (line.IsEmpty)
        {
            return;
        }

        if ((type & ArticleType.Binary) != 0 && !inHeader)
        {
            ObserveYenc(line, ref type);
            return;
        }

        ObserveYenc(line, ref type);
        ObserveContentAndControl(line, inHeader, ref type);
        if (!inHeader)
        {
            ObserveBodyMarkers(line, ref type);
        }

        if (type != ArticleType.None && type != ArticleType.Default)
        {
            type &= ~ArticleType.Default;
        }
    }

    private static void ScanLines(ReadOnlySpan<byte> block, bool inHeader, ref ArticleType type)
    {
        var offset = 0;
        while (offset < block.Length)
        {
            var remaining = block[offset..];
            var crlf = remaining.IndexOf("\r\n"u8);
            ReadOnlySpan<byte> line;
            if (crlf < 0)
            {
                line = remaining;
                offset = block.Length;
            }
            else
            {
                line = remaining[..crlf];
                offset += crlf + 2;
            }

            ObserveLine(line, inHeader, ref type);
        }
    }

    private static void ObserveYenc(ReadOnlySpan<byte> line, ref ArticleType type)
    {
        if (line.Length >= 13 && line[0] == (byte)'=' && StartsWithFolded(line, "=YBEGIN PART="u8))
        {
            type |= ArticleType.Binary | ArticleType.Partial | ArticleType.YEncoded;
            return;
        }

        if (line.Length >= 13 && line[0] == (byte)'=' && StartsWithFolded(line, "=YBEGIN LINE="u8))
        {
            type |= ArticleType.Binary | ArticleType.YEncoded;
        }
    }

    private static void ObserveContentAndControl(ReadOnlySpan<byte> line, bool inHeader, ref ArticleType type)
    {
        if (StartsWithFolded(line, "CONTENT-TYPE: TEXT/HTML"u8))
        {
            type |= ArticleType.Html | ArticleType.Mime;
        }
        else if (StartsWithFolded(line, "CONTENT-TYPE: MULTIPART"u8))
        {
            type |= ArticleType.Multipart | ArticleType.Mime;
        }
        else if (StartsWithFolded(line, "CONTENT-TYPE: APPLICATION/POSTSCRIPT"u8))
        {
            type |= ArticleType.PostScript | ArticleType.Mime;
        }
        else if (StartsWithFolded(line, "CONTENT-TYPE: APPLICATION/MAC-BINHEX40"u8))
        {
            type |= ArticleType.BinHex | ArticleType.Mime;
        }
        else if (StartsWithFolded(line, "CONTENT-TYPE: APPLICATION/OCTET-STREAM"u8))
        {
            type |= ArticleType.Binary | ArticleType.Mime;
        }
        else if (StartsWithFolded(line, "CONTENT-TYPE: MESSAGE/PARTIAL"u8))
        {
            type |= ArticleType.Partial | ArticleType.Mime;
        }
        else if (StartsWithFolded(line, "CONTENT-TYPE:"u8))
        {
            type |= ArticleType.Mime;
        }

        if (StartsWithFolded(line, "CONTENT-TRANSFER-ENCODING: BASE64"u8))
        {
            type |= ArticleType.Base64 | ArticleType.Binary;
        }
        else if (StartsWithFolded(line, "CONTENT-TRANSFER-ENCODING: X-BOMMANEWS"u8))
        {
            type |= ArticleType.BommaNews | ArticleType.Binary;
        }
        else if (StartsWithFolded(line, "CONTENT-TRANSFER-ENCODING: X-UNIDATAENCODING"u8))
        {
            type |= ArticleType.UniData | ArticleType.Binary;
        }

        if (inHeader)
        {
            if (StartsWithFolded(line, "CONTROL: CANCEL "u8) || StartsWithFolded(line, "CONTROL: CANCEL\t"u8))
            {
                type |= ArticleType.Control | ArticleType.Cancel;
            }
            else if (StartsWithFolded(line, "CONTROL:"u8))
            {
                type |= ArticleType.Control;
            }

            if (StartsWithFolded(line, "MIME-VERSION:"u8))
            {
                type |= ArticleType.Mime;
            }
        }
    }

    private static void ObserveBodyMarkers(ReadOnlySpan<byte> line, ref ArticleType type)
    {
        if (StartsWithFolded(line, "-----BEGIN PGP MESSAGE-----"u8))
        {
            type |= ArticleType.PgpMessage;
        }

        if (StartsWithFolded(line, "BEGIN "u8) && line.Length > 6)
        {
            type |= ArticleType.UuEncode | ArticleType.Binary;
        }
    }

    /// <summary>Parses <c>Content-Length</c> as a non-negative destuffed-body size hint, or -1.</summary>
    public static int TryParseContentLength(ReadOnlySpan<byte> headerLine)
    {
        if (!StartsWithFolded(headerLine, "CONTENT-LENGTH:"u8))
        {
            return -1;
        }

        return TryParseNonNegativeInt(headerLine["CONTENT-LENGTH:".Length..]);
    }

    /// <summary>
    /// Parses yEnc <c>size=</c> from a destuffed <c>=ybegin</c> line.
    /// That value is the original/decoded size, not wire bytes.
    /// </summary>
    public static int TryParseYencSize(ReadOnlySpan<byte> line)
    {
        if (line.Length < 8 || line[0] != (byte)'=' || !StartsWithFolded(line, "=YBEGIN"u8))
        {
            return -1;
        }

        var sizeToken = " SIZE="u8;
        for (var i = 0; i + sizeToken.Length < line.Length; i++)
        {
            if (!StartsWithFolded(line[i..], sizeToken))
            {
                continue;
            }

            return TryParseNonNegativeInt(line[(i + sizeToken.Length)..]);
        }

        return -1;
    }

    /// <summary>Returns whether <paramref name="line"/> is a destuffed yEnc begin marker.</summary>
    public static bool IsYencBegin(ReadOnlySpan<byte> line) =>
        line.Length >= 8 && line[0] == (byte)'=' && StartsWithFolded(line, "=YBEGIN"u8);

    internal static bool StartsWithFolded(ReadOnlySpan<byte> value, ReadOnlySpan<byte> upperAscii)
    {
        if (value.Length < upperAscii.Length)
        {
            return false;
        }

        for (var i = 0; i < upperAscii.Length; i++)
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

    private static int TryParseNonNegativeInt(ReadOnlySpan<byte> text)
    {
        var i = 0;
        while (i < text.Length && (text[i] == (byte)' ' || text[i] == (byte)'\t'))
        {
            i++;
        }

        if (i >= text.Length || text[i] < (byte)'0' || text[i] > (byte)'9')
        {
            return -1;
        }

        var value = 0;
        while (i < text.Length && text[i] >= (byte)'0' && text[i] <= (byte)'9')
        {
            var digit = text[i] - (byte)'0';
            if (value > (int.MaxValue - digit) / 10)
            {
                return -1;
            }

            value = (value * 10) + digit;
            i++;
        }

        return value;
    }
}
