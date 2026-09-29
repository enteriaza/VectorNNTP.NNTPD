using System.Text.RegularExpressions;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.NNTPD.Transport.Vatp;

/// <summary>
/// Parses RabbitMQ Success <c>cache://</c> URIs into host, port, and ArticleId path components.
/// Host/port are used for VATP dialing; the path is the lowercase hexadecimal ArticleId and is
/// not a VATP lookup key.
/// </summary>
public static partial class CacheArticleUriParser
{
    /// <summary>Parsed cache URI components.</summary>
    public readonly record struct ParsedCacheArticleUri(string Host, int Port, string ArticleIdHex);

    /// <summary>
    /// Attempts to parse <c>cache://{fqdn}:{port}/{64 hex ArticleId}</c>.
    /// </summary>
    public static bool TryParse(string cacheUri, out ParsedCacheArticleUri parsed, out string error)
    {
        parsed = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(cacheUri))
        {
            error = "Cache URI is required.";
            return false;
        }

        if (!CanonicalCacheUriRegex().IsMatch(cacheUri))
        {
            error = "Cache URI is not in the canonical cache:// form.";
            return false;
        }

        var withoutScheme = cacheUri.AsSpan("cache://".Length);
        var slash = withoutScheme.IndexOf('/');
        if (slash <= 0)
        {
            error = "Cache URI path is missing.";
            return false;
        }

        var authority = withoutScheme[..slash];
        var articleIdHex = withoutScheme[(slash + 1)..].ToString();
        if (!ArticleId.TryParseLowerHex(articleIdHex, out _))
        {
            error = "Cache URI path is not a lowercase hexadecimal ArticleId.";
            return false;
        }

        var colon = authority.LastIndexOf(':');
        if (colon <= 0 || colon >= authority.Length - 1)
        {
            error = "Cache URI host or port is invalid.";
            return false;
        }

        var host = authority[..colon].ToString();
        if (!int.TryParse(authority[(colon + 1)..], out var port) || port is < 1 or > 65535)
        {
            error = "Cache URI port is out of range.";
            return false;
        }

        parsed = new ParsedCacheArticleUri(host, port, articleIdHex);
        return true;
    }

    [GeneratedRegex(
        "^cache://(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?:(?:6553[0-5]|655[0-2][0-9]|65[0-4][0-9]{2}|6[0-4][0-9]{3}|[1-5][0-9]{4}|[1-9][0-9]{0,3})/[0-9a-f]{64}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalCacheUriRegex();
}
