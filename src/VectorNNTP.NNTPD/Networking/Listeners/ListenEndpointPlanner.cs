using System.Net;
using System.Net.Sockets;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Networking.Listeners;

/// <summary>One listen socket binding plan.</summary>
/// <param name="Address">Local address to bind.</param>
/// <param name="Port">TCP port.</param>
/// <param name="DualMode">
/// When <see langword="true"/> and <see cref="Address"/> is IPv6, the socket also accepts IPv4
/// (mapped) connections (<c>IPV6_V6ONLY = 0</c> / <see cref="Socket.DualMode"/>).
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
/// <remarks>
/// <para>
/// Invariants: no overlapping listeners; <c>*</c> is a single dual-stack IPv6-any socket;
/// <c>::</c> alone is dual-stack and therefore covers IPv4; <c>0.0.0.0</c> with <c>::</c> uses
/// separate single-family sockets (<see cref="ListenBinding.DualMode"/> false) so both wildcards
/// remain meaningful without overlap.
/// </para>
/// </remarks>
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

        // Dual-stack :: covers IPv4 only when we are not also planning a separate IPv4-any socket.
        var dualStackIpv6Any = hasIpv6Any && !hasIpv4Any;

        if (hasIpv4Any && hasIpv6Any)
        {
            // Both family wildcards requested: use two single-family sockets (no DualMode) so
            // coverage does not overlap and each wildcard retains independent meaning.
            result.Add(new ListenBinding(IPAddress.Any, port, DualMode: false));
            result.Add(new ListenBinding(IPAddress.IPv6Any, port, DualMode: false));
        }
        else if (hasIpv4Any)
        {
            result.Add(new ListenBinding(IPAddress.Any, port, DualMode: false));
        }
        else if (dualStackIpv6Any)
        {
            result.Add(new ListenBinding(IPAddress.IPv6Any, port, DualMode: true));
        }

        foreach (var address in explicits)
        {
            if (IsCoveredByPlannedWildcard(address, hasIpv4Any, hasIpv6Any, dualStackIpv6Any))
            {
                continue;
            }

            result.Add(new ListenBinding(address, port, DualMode: false));
        }

        return result;
    }

    private static bool IsCoveredByPlannedWildcard(
        IPAddress address,
        bool hasIpv4Any,
        bool hasIpv6Any,
        bool dualStackIpv6Any)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // IPv4-any or dual-stack :: already accepts all IPv4 destinations on this port.
            return hasIpv4Any || dualStackIpv6Any;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (hasIpv6Any)
            {
                return true;
            }

            // IPv4-mapped IPv6 literals are covered by dual-stack IPv6-any the same as native IPv4.
            if (dualStackIpv6Any && address.IsIPv4MappedToIPv6)
            {
                return true;
            }
        }

        return false;
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
