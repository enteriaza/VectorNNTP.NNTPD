using System.Net;
using System.Net.Sockets;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Networking.Listeners;

/// <summary>One listen socket binding plan.</summary>
/// <param name="Address">Local address to bind.</param>
/// <param name="Port">TCP port.</param>
/// <param name="DualMode">
/// When <see langword="true"/> and <see cref="Address"/> is IPv6, the socket also accepts IPv4
/// (mapped) connections.
/// </param>
public readonly record struct ListenBinding(IPAddress Address, int Port, bool DualMode)
{
    /// <summary>Gets the endpoint for bind/listen.</summary>
    public IPEndPoint EndPoint => new(Address, Port);
}

/// <summary>
/// Plans listen bindings from configured <see cref="NntpdOptions.BindAddress"/> entries without
/// duplicating DNS eligibility filtering.
/// </summary>
public static class ListenEndpointPlanner
{
    /// <summary>
    /// Builds a deduplicated set of listen bindings for <paramref name="port"/>.
    /// </summary>
    public static IReadOnlyList<ListenBinding> Plan(IReadOnlyList<string> bindAddressEntries, int port)
    {
        ArgumentNullException.ThrowIfNull(bindAddressEntries);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var hasStar = false;
        var hasIpv4Any = false;
        var hasIpv6Any = false;
        var explicits = new List<IPAddress>();

        foreach (var raw in bindAddressEntries)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var entry = raw.Trim();
            if (entry == "*")
            {
                hasStar = true;
                continue;
            }

            if (!IPAddress.TryParse(entry, out var address))
            {
                continue;
            }

            if (address.Equals(IPAddress.Any))
            {
                hasIpv4Any = true;
                continue;
            }

            if (address.Equals(IPAddress.IPv6Any))
            {
                hasIpv6Any = true;
                continue;
            }

            if (!ContainsAddress(explicits, address))
            {
                explicits.Add(address);
            }
        }

        var result = new List<ListenBinding>();

        if (hasStar)
        {
            // Prefer a single dual-stack IPv6 any socket covering IPv4-mapped clients when supported.
            result.Add(new ListenBinding(IPAddress.IPv6Any, port, DualMode: true));
            return result;
        }

        if (hasIpv4Any && hasIpv6Any)
        {
            result.Add(new ListenBinding(IPAddress.Any, port, DualMode: false));
            result.Add(new ListenBinding(IPAddress.IPv6Any, port, DualMode: false));
        }
        else if (hasIpv4Any)
        {
            result.Add(new ListenBinding(IPAddress.Any, port, DualMode: false));
        }
        else if (hasIpv6Any)
        {
            result.Add(new ListenBinding(IPAddress.IPv6Any, port, DualMode: true));
        }

        foreach (var address in explicits)
        {
            // Skip explicits already covered by a wildcard binding on the same family.
            if (hasStar)
            {
                continue;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork && hasIpv4Any)
            {
                continue;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6 && hasIpv6Any)
            {
                continue;
            }

            result.Add(new ListenBinding(address, port, DualMode: false));
        }

        return result;
    }

    private static bool ContainsAddress(List<IPAddress> addresses, IPAddress candidate)
    {
        foreach (var existing in addresses)
        {
            if (existing.Equals(candidate))
            {
                return true;
            }
        }

        return false;
    }
}
