using System.Net;
using System.Net.Sockets;

namespace VectorNNTP.NNTPD.Networking;

/// <summary>
/// Classifies IP addresses for DNS publication from resolved bind addresses.
/// </summary>
/// <remarks>
/// Eligible addresses may be public or private (including RFC1918). Multicast, unspecified,
/// loopback, and link-local addresses are excluded. <see cref="IPAddress.IsIPv6UniqueLocal"/> /
/// site-local uniqueness is not used as a rejection criterion; private routable addresses remain eligible.
/// </remarks>
public static class IpAddressEligibility
{
    /// <summary>
    /// Returns whether <paramref name="address"/> may be published in authoritative DNS for this host.
    /// </summary>
    /// <param name="address">The address to evaluate.</param>
    /// <returns><see langword="true"/> when the address is eligible for DNS publication.</returns>
    public static bool IsEligibleForDns(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6Multicast || address.IsIPv6LinkLocal)
            {
                return false;
            }

            // IPv4-mapped IPv6 is not a distinct bind target for DNS publication.
            if (address.IsIPv4MappedToIPv6)
            {
                return IsEligibleForDns(address.MapToIPv4());
            }

            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        // IPv4 multicast 224.0.0.0/4
        if ((bytes[0] & 0xF0) == 0xE0)
        {
            return false;
        }

        // IPv4 link-local 169.254.0.0/16
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns a canonical string suitable for comparing and writing Cloudflare A/AAAA <c>content</c>.
    /// </summary>
    /// <param name="address">The address to format.</param>
    /// <returns>Canonical IP string without zone/scope identifiers.</returns>
    public static string ToDnsContent(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            var unscope = new IPAddress(address.GetAddressBytes());
            return unscope.ToString();
        }

        return address.ToString();
    }

    /// <summary>
    /// Attempts to parse Cloudflare DNS record content as an IP address.
    /// </summary>
    public static bool TryParseDnsContent(string? content, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return IPAddress.TryParse(content.Trim(), out address!);
    }
}
