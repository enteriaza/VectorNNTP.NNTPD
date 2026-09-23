namespace VectorNNTP.NNTPD.Cloudflare;

/// <summary>
/// Managed A/AAAA DNS attributes enforced by reconciliation.
/// </summary>
public static class CloudflareManagedDnsPolicy
{
    /// <summary>TTL (seconds) required on every managed A/AAAA record.</summary>
    public const int ManagedTtl = 300;

    /// <summary>Proxy flag required on every managed A/AAAA record (DNS-only).</summary>
    public const bool ManagedProxied = false;

    /// <summary>
    /// Returns whether a listed A/AAAA record already matches managed content attributes
    /// (TTL and proxy), given that its content is already the desired address.
    /// </summary>
    public static bool MatchesManagedAttributes(CloudflareDnsRecord record) =>
        record is { Ttl: ManagedTtl, Proxied: ManagedProxied };
}
