using System.Globalization;

namespace VectorNNTP.Common.Articles.DateParser
{
    /// <summary>
    /// Parses raw article date-header values into a canonical UTC instant without heap strings.
    /// </summary>
    /// <remarks>
    /// Input is inspected as bytes. The BCL <see cref="DateTimeOffset"/> parsers require UTF-16,
    /// so accepted printable-ASCII bytes are copied into a stackalloc char buffer. That copy is
    /// not <c>Encoding.GetString</c> and does not allocate.
    /// </remarks>
    public static partial class NewsDateParser
    {
        private const DateTimeStyles ParseStyles =
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        private const string CanonicalFormat = "ddd, dd MMM yyyy HH:mm:ss";

        private static readonly string[] DateFormats =
        [
            "ddd, dd MMM yyyy HH:mm:ss zzz",
            "ddd, d MMM yyyy HH:mm:ss zzz",
            "ddd, dd MMM yyyy H:mm:ss zzz",
            "ddd, d MMM yyyy H:mm:ss zzz",
            "ddd, dd MMM yyyy HH:mm:ss",
            "ddd, d MMM yyyy HH:mm:ss",
            "ddd, dd MMM yyyy H:mm:ss",
            "ddd, d MMM yyyy H:mm:ss",
            "dd MMM yyyy HH:mm:ss zzz",
            "d MMM yyyy HH:mm:ss zzz",
            "dd MMM yyyy H:mm:ss zzz",
            "d MMM yyyy H:mm:ss zzz",
            "dd MMM yyyy HH:mm:ss",
            "d MMM yyyy HH:mm:ss",
            "dd MMM yyyy H:mm:ss",
            "d MMM yyyy H:mm:ss",
            "yyyy-MM-dd HH:mm:ss zzz",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd H:mm:ss",
            "yyyy-MM-ddTHH:mm:ss zzz",
            "yyyy-MM-ddTHH:mm:sszzz",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy/MM/dd H:mm:ss",
            "MM/dd/yyyy HH:mm:ss",
            "MM/dd/yyyy H:mm:ss",
            "dd/MM/yyyy HH:mm:ss",
            "dd/MM/yyyy H:mm:ss",
            "dd MMM yyyy",
            "d MMM yyyy",
            "yyyy-MM-dd",
            "dd/MM/yyyy",
            "MM/dd/yyyy",
            "ddd, dd MMM yy HH:mm:ss zzz",
            "ddd, d MMM yy HH:mm:ss zzz",
            "ddd, dd MMM yy H:mm:ss zzz",
            "ddd, d MMM yy H:mm:ss zzz",
            "ddd, dd MMM yy HH:mm:ss",
            "ddd, d MMM yy HH:mm:ss",
            "ddd, dd MMM yy H:mm:ss",
            "ddd, d MMM yy H:mm:ss",
            "dd MMM yy HH:mm:ss zzz",
            "d MMM yy HH:mm:ss zzz",
            "dd MMM yy H:mm:ss zzz",
            "d MMM yy H:mm:ss zzz",
            "dd MMM yy HH:mm:ss",
            "d MMM yy HH:mm:ss",
            "dd MMM yy H:mm:ss",
            "d MMM yy H:mm:ss",
        ];

        /// <summary>
        /// Tries to parse one NNTP date value using the repository default options.
        /// </summary>
        /// <param name="input">Raw date-header value bytes.</param>
        /// <param name="utc">Canonical UTC instant when parsing succeeds.</param>
        /// <param name="failure">Failure reason when parsing fails.</param>
        /// <returns><see langword="true"/> when <paramref name="input"/> produced a UTC instant.</returns>
        public static bool TryGetCanonicalUtc(ReadOnlySpan<byte> input, out DateTime utc, out DateParseFailureReason failure)
            => TryGetCanonicalUtc(input, DateParseOptions.Default, out utc, out failure);

        /// <summary>
        /// Tries to parse one NNTP date value using explicit normalization options.
        /// </summary>
        /// <param name="input">Raw candidate date value bytes.</param>
        /// <param name="options">Guardrails and normalization toggles applied before exact parsing.</param>
        /// <param name="utc">Canonical UTC instant when parsing succeeds.</param>
        /// <param name="failure">Failure reason describing the stage that rejected <paramref name="input"/>.</param>
        /// <returns><see langword="true"/> when parsing succeeds.</returns>
        public static bool TryGetCanonicalUtc(
            ReadOnlySpan<byte> input,
            DateParseOptions options,
            out DateTime utc,
            out DateParseFailureReason failure)
        {
            utc = default;
            failure = DateParseFailureReason.None;

            var trimmed = TrimAsciiWhitespace(input);
            if (trimmed.IsEmpty)
            {
                failure = DateParseFailureReason.Empty;
                return false;
            }

            if (trimmed.Length > options.MaxInputLength)
            {
                failure = DateParseFailureReason.TooLong;
                return false;
            }

            if (!PrintableAsciiSimd.IsAllPrintableAscii(trimmed))
            {
                failure = DateParseFailureReason.NonPrintableAscii;
                return false;
            }

            Span<char> parseChars = stackalloc char[options.MaxInputLength];
            CopyAsciiBytesToChars(trimmed, parseChars);
            if (TryQuickParse(parseChars[..trimmed.Length], out utc))
            {
                return true;
            }

            Span<byte> working = stackalloc byte[options.MaxInputLength];
            trimmed.CopyTo(working);
            var workingLength = trimmed.Length;
            workingLength = StripTrailingParenthetical(working, workingLength);
            workingLength = TrimAsciiWhitespaceInPlace(working, workingLength);

            if (options.NormalizeInteriorWhitespace)
            {
                workingLength = CollapseInteriorSpaces(working, workingLength);
            }

            if (options.RequireKnownTimezoneAbbreviation
                && TryGetUnknownTrailingAbbreviation(working[..workingLength], out _))
            {
                failure = DateParseFailureReason.UnknownTimezoneAbbreviation;
                return false;
            }

            workingLength = SubstituteTimezoneAbbreviation(working, workingLength, options.MaxInputLength);
            CopyAsciiBytesToChars(working[..workingLength], parseChars);
            if (TryExactParse(parseChars[..workingLength], out utc))
            {
                return true;
            }

            failure = DateParseFailureReason.ParseFailed;
            return false;
        }

        /// <summary>
        /// Writes the canonical UTC RFC 5322 form <c>ddd, dd MMM yyyy HH:mm:ss +0000</c> as ASCII bytes.
        /// </summary>
        /// <param name="utc">Date-time value interpreted as UTC output.</param>
        /// <param name="destination">Destination receiving ASCII bytes.</param>
        /// <param name="bytesWritten">Bytes written on success.</param>
        /// <returns><see langword="true"/> when <paramref name="destination"/> was large enough.</returns>
        public static bool TryFormatCanonicalRfc5322Utc(DateTime utc, Span<byte> destination, out int bytesWritten)
        {
            utc = NormalizeUtcKind(utc);
            Span<char> chars = stackalloc char[32];
            if (!utc.TryFormat(chars, out var charCount, CanonicalFormat, CultureInfo.InvariantCulture))
            {
                bytesWritten = 0;
                return false;
            }

            const string Suffix = " +0000";
            var total = charCount + Suffix.Length;
            if (destination.Length < total)
            {
                bytesWritten = 0;
                return false;
            }

            for (var i = 0; i < charCount; i++)
            {
                destination[i] = (byte)chars[i];
            }

            for (var i = 0; i < Suffix.Length; i++)
            {
                destination[charCount + i] = (byte)Suffix[i];
            }

            bytesWritten = total;
            return true;
        }

        /// <summary>
        /// Normalizes <paramref name="utc"/> so local values convert to UTC and unspecified values are treated as UTC.
        /// </summary>
        /// <param name="utc">Input instant.</param>
        /// <returns>UTC <see cref="DateTime"/>.</returns>
        public static DateTime NormalizeUtcKind(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Local)
            {
                return utc.ToUniversalTime();
            }

            return utc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
                : utc;
        }

        private static bool TryQuickParse(ReadOnlySpan<char> input, out DateTime result)
        {
            if (DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, ParseStyles, out var dto))
            {
                result = dto.UtcDateTime;
                return true;
            }

            result = default;
            return false;
        }

        private static bool TryExactParse(ReadOnlySpan<char> input, out DateTime result)
        {
            if (DateTimeOffset.TryParseExact(input, DateFormats, CultureInfo.InvariantCulture, ParseStyles, out var dto))
            {
                result = dto.UtcDateTime;
                return true;
            }

            result = default;
            return false;
        }

        private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
        {
            var start = 0;
            var end = value.Length;
            while (start < end && IsAsciiWhitespace(value[start]))
            {
                start++;
            }

            while (end > start && IsAsciiWhitespace(value[end - 1]))
            {
                end--;
            }

            return value[start..end];
        }

        private static int TrimAsciiWhitespaceInPlace(Span<byte> buffer, int length)
        {
            var start = 0;
            var end = length;
            while (start < end && IsAsciiWhitespace(buffer[start]))
            {
                start++;
            }

            while (end > start && IsAsciiWhitespace(buffer[end - 1]))
            {
                end--;
            }

            var remaining = end - start;
            if (start > 0 && remaining > 0)
            {
                buffer.Slice(start, remaining).CopyTo(buffer);
            }

            return remaining;
        }

        private static bool IsAsciiWhitespace(byte value) => value is (byte)' ' or (byte)'\t';

        private static void CopyAsciiBytesToChars(ReadOnlySpan<byte> source, Span<char> destination)
        {
            for (var i = 0; i < source.Length; i++)
            {
                destination[i] = (char)source[i];
            }
        }

        private static int CollapseInteriorSpaces(Span<byte> buffer, int length)
        {
            var write = 0;
            var lastWasSpace = false;
            for (var i = 0; i < length; i++)
            {
                var b = buffer[i];
                if (b == (byte)' ')
                {
                    if (lastWasSpace)
                    {
                        continue;
                    }

                    lastWasSpace = true;
                }
                else
                {
                    lastWasSpace = false;
                }

                buffer[write++] = b;
            }

            return write;
        }

        private static int StripTrailingParenthetical(Span<byte> buffer, int length)
        {
            var end = length;
            while (end > 0 && IsAsciiWhitespace(buffer[end - 1]))
            {
                end--;
            }

            if (end == 0 || buffer[end - 1] != (byte)')')
            {
                return length;
            }

            var open = -1;
            for (var i = end - 2; i >= 0; i--)
            {
                if (buffer[i] == (byte)'(')
                {
                    open = i;
                    break;
                }

                if (buffer[i] == (byte)')')
                {
                    return length;
                }
            }

            if (open < 0)
            {
                return length;
            }

            var prefixEnd = open;
            while (prefixEnd > 0 && IsAsciiWhitespace(buffer[prefixEnd - 1]))
            {
                prefixEnd--;
            }

            return prefixEnd;
        }
    }
}
