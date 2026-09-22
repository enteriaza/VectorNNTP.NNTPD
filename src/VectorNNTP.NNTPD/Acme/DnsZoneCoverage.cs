namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Ensures every certificate identity falls under the configured DNS apex (<c>DnsSuffix</c>).
/// </summary>
public static class DnsZoneCoverage
{
    /// <summary>Normalizes a DNS hostname (lowercase, no trailing dots).</summary>
    public static string NormalizeDnsHostname(string name, string settingName = "dns_name")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var cleaned = name.Trim().TrimEnd('.').ToLowerInvariant();
        if (cleaned.Length == 0)
        {
            throw new AcmeConfigurationException("invalid_dns_name", $"{settingName} empty");
        }

        if (cleaned.Contains('*', StringComparison.Ordinal))
        {
            throw new AcmeConfigurationException("invalid_dns_name", $"{settingName} wildcard");
        }

        var labels = cleaned.Split('.');
        if (labels.Any(static label => label.Length == 0))
        {
            throw new AcmeConfigurationException("invalid_dns_name", $"{settingName} empty_label");
        }

        return cleaned;
    }

    /// <summary>
    /// Returns whether <paramref name="hostname"/> is <paramref name="zoneApex"/> or a subdomain thereof
    /// (label-boundary aware).
    /// </summary>
    public static bool ZoneCoversHostname(string zoneApex, string hostname)
    {
        var zone = NormalizeDnsHostname(zoneApex, "zone_apex");
        var host = NormalizeDnsHostname(hostname, "hostname");
        return host == zone || host.EndsWith("." + zone, StringComparison.Ordinal);
    }

    /// <summary>Fails closed unless every identity is inside <paramref name="zoneApex"/>.</summary>
    public static void RequireIdentitiesInDnsZone(IReadOnlyList<string> identities, string zoneApex)
    {
        ArgumentNullException.ThrowIfNull(identities);

        var apex = NormalizeDnsHostname(zoneApex, "DNSSuffix");
        if (identities.Count == 0)
        {
            throw new AcmeConfigurationException("missing_identities", "no certificate identities");
        }

        foreach (var raw in identities)
        {
            var host = NormalizeDnsHostname(raw, "certificate_identity");
            if (!ZoneCoversHostname(apex, host))
            {
                throw new AcmeConfigurationException(
                    "identity_outside_zone",
                    $"{host} not under DNSSuffix={apex}");
            }
        }
    }
}
