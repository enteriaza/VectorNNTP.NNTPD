using System.Net;
using System.Net.Sockets;

namespace VectorNNTP.NNTPD.Networking;

/// <summary>
/// Deduplicated set of eligible IP addresses derived from configured <c>BindAddress</c> entries.
/// </summary>
public sealed class ResolvedBindAddresses
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResolvedBindAddresses"/> class.
    /// </summary>
    /// <param name="addresses">Eligible addresses (already filtered and deduplicated).</param>
    public ResolvedBindAddresses(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var all = new List<IPAddress>();
        var ipv4 = new List<IPAddress>();
        var ipv6 = new List<IPAddress>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var address in addresses)
        {
            ArgumentNullException.ThrowIfNull(address);
            var key = IpAddressEligibility.ToDnsContent(address);
            if (!seen.Add(key))
            {
                continue;
            }

            all.Add(address);
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                ipv4.Add(address);
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                ipv6.Add(address);
            }
        }

        All = all;
        IPv4 = ipv4;
        IPv6 = ipv6;
    }

    /// <summary>Gets all eligible addresses in discovery order (deduplicated).</summary>
    public IReadOnlyList<IPAddress> All { get; }

    /// <summary>Gets eligible IPv4 addresses.</summary>
    public IReadOnlyList<IPAddress> IPv4 { get; }

    /// <summary>Gets eligible IPv6 addresses.</summary>
    public IReadOnlyList<IPAddress> IPv6 { get; }

    /// <summary>Gets a value indicating whether at least one eligible address is present.</summary>
    public bool HasAny => All.Count > 0;
}
