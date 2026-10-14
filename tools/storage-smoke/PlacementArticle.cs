using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.StorageSmoke;

/// <summary>
/// Builds one pre-ingestion article for direct VATP placement.
/// <see cref="ArticlePathMode.Normalize"/> inserts <see cref="SmokeLimits.TrackerHost"/> and does not
/// prepend an application hop. StorageServer's own traverse is what adds the cache hop later.
/// </summary>
internal static class PlacementArticle
{
    private const string DateHeader = "Tue, 13 Oct 2026 21:16:00 +0000";

    internal static byte[] CreateBody(int bodyBytes)
    {
        var body = new byte[bodyBytes];
        for (var offset = 0; offset < body.Length; offset += 80)
        {
            var take = Math.Min(78, body.Length - offset);
            body.AsSpan(offset, take).Fill((byte)'x');
            if (take == 78)
            {
                body[offset + 78] = (byte)'\r';
                body[offset + 79] = (byte)'\n';
            }
        }

        return body;
    }

    internal static bool TryCreate(ReadOnlySpan<byte> body, string messageIdToken, out ArticleRecord record, out string failure)
    {
        var header = Encoding.ASCII.GetBytes(
            "Path: " + SmokeLimits.SourcePath + "\r\n" +
            "Date: " + DateHeader + "\r\n" +
            "Message-ID: <" + messageIdToken + "@soak.invalid>\r\n" +
            "Newsgroups: alt.test\r\n" +
            "From: soak@soak.invalid\r\n" +
            "Subject: storage-smoke\r\n" +
            "\r\n");
        var article = new byte[header.Length + body.Length];
        header.CopyTo(article, 0);
        body.CopyTo(article.AsSpan(header.Length));
        var parser = new NntpArticleParser(SmokeLimits.TrackerHost);
        var created = ArticleRecordFactory.TryCreate(parser, article, ArticlePathMode.Normalize);
        if (!created.IsAccepted)
        {
            record = default;
            failure = "parse=" + created.ParseFailure + " materialize=" + created.MaterializeFailure;
            return false;
        }

        record = created.Record;
        failure = string.Empty;
        return true;
    }
}
