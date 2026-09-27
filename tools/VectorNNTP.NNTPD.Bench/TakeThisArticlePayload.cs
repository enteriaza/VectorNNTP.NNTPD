using System.Globalization;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Pre-built immutable article (headers + body + multiline terminator) that
/// <see cref="ArticleRecordFactory"/> accepts.
/// </summary>
/// <remarks>
/// Body construction matches the previous Python-shaped workload (80-byte CRLF
/// lines, no leading dots, ~750 KiB target body). Headers were expanded so the
/// Common parser/factory can accept the article:
/// Path, Date, Newsgroups, From, Subject, Message-ID.
/// The article Message-ID field is fixed-width so POST can overwrite it in place.
/// TAKETHIS History peeks the command Message-ID, not this header.
/// </remarks>
internal static class TakeThisArticlePayload
{
    public const int DefaultTargetBodyBytes = 750 * 1024;
    public const int LineLength = 80;
    public const string Newsgroups = "misc.test";
    public const string PathHost = "bench.vectornntp.local";
    public const string FromAddress = "benchmark@vectornntp.local";
    public const string Subject = "VectorNNTP benchmark";

    /// <summary>
    /// Placeholder article Message-ID (same width as
    /// <see cref="TakeThisCommandBuffer.FormatMessageId"/>).
    /// </summary>
    public const string StaticMessageId = "t0000000000-00-000000000000@vectornntp.local";

    public const string MessageIdHeaderPrefix = "Message-ID: <";
    public const string MessageIdHeaderSuffix = ">\r\n";

    private static readonly byte[] Pattern = "1234567890abcdefghijklmnopqrstuvwxyz"u8.ToArray();

    /// <summary>Builds one article including the terminating <c>CRLF.CRLF</c>.</summary>
    public static byte[] Build(int targetBodyBytes = DefaultTargetBodyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetBodyBytes);

        var line = new byte[LineLength + 2];
        var repeats = (LineLength / Pattern.Length) + 1;
        var filled = 0;
        for (var i = 0; i < repeats && filled < LineLength; i++)
        {
            var copy = Math.Min(Pattern.Length, LineLength - filled);
            Buffer.BlockCopy(Pattern, 0, line, filled, copy);
            filled += copy;
        }

        line[LineLength] = (byte)'\r';
        line[LineLength + 1] = (byte)'\n';

        var lineCount = targetBodyBytes / line.Length;
        if (lineCount <= 0)
        {
            throw new ArgumentException("Article size is too small for one article line.", nameof(targetBodyBytes));
        }

        var body = new byte[line.Length * lineCount];
        for (var i = 0; i < lineCount; i++)
        {
            Buffer.BlockCopy(line, 0, body, i * line.Length, line.Length);
        }

        var date = FormatRfc5322Utc(DateTimeOffset.UtcNow);
        var header = Encoding.ASCII.GetBytes(
            "Path: " + PathHost + "\r\n" +
            "From: " + FromAddress + "\r\n" +
            "Newsgroups: " + Newsgroups + "\r\n" +
            "Subject: " + Subject + "\r\n" +
            "Date: " + date + "\r\n" +
            MessageIdHeaderPrefix + StaticMessageId + MessageIdHeaderSuffix +
            "\r\n");
        var terminator = "\r\n.\r\n"u8.ToArray();

        var article = new byte[header.Length + body.Length + terminator.Length];
        Buffer.BlockCopy(header, 0, article, 0, header.Length);
        Buffer.BlockCopy(body, 0, article, header.Length, body.Length);
        Buffer.BlockCopy(terminator, 0, article, header.Length + body.Length, terminator.Length);

        EnsureFactoryAccepts(article);
        return article;
    }

    /// <summary>Offset of the Message-ID value (including <c>&lt;</c>, excluding <c>&gt;</c>).</summary>
    public static int MessageIdValueOffset(ReadOnlySpan<byte> article)
    {
        var needle = Encoding.ASCII.GetBytes(MessageIdHeaderPrefix);
        var index = article.IndexOf(needle);
        if (index < 0)
        {
            throw new InvalidOperationException("Article is missing the Message-ID header.");
        }

        return index + needle.Length;
    }

    /// <summary>Length of the fixed-width Message-ID value including angle brackets.</summary>
    public static int MessageIdValueLength => StaticMessageId.Length + 2;

    /// <summary>Writes a unique Message-ID of the same width into a mutable article copy.</summary>
    public static void WriteMessageId(Span<byte> article, string messageIdIncludingBrackets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageIdIncludingBrackets);
        if (messageIdIncludingBrackets.Length != MessageIdValueLength)
        {
            throw new ArgumentException(
                $"Message-ID must be {MessageIdValueLength} ASCII characters including brackets.",
                nameof(messageIdIncludingBrackets));
        }

        var offset = MessageIdValueOffset(article);
        var inner = messageIdIncludingBrackets.AsSpan(1, messageIdIncludingBrackets.Length - 2);
        Encoding.ASCII.GetBytes(inner, article[offset..]);
    }

    /// <summary>Body length implied by a built article (excludes headers and terminator).</summary>
    public static int BodyBytes(ReadOnlySpan<byte> article)
    {
        var headerEnd = article.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0)
        {
            throw new InvalidOperationException("Article is missing the header/body separator.");
        }

        var bodyStart = headerEnd + 4;
        var terminator = "\r\n.\r\n"u8;
        if (article.Length < bodyStart + terminator.Length)
        {
            throw new InvalidOperationException("Article is shorter than a framed body.");
        }

        return article.Length - bodyStart - terminator.Length;
    }

    /// <summary>Destuffed bytes without the NNTP terminator (factory input).</summary>
    public static ReadOnlyMemory<byte> DestuffedWithoutTerminator(byte[] article)
    {
        ArgumentNullException.ThrowIfNull(article);
        var terminator = "\r\n.\r\n"u8;
        if (article.Length < terminator.Length
            || !article.AsSpan()[^terminator.Length..].SequenceEqual(terminator))
        {
            throw new InvalidOperationException("Article is missing the multiline terminator.");
        }

        return article.AsMemory(0, article.Length - terminator.Length);
    }

    /// <summary>RFC 5322 UTC date accepted by POST <c>PostRfcDate</c> and Common <c>NewsDateParser</c>.</summary>
    public static string FormatRfc5322Utc(DateTimeOffset utc)
    {
        var value = utc.ToUniversalTime();
        return value.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture);
    }

    private static void EnsureFactoryAccepts(byte[] article)
    {
        var destuffed = DestuffedWithoutTerminator(article);
        var created = ArticleRecordFactory.TryCreate(new NntpArticleParser("nntpd01.usenet.ninja"), destuffed);
        if (!created.IsAccepted)
        {
            throw new InvalidOperationException(
                "Benchmark article was rejected by ArticleRecordFactory: "
                + (created.ParseFailure != NntpArticleParseFailureCode.None
                    ? created.ParseFailure.ToString()
                    : created.MaterializeFailure.ToString()));
        }
    }
}
