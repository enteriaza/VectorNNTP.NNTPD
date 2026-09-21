using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Networking;

/// <summary>
/// Resolves <see cref="NntpdOptions.BindAddress"/> into the eligible IP set used for DNS reconciliation.
/// </summary>
/// <remarks>
/// Wildcard entries expand to eligible unicast addresses from <see cref="ILocalIpAddressAssignee"/>.
/// Explicit entries contribute only themselves when eligible. Multicast, unspecified, loopback, and
/// link-local addresses are never included. Private addresses remain eligible and are published
/// intentionally. This resolver describes the intended listen/DNS address set; NNTP sockets are not
/// bound by this type.
/// </remarks>
public sealed class BindAddressResolver : IBindAddressResolver
{
    private readonly ILocalIpAddressAssignee _localIpAddressAssignee;
    private readonly ILogger<BindAddressResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BindAddressResolver"/> class.
    /// </summary>
    public BindAddressResolver(
        ILocalIpAddressAssignee localIpAddressAssignee,
        ILogger<BindAddressResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(localIpAddressAssignee);
        ArgumentNullException.ThrowIfNull(logger);
        _localIpAddressAssignee = localIpAddressAssignee;
        _logger = logger;
    }

    /// <inheritdoc />
    public ResolvedBindAddresses Resolve(NntpdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var bindEntries = options.BindAddress ?? [];
        var collected = new List<IPAddress>();

        IReadOnlyList<IPAddress>? localUnicast = null;

        foreach (var entry in bindEntries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var trimmed = entry.Trim();
            if (NntpdOptions.IsBindAddressWildcard(trimmed))
            {
                localUnicast ??= _localIpAddressAssignee.GetAssignedUnicastAddresses();
                AppendWildcardAddresses(trimmed, localUnicast, collected);
                continue;
            }

            if (!IPAddress.TryParse(trimmed, out var explicitAddress))
            {
                // Configuration validation should already reject this; keep resolver defensive.
                _logger.LogWarning(
                    "Ignoring non-IP BindAddress entry during resolution (validation should have failed earlier).");
                continue;
            }

            if (!IpAddressEligibility.IsEligibleForDns(explicitAddress))
            {
                _logger.LogInformation(
                    "BindAddress entry {Address} is not eligible for DNS publication and will be omitted from the reconciled set.",
                    IpAddressEligibility.ToDnsContent(explicitAddress));
                continue;
            }

            collected.Add(explicitAddress);
        }

        var resolved = new ResolvedBindAddresses(collected);
        _logger.LogInformation(
            "Resolved {TotalCount} eligible bind address(es) for DNS ({IPv4Count} IPv4, {IPv6Count} IPv6).",
            resolved.All.Count,
            resolved.IPv4.Count,
            resolved.IPv6.Count);

        return resolved;
    }

    private static void AppendWildcardAddresses(
        string wildcardEntry,
        IReadOnlyList<IPAddress> localUnicast,
        List<IPAddress> collected)
    {
        var includeV4 = true;
        var includeV6 = true;

        if (IPAddress.TryParse(wildcardEntry, out var wildcardAddress))
        {
            if (wildcardAddress.Equals(IPAddress.Any))
            {
                includeV6 = false;
            }
            else if (wildcardAddress.Equals(IPAddress.IPv6Any))
            {
                includeV4 = false;
            }
        }

        foreach (var address in localUnicast)
        {
            if (!IpAddressEligibility.IsEligibleForDns(address))
            {
                continue;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork && includeV4)
            {
                collected.Add(address);
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6 && includeV6)
            {
                collected.Add(address);
            }
        }
    }
}
