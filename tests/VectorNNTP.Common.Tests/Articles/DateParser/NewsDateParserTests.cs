using System.Text;
using VectorNNTP.Common.Articles.DateParser;

namespace VectorNNTP.Common.Tests.Articles.DateParser
{
    public sealed class NewsDateParserTests
    {
        [Fact]
        public void TryGetCanonicalUtc_WhenNumericOffsetPresent_ReturnsUtcWithoutFallback()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 07:30:10 +0200"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, 5, 30, 10, DateTimeKind.Utc), utc);
            Assert.Equal("Fri, 23 Aug 2024 05:30:10 +0000", Format(utc));
        }

        [Fact]
        public void TryGetCanonicalUtc_WhenGmtAbbreviationPresent_SubstitutesOffset()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 05:30:10 GMT"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, 5, 30, 10, DateTimeKind.Utc), utc);
        }

        [Fact]
        public void TryGetCanonicalUtc_WhenTrailingCommentPresent_StripsParenthetical()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 05:30:10 +0000 (UTC)"u8,
                out var utc,
                out var failure);

            Assert.True(accepted);
            Assert.Equal(DateParseFailureReason.None, failure);
            Assert.Equal(new DateTime(2024, 8, 23, 5, 30, 10, DateTimeKind.Utc), utc);
        }

        [Fact]
        public void TryGetCanonicalUtc_WhenEmpty_ReturnsEmptyFailure()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc("   "u8, out _, out var failure);
            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.Empty, failure);
        }

        [Fact]
        public void TryGetCanonicalUtc_WhenNonPrintable_ReturnsNonPrintableFailure()
        {
            var accepted = NewsDateParser.TryGetCanonicalUtc("Fri, 23 Aug 2024 05:30:10 +\0"u8, out _, out var failure);
            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.NonPrintableAscii, failure);
        }

        [Fact]
        public void TryGetCanonicalUtc_WhenUnknownAbbreviationRequired_Rejects()
        {
            var options = DateParseOptions.Default with { RequireKnownTimezoneAbbreviation = true };
            var accepted = NewsDateParser.TryGetCanonicalUtc(
                "Fri, 23 Aug 2024 05:30:10 ZZQX"u8,
                options,
                out _,
                out var failure);

            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.UnknownTimezoneAbbreviation, failure);
        }

        [Fact]
        public void TryGetCanonicalUtc_WhenTooLong_Rejects()
        {
            var padded = new byte[DateParseOptions.Default.MaxInputLength + 1];
            padded.AsSpan().Fill((byte)'A');
            var accepted = NewsDateParser.TryGetCanonicalUtc(padded, out _, out var failure);
            Assert.False(accepted);
            Assert.Equal(DateParseFailureReason.TooLong, failure);
        }

        private static string Format(DateTime utc)
        {
            Span<byte> destination = stackalloc byte[40];
            Assert.True(NewsDateParser.TryFormatCanonicalRfc5322Utc(utc, destination, out var written));
            return Encoding.ASCII.GetString(destination[..written]);
        }
    }
}
