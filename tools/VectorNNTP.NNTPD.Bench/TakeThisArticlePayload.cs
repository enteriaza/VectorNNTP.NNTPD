using System.Text;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Pre-built immutable TAKETHIS article (headers + body + multiline terminator).
/// </summary>
/// <remarks>
/// Matches the Python client's body construction (80-byte CRLF lines, no leading dots).
/// The article <c>Message-ID</c> header is static so the ~768 KiB payload can be reused
/// without reconstruction. The TAKETHIS command line carries the unique Message-ID.
/// </remarks>
internal static class TakeThisArticlePayload
{
    public const int DefaultTargetBodyBytes = 750 * 1024;
    public const int LineLength = 80;
    public const string StaticMessageId = "bench-00-000000000001@vectornntp.local";

    private static readonly byte[] Pattern = "1234567890abcdefghijklmnopqrstuvwxyz"u8.ToArray();

    /// <summary>Build one immutable article including the terminating <c>CRLF.CRLF</c>.</summary>
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

        var header = Encoding.ASCII.GetBytes(
            "From: benchmark@vectornntp.local\r\n" +
            "Subject: TAKETHIS benchmark\r\n" +
            "Message-ID: <" + StaticMessageId + ">\r\n" +
            "\r\n");
        var terminator = "\r\n.\r\n"u8.ToArray();

        var article = new byte[header.Length + body.Length + terminator.Length];
        Buffer.BlockCopy(header, 0, article, 0, header.Length);
        Buffer.BlockCopy(body, 0, article, header.Length, body.Length);
        Buffer.BlockCopy(terminator, 0, article, header.Length + body.Length, terminator.Length);
        return article;
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
}
