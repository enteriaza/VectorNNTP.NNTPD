namespace VectorNNTP.BackFiller.Retention;

/// <summary>
/// Single helper for the Success <c>cache://</c> URI. Used by retention and the future listener.
/// </summary>
public static class CacheArticleUri
{
    /// <summary>
    /// Formats <c>cache://{fqdn}:{bindPort}/{md5}</c> with no encoding of the MD5 path component.
    /// </summary>
    /// <param name="fqdn">Validated canonical BackFiller FQDN.</param>
    /// <param name="bindPort">Validated listener bind port.</param>
    /// <param name="identity">Article identity whose MD5 is the path.</param>
    /// <returns>The cache URI string.</returns>
    public static string Create(string fqdn, int bindPort, ArticleIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
        ArgumentOutOfRangeException.ThrowIfLessThan(bindPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bindPort, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Md5Hex);
        if (identity.Md5Hex.Length != ArticleIdentity.Md5HexLength)
        {
            throw new ArgumentException("Article identity MD5 must be 32 lowercase hex characters.", nameof(identity));
        }

        return $"cache://{fqdn}:{bindPort}/{identity.Md5Hex}";
    }
}
