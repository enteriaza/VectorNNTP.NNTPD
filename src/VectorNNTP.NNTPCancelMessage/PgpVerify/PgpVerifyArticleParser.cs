using System.Text;

namespace VectorNNTP.NNTPCancelMessage.PgpVerify;

/// <summary>Splits a CRLF Netnews article into case-sensitive header values and body.</summary>
internal static class PgpVerifyArticleParser
{
    public static ParsedArticle Parse(string article)
    {
        ArgumentException.ThrowIfNullOrEmpty(article);
        var normalized = article.Replace("\r\n", "\n", StringComparison.Ordinal);
        var blank = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        if (blank < 0)
        {
            throw new FormatException("Article has no header/body separator.");
        }

        var headerBlock = normalized[..blank];
        var body = normalized[(blank + 2)..];
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentName = null;
        var currentValue = new StringBuilder();
        var haveHeader = false;
        foreach (var rawLine in headerBlock.Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                continue;
            }

            if (rawLine[0] is ' ' or '\t')
            {
                if (!haveHeader)
                {
                    throw new FormatException("Leading folded header is not valid.");
                }

                var start = 0;
                while (start < rawLine.Length && (rawLine[start] == ' ' || rawLine[start] == '\t'))
                {
                    start++;
                }

                currentValue.Append(' ');
                currentValue.Append(rawLine.AsSpan(start));
                continue;
            }

            FlushHeader(headers, ref haveHeader, ref currentName, currentValue);
            var colon = rawLine.IndexOf(':');
            if (colon <= 0)
            {
                throw new FormatException("Malformed article header.");
            }

            currentName = rawLine[..colon];
            var value = rawLine[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            currentValue.Append(value);
            haveHeader = true;
        }

        FlushHeader(headers, ref haveHeader, ref currentName, currentValue);
        return new ParsedArticle(headers, body);
    }

    internal readonly record struct ParsedArticle(Dictionary<string, string> Headers, string Body);

    private static void FlushHeader(
        Dictionary<string, string> headers,
        ref bool haveHeader,
        ref string? currentName,
        StringBuilder currentValue)
    {
        if (!haveHeader || currentName is null)
        {
            return;
        }

        headers[currentName] = currentValue.ToString();
        currentValue.Clear();
        currentName = null;
        haveHeader = false;
    }
}
