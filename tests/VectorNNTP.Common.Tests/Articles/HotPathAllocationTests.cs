using VectorNNTP.Common.Articles.DateParser;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Articles.Processing;
using VectorNNTP.Common.Articles.Validation;
using VectorNNTP.Common.Articles.YEnc;

namespace VectorNNTP.Common.Tests.Articles;

public sealed class HotPathAllocationTests
{
    [Fact]
    public void Line_scanner_hex_crc_unfold_message_id_and_printable_do_not_allocate_after_warmup()
    {
        var body = "preamble\r\n=ybegin line=128 size=3\r\n=yend size=3 crc32=ABCDEF12\r\n"u8.ToArray();
        var hex = "ABCDEF12"u8.ToArray();
        var crcPayload = "123456789"u8.ToArray();
        var rawHeader = "Sat, 26 Sep\r\n 2026 12:00:00 +0000"u8.ToArray();
        var messageId = "<part.one+tag@news-server.example.net>"u8.ToArray();
        var date = "Sat, 26 Sep 2026 12:00:00 +0000"u8.ToArray();
        Span<byte> unfoldDest = stackalloc byte[64];

        Warm(body, hex, crcPayload, rawHeader, messageId, date, unfoldDest);

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = ArticleLineScanner.IndexOfCrLf(body, 0);
        _ = ArticleLineScanner.FindLineStartingWith(body, 0, "=yend "u8);
        _ = HexUInt32Parser.TryParseHexUInt32(hex, out _);
        _ = YEncCrc32.Compute(crcPayload);
        _ = NntpArticleHeaderValueUnfolder.TryUnfold(rawHeader, unfoldDest, out _);
        _ = NntpMessageIdValidation.IsValidMessageId(messageId);
        _ = PrintableAsciiSimd.IsAllPrintableAscii(date);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Message_id_char_span_does_not_allocate_after_warmup()
    {
        const string messageId = "<part.one+tag@news-server.example.net>";
        _ = NntpMessageIdValidation.IsValidMessageId(messageId.AsSpan());

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = NntpMessageIdValidation.IsValidMessageId(messageId.AsSpan());
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Representative_valid_article_parse_does_not_allocate_after_warmup()
    {
        var parser = new NntpArticleParser("bf01.usenet.ninja"u8);
        var article = "Date: Fri, 23 Aug 2024 07:30:10 +0200\r\nMessage-ID: <m1@example.test>\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: hello\r\n\r\nThis is text.\r\n"u8.ToArray();

        _ = parser.Parse(article);
        _ = parser.Parse(article);

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = parser.Parse(article);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Malformed_article_parse_does_not_allocate_after_warmup()
    {
        var parser = new NntpArticleParser("bf01.usenet.ninja"u8);
        var missingDate = "Message-ID: <m4@example.test>\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\n\r\nbody\r\n"u8.ToArray();
        var invalidMessageId = "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: malformed-id\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\n\r\nbody\r\n"u8.ToArray();

        _ = parser.Parse(missingDate);
        _ = parser.Parse(invalidMessageId);

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = parser.Parse(missingDate);
        _ = parser.Parse(invalidMessageId);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void News_date_parser_does_not_allocate_after_warmup()
    {
        var numeric = "Fri, 23 Aug 2024 07:30:10 +0200"u8.ToArray();
        var gmt = "Fri, 23 Aug 2024 05:30:10 GMT"u8.ToArray();
        Span<byte> formatted = stackalloc byte[40];

        _ = NewsDateParser.TryGetCanonicalUtc(numeric, out var utc, out _);
        _ = NewsDateParser.TryGetCanonicalUtc(gmt, out _, out _);
        _ = NewsDateParser.TryFormatCanonicalRfc5322Utc(utc, formatted, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = NewsDateParser.TryGetCanonicalUtc(numeric, out utc, out _);
        _ = NewsDateParser.TryGetCanonicalUtc(gmt, out _, out _);
        _ = NewsDateParser.TryFormatCanonicalRfc5322Utc(utc, formatted, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Yenc_validation_does_not_allocate_after_warmup()
    {
        var decoded = new byte[] { 4 };
        var crc = YEncCrc32.Compute(decoded);
        var valid = System.Text.Encoding.ASCII.GetBytes(
            $"=ybegin line=128 size=1 name=t.bin\r\n.\r\n=yend size=1 crc32={crc:x8}\r\n");
        var invalidEscape = "=ybegin line=128 size=1 name=t.bin\r\n=\r\n=yend size=1 crc32=00000000\r\n"u8.ToArray();
        var invalidMetadata = "=ybegin line=128 size=abc name=t.bin\r\n.\r\n=yend size=1 crc32=00000000\r\n"u8.ToArray();

        _ = YEncArticleValidator.Validate(valid);
        _ = YEncArticleValidator.Validate(invalidEscape);
        _ = YEncArticleValidator.Validate(invalidMetadata);

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = YEncArticleValidator.Validate(valid);
        _ = YEncArticleValidator.Validate(invalidEscape);
        _ = YEncArticleValidator.Validate(invalidMetadata);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Parser_yenc_validation_path_does_not_allocate_after_warmup()
    {
        var parser = new NntpArticleParser("bf01.usenet.ninja"u8);
        var decoded = new byte[] { 4 };
        var crc = YEncCrc32.Compute(decoded);
        var body = System.Text.Encoding.ASCII.GetBytes(
            $"=ybegin line=128 size=1 name=t.bin\r\n.\r\n=yend size=1 crc32={crc:x8}\r\n");
        var article = System.Text.Encoding.ASCII.GetBytes(
            "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: <m1@example.test>\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\n\r\n");
        var combined = new byte[article.Length + body.Length];
        Buffer.BlockCopy(article, 0, combined, 0, article.Length);
        Buffer.BlockCopy(body, 0, combined, article.Length, body.Length);

        _ = parser.Parse(combined);
        _ = parser.Parse(combined);

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = parser.Parse(combined);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Canonical_materializer_allocates_only_the_output_buffer_after_warmup()
    {
        var parser = new NntpArticleParser("bf01.usenet.ninja"u8);
        var article = "Date: Fri, 23 Aug 2024 07:30:10 +0200\r\nMessage-ID: <m1@example.test>\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: hello\r\n\r\nThis is text.\r\n"u8.ToArray();
        var parse = parser.Parse(article);
        Assert.True(parse.IsAccepted);

        _ = NntpArticleCanonicalMaterializer.Materialize(parse);
        _ = NntpArticleCanonicalMaterializer.Materialize(parse);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = NntpArticleCanonicalMaterializer.Materialize(parse);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(result.IsAccepted);
        Assert.NotNull(result.ArticleBytes);
        Assert.True(allocated >= result.ArticleBytes.Length);
        Assert.True(allocated <= result.ArticleBytes.Length + 64);
    }

    private static void Warm(
        byte[] body,
        byte[] hex,
        byte[] crcPayload,
        byte[] rawHeader,
        byte[] messageId,
        byte[] date,
        Span<byte> unfoldDest)
    {
        _ = ArticleLineScanner.IndexOfCrLf(body, 0);
        _ = ArticleLineScanner.FindLineStartingWith(body, 0, "=yend "u8);
        _ = HexUInt32Parser.TryParseHexUInt32(hex, out _);
        _ = YEncCrc32.Compute(crcPayload);
        _ = NntpArticleHeaderValueUnfolder.TryUnfold(rawHeader, unfoldDest, out _);
        _ = NntpMessageIdValidation.IsValidMessageId(messageId);
        _ = PrintableAsciiSimd.IsAllPrintableAscii(date);
    }
}
