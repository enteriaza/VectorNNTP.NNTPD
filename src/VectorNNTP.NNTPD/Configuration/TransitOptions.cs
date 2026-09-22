namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Peer authorization for NNTP transit/streaming feeds.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AllowedPeers"/> lists effective client IP addresses trusted as transit peers.
/// Matching peers receive <c>AuthorizedTransit</c> and <c>StreamingPermitted</c> without
/// interactive authentication. This is peer authorization, not user authentication.
/// </para>
/// <para>
/// Default is empty (deny-by-default). Does not grant reader access, posting, or
/// <c>IsAuthenticated</c>. Distinct from <c>ProxyHosts</c> (HAProxy PROXY trust only).
/// Literal IPv4/IPv6 only (no CIDR/DNS in this version).
/// </para>
/// </remarks>
public sealed class TransitOptions
{
    /// <summary>
    /// Gets or sets effective client IP addresses authorized as transit/streaming peers.
    /// </summary>
    /// <remarks>
    /// Matched against <c>ConnectionClientIdentity.ClientAddress</c> (PROXY effective client
    /// when applicable). Empty by default.
    /// </remarks>
    public string[] AllowedPeers { get; set; } = [];
}
