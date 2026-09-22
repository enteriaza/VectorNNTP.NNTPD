namespace VectorNNTP.NNTPD.Acme;

/// <summary>Deterministic certificate SAN set for NNTPD TLS certificates.</summary>
public static class CertificateIdentities
{
    /// <summary>Shared public news hostname required on every server certificate.</summary>
    public const string NewsHostname = "news.usenet.ninja";

    /// <summary>
    /// Returns the exact DNS SAN set for issuance: <paramref name="fqdn"/> and <see cref="NewsHostname"/>.
    /// </summary>
    /// <param name="fqdn">Server FQDN (for example <c>nntpd01.usenet.ninja</c>).</param>
    /// <returns>Lowercased, deduplicated identities preserving the required pair.</returns>
    public static IReadOnlyList<string> ForFqdn(string fqdn)
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

        var news = NewsHostname.ToLowerInvariant();
        var ordered = new List<string>(2);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { cleanedFqdn, news })
        {
            if (seen.Add(name))
            {
                ordered.Add(name);
            }
        }

        if (!seen.Contains(cleanedFqdn) || !seen.Contains(news))
        {
            throw new InvalidOperationException("required certificate identities missing after normalization.");
        }

        return ordered;
    }
}
