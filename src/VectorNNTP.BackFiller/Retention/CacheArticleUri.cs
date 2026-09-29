using VectorNNTP.Common.Articles;

namespace VectorNNTP.BackFiller.Retention;

/// <summary>
/// Formats the Article Work Success <c>cache://</c> URI metadata. Not a transfer protocol;
/// NNTPD uses host/port for VATP dialing. The path is the lowercase hexadecimal ArticleId.
/// </summary>
public static class CacheArticleUri
{
    /// <summary>
    /// Formats <c>cache://{fqdn}:{bindPort}/{64-char ArticleId hex}</c> from an existing
    /// <see cref="ArticleId"/>. Does not hash Message-ID.
    /// </summary>
    public static string Create(string fqdn, int bindPort, ArticleId artId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
        ArgumentOutOfRangeException.ThrowIfLessThan(bindPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bindPort, 65535);
        return $"cache://{fqdn}:{bindPort}/{artId.ToLowerHexString()}";
    }

    /// <summary>
    /// Formats <c>cache://{fqdn}:{bindPort}/{articleIdHex}</c> from a retention identity.
    /// </summary>
    public static string Create(string fqdn, int bindPort, ArticleIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
        ArgumentOutOfRangeException.ThrowIfLessThan(bindPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bindPort, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ArticleIdHex);
        if (identity.ArticleIdHex.Length != ArticleIdentity.ArticleIdHexLength)
        {
            throw new ArgumentException(
                $"Article identity ArticleId hex must be {ArticleIdentity.ArticleIdHexLength} lowercase hex characters.",
                nameof(identity));
        }

        return $"cache://{fqdn}:{bindPort}/{identity.ArticleIdHex}";
    }
}
