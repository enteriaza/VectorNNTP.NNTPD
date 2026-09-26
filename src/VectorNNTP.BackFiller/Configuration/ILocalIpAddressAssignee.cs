using System.Net;
using System.Net.NetworkInformation;

namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Reports whether an IP address is assigned to a local network interface.
/// </summary>
/// <remarks>
/// Used only for bind-address validation. Validation does not bind sockets.
/// Tests inject a deterministic implementation.
/// </remarks>
public interface ILocalIpAddressAssignee
{
    /// <summary>
    /// Returns whether <paramref name="address"/> is present as a unicast address on any local NIC.
    /// </summary>
    /// <param name="address">The address to check.</param>
    /// <returns><see langword="true"/> when the address is locally assigned.</returns>
    bool IsLocallyAssigned(IPAddress address);
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
                if (AddressesMatch(unicast.Address, address))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool AddressesMatch(IPAddress left, IPAddress right)
    {
        if (left.Equals(right))
        {
            return true;
        }

        if (left.AddressFamily == right.AddressFamily
            && left.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return left.GetAddressBytes().AsSpan().SequenceEqual(right.GetAddressBytes());
        }

        return false;
    }
}
