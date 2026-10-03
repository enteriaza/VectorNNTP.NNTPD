namespace VectorNNTP.Common.Articles
{
    /// <summary>
    /// Deterministic Diablo-style article classification from header and body-prefix bytes.
    /// </summary>
    /// <remarks>
    /// Mapped from Diablo <c>lib/arttype.c</c> <c>ArticleType()</c> / <c>ArtTypeConv()</c>.
    /// Does not decode yEnc, BASE64, or uuencode. Does not scan the whole body when a
    /// header or prefix marker is sufficient. Does not implement Diablo
    /// <c>classifyLineAsTypes()</c> character-table / 8-line confirmation.
    /// </remarks>
    public static class ArticleTypeClassifier
    {
        /// <summary>Maximum destuffed body prefix examined for markers (8 KiB).</summary>
        private const int MaxPrefixBytes = 8 * 1024;

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
        internal static void ObserveLine(ReadOnlySpan<byte> line, bool inHeader, ref ArticleType type)
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

        /// <summary>
        /// Splits <paramref name="block"/> on CRLF and passes each line, without the terminator, to <see cref="ObserveLine"/>.
        /// </summary>
        /// <param name="block">Destuffed header block or body prefix.</param>
        /// <param name="inHeader"><see langword="true"/> while scanning headers. A final fragment with no CRLF is still one line. Lone LF is not a split.</param>
        /// <param name="type">Classification flags updated in place.</param>
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

        /// <summary>
        /// Sets yEnc flags when <paramref name="line"/> starts with <c>=YBEGIN PART=</c> or <c>=YBEGIN LINE=</c> under ASCII case folding.
        /// </summary>
        /// <param name="line">One destuffed line without its CRLF. The match also requires length at least 13 and a leading <c>=</c>.</param>
        /// <param name="type">
        /// <c>=YBEGIN PART=</c> adds <see cref="ArticleType.Binary"/>, <see cref="ArticleType.Partial"/>, and <see cref="ArticleType.YEncoded"/>.
        /// <c>=YBEGIN LINE=</c> adds <see cref="ArticleType.Binary"/> and <see cref="ArticleType.YEncoded"/>.
        /// </param>
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

        /// <summary>
        /// Applies Content-Type, Content-Transfer-Encoding, Control, and MIME-Version prefix matches.
        /// </summary>
        /// <param name="line">One destuffed line. Prefixes are matched with <see cref="StartsWithFolded"/> against uppercase ASCII needles.</param>
        /// <param name="inHeader">Control and <c>MIME-Version:</c> matches are applied only when this is <see langword="true"/>.</param>
        /// <param name="type">
        /// HTML, multipart, PostScript, BinHex, octet-stream, message/partial, and a bare <c>Content-Type:</c> set the corresponding MIME flags.
        /// Base64, X-BommaNews, and X-UniDataEncoding set binary plus their encoding flag.
        /// <c>Control: cancel</c> followed by SP or HTAB sets <see cref="ArticleType.Control"/> and <see cref="ArticleType.Cancel"/>; any other <c>Control:</c> sets <see cref="ArticleType.Control"/>.
        /// </param>
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

        /// <summary>
        /// Sets body-prefix flags for a PGP armor line or a <c>BEGIN </c> line longer than 6 bytes.
        /// </summary>
        /// <param name="line">One destuffed body line.</param>
        /// <param name="type">
        /// <c>-----BEGIN PGP MESSAGE-----</c> adds <see cref="ArticleType.PgpMessage"/>.
        /// <c>BEGIN </c> with length greater than 6 adds <see cref="ArticleType.UuEncode"/> and <see cref="ArticleType.Binary"/>. The mode digits are not checked.
        /// </param>
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

        /// <summary>
        /// Returns whether <paramref name="value"/> begins with <paramref name="upperAscii"/> after folding ASCII <c>a-z</c> to upper case.
        /// </summary>
        /// <param name="value">Line or header bytes.</param>
        /// <param name="upperAscii">Needle already in uppercase ASCII. Bytes outside <c>a-z</c> in <paramref name="value"/> are compared unchanged.</param>
        /// <returns><see langword="false"/> when <paramref name="value"/> is shorter than the needle or any folded byte differs.</returns>
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

        /// <summary>
        /// Parses a non-negative decimal integer after optional leading SP or HTAB.
        /// </summary>
        /// <param name="text">Remainder of a header or <c>size=</c> token. Parsing stops at the first non-digit and does not require the span to end there.</param>
        /// <returns>The integer, or <c>-1</c> when there is no digit or the value would exceed <see cref="int.MaxValue"/>. A leading sign is rejected.</returns>
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
}
