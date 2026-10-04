using System.Text;
using VectorNNTP.NNTPD.Session;

namespace VectorNNTP.NNTPD.NntpDb;

/// <summary>
/// Captures one <c>nntpsharedconfig</c> snapshot for a single article operation.
/// </summary>
internal static class NntpArticlePolicyCapture
{
    /// <summary>
    /// Copies the published snapshot when the session has a catalogue.
    /// Otherwise returns <paramref name="fallbackMaxArticleBytes"/> and a null site name.
    /// </summary>
    /// <param name="session">Session that may hold the catalogue.</param>
    /// <param name="fallbackMaxArticleBytes">Limit used when no snapshot is published on the session.</param>
    /// <param name="maxArticleBytes">Article-size boundary for this operation.</param>
    /// <param name="siteNameUtf8">UTF-8 tracker component, or <see langword="null"/> when the static tracker remains in use.</param>
    internal static void Capture(
        NntpSession session,
        int fallbackMaxArticleBytes,
        out int maxArticleBytes,
        out byte[]? siteNameUtf8)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.SharedConfiguration is { } catalogue
            && catalogue.TryGetArticlePolicy(out var publishedMax, out var siteName))
        {
            maxArticleBytes = publishedMax;
            siteNameUtf8 = Encoding.UTF8.GetBytes(siteName);
            return;
        }

        maxArticleBytes = fallbackMaxArticleBytes;
        siteNameUtf8 = null;
    }
}
