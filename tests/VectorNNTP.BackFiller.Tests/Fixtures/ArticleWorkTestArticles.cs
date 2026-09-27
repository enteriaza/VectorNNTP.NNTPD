using System.Text;
using VectorNNTP.Common.Articles.YEnc;

namespace VectorNNTP.BackFiller.Tests.Fixtures;

/// <summary>
/// Destuffed article payloads that satisfy the Common parser and match Canonical Article Work requests.
/// </summary>
internal static class ArticleWorkTestArticles
{
    internal const string ValidDate = "Fri, 23 Aug 2024 07:30:10 +0000";
    internal const string CanonicalUtcDate = "Fri, 23 Aug 2024 07:30:10 +0000";

    internal static byte[] Valid(
        string messageId = ArticleWorkTestDeliveries.CanonicalMessageId,
        string body = "body\r\n",
        string? extraHeaders = null)
    {
        var builder = new StringBuilder(256 + body.Length);
        _ = builder.Append("Date: ").Append(ValidDate).Append("\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        if (extraHeaders is not null)
        {
            _ = builder.Append(extraHeaders);
        }

        _ = builder.Append("\r\n").Append(body);
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    internal static byte[] ValidYEnc(string messageId = ArticleWorkTestDeliveries.CanonicalMessageId)
    {
        var decoded = new byte[] { 0x41 };
        var crc = YEncCrc32.Compute(decoded);
        var encoded = unchecked((byte)(decoded[0] + 42));
        var body = $"=ybegin line=128 size=1 name=t.bin\r\n{(char)encoded}\r\n=yend size=1 crc32={crc:x8}\r\n";
        return Valid(messageId, body);
    }

    internal static byte[] InvalidDate(string messageId = ArticleWorkTestDeliveries.CanonicalMessageId)
        => Valid(messageId, extraHeaders: null).ReplaceDate("INVALID");

    internal static byte[] InvalidMessageId()
        => Encoding.ASCII.GetBytes(
            $"Date: {ValidDate}\r\nMessage-ID: malformed-id\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\n\r\nbody\r\n");

    internal static byte[] InvalidNewsgroups(string messageId = ArticleWorkTestDeliveries.CanonicalMessageId)
        => Encoding.ASCII.GetBytes(
            $"Date: {ValidDate}\r\nMessage-ID: {messageId}\r\nNewsgroups: \r\nFrom: user@example.test\r\n\r\nbody\r\n");

    internal static byte[] InvalidHeaders(string messageId = ArticleWorkTestDeliveries.CanonicalMessageId)
        => Encoding.ASCII.GetBytes(
            $"Date: {ValidDate}\r\nMessage-ID: {messageId}\r\nNewsgroups: alt.test\r\nFrom: \r\n\r\nbody\r\n");

    internal static byte[] InvalidPath(string messageId = ArticleWorkTestDeliveries.CanonicalMessageId)
        => Valid(messageId, extraHeaders: "Path: foo bar\r\n");

    internal static byte[] BadYEncCrc(string messageId = ArticleWorkTestDeliveries.CanonicalMessageId)
        => Valid(messageId, "=ybegin line=128 size=1 name=t.bin\r\nk\r\n=yend size=1 crc32=00000000\r\n");

    internal static byte[] InvalidYEncEscape(string messageId = ArticleWorkTestDeliveries.CanonicalMessageId)
        => Valid(messageId, "=ybegin line=128 size=1 name=t.bin\r\n=\r\n=yend size=1 crc32=00000000\r\n");

    internal static byte[] OversizedForOneMegabyteRetention(string messageId = ArticleWorkTestDeliveries.CanonicalMessageId)
    {
        const int target = (1024 * 1024) + 1;
        var body = new char[target];
        Array.Fill(body, 'x');
        for (var i = 100; i + 1 < body.Length; i += 102)
        {
            body[i] = '\r';
            body[i + 1] = '\n';
        }

        body[^2] = '\r';
        body[^1] = '\n';
        return Valid(messageId, new string(body));
    }

    internal static byte[] PathRewriteExceedsLineLimit(string localFqdn, string messageId = ArticleWorkTestDeliveries.CanonicalMessageId)
    {
        var prefixLength = localFqdn.Length + 1;
        var pathValueAt1025 = 1025 - "Path: ".Length;
        var upstream = new string('x', pathValueAt1025 - prefixLength);
        return Valid(messageId, extraHeaders: $"Path: {upstream}\r\n");
    }

    internal static string ArticleResponse(byte[] destuffed)
        => "220 follows\r\n" + Encoding.ASCII.GetString(destuffed) + "\r\n.\r\n";

    private static byte[] ReplaceDate(this byte[] article, string dateValue)
    {
        var text = Encoding.ASCII.GetString(article);
        var replaced = text.Replace($"Date: {ValidDate}", $"Date: {dateValue}", StringComparison.Ordinal);
        return Encoding.ASCII.GetBytes(replaced);
    }
}
