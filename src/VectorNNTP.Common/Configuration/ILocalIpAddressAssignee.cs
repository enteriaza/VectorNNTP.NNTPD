using System.Net;
using System.Net.NetworkInformation;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Reports whether an IP address is assigned to a local network interface and enumerates local unicast addresses.
/// </summary>
public interface ILocalIpAddressAssignee
{
    /// <summary>
    /// Returns whether <paramref name="address"/> is present as a unicast address on any local NIC.
    /// </summary>
    /// <param name="address">The address to check.</param>
    /// <returns><see langword="true"/> when the address is locally assigned.</returns>
    bool IsLocallyAssigned(IPAddress address);

    /// <summary>
    /// Returns all unicast addresses currently assigned to local network interfaces.
    /// </summary>
    /// <remarks>
    /// Used when expanding wildcard bind entries. Callers apply eligibility filters for DNS publication.
    /// </remarks>
    /// <returns>Assigned unicast addresses (may include loopback/link-local; not filtered).</returns>
    IReadOnlyList<IPAddress> GetAssignedUnicastAddresses();
}

/// <summary>
/// Enumerates local network interfaces via <see cref="NetworkInterface"/>.
/// </summary>
public sealed class NetworkInterfaceLocalIpAddressAssignee : ILocalIpAddressAssignee
{
    /// <inheritdoc />
    public bool IsLocallyAssigned(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        foreach (var candidate in GetAssignedUnicastAddresses())
        {
            if (AddressesMatch(candidate, address))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<IPAddress> GetAssignedUnicastAddresses()
    {
        var results = new List<IPAddress>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                results.Add(unicast.Address);
            }
        }

        return results;
    }

    private static bool AddressesMatch(IPAddress left, IPAddress right)
    {
        if (left.Equals(right))
        {
            return true;
        }

        // Compare IPv6 addresses ignoring scope id differences when the bytes match.
        if (left.AddressFamily == right.AddressFamily
            && left.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return left.GetAddressBytes().AsSpan().SequenceEqual(right.GetAddressBytes());
        }

        return false;
    }
}
