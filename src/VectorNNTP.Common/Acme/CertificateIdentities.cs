namespace VectorNNTP.NNTPD.Acme;

/// <summary>Deterministic certificate SAN set for NNTPD TLS certificates.</summary>
public static class CertificateIdentities
{
    /// <summary>Shared public news hostname required on every server certificate.</summary>
    public const string NewsHostname = "news.usenet.ninja";

    /// <summary>
    /// Returns the exact DNS SAN set for issuance.
    /// </summary>
    /// <param name="fqdn">Server FQDN (for example <c>nntpd01.usenet.ninja</c>).</param>
    /// <param name="includeNewsHostname">
    /// When <see langword="true"/> (NNTPD), also include <see cref="NewsHostname"/>.
    /// When <see langword="false"/> (BackFiller), request only <paramref name="fqdn"/>.
    /// </param>
    /// <returns>Lowercased, deduplicated identities.</returns>
    public static IReadOnlyList<string> ForFqdn(string fqdn, bool includeNewsHostname = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);

        var cleanedFqdn = fqdn.Trim().TrimEnd('.').ToLowerInvariant();
        if (cleanedFqdn.Length == 0)
        {
            throw new ArgumentException("fqdn must be a non-empty DNS name.", nameof(fqdn));
        }

        if (cleanedFqdn.Contains('*', StringComparison.Ordinal))
        {
            throw new ArgumentException("wildcard identities are not permitted.", nameof(fqdn));
        }

        var ordered = new List<string>(2);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (seen.Add(cleanedFqdn))
        {
            ordered.Add(cleanedFqdn);
        }

        if (includeNewsHostname)
        {
            var news = NewsHostname.ToLowerInvariant();
            if (seen.Add(news))
            {
                ordered.Add(news);
            }

            if (!seen.Contains(cleanedFqdn) || !seen.Contains(news))
            {
                throw new InvalidOperationException("required certificate identities missing after normalization.");
            }
        }
        else if (!seen.Contains(cleanedFqdn))
        {
            throw new InvalidOperationException("required certificate identity missing after normalization.");
        }

        return ordered;
    }
}
