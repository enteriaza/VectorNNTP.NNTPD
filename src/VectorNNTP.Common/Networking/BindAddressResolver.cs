using System.Net;
using System.Net.Sockets;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.Common.Networking
{
    /// <summary>
    /// Resolves <see cref="AcmeCloudflareOptions.BindAddress"/> into the eligible IP set used for DNS reconciliation.
    /// </summary>
    /// <remarks>
    /// Wildcard entries expand to eligible unicast addresses from <see cref="ILocalIpAddressAssignee"/>.
    /// Explicit entries contribute only themselves when eligible. Multicast, unspecified, loopback, and
    /// link-local addresses are never included. Private addresses remain eligible and are published
    /// intentionally. This resolver describes the intended listen/DNS address set; NNTP sockets are not
    /// bound by this type.
    /// </remarks>
    internal sealed class BindAddressResolver : IBindAddressResolver
    {
        /// <summary>Source of assigned unicast addresses. Queried only when a wildcard bind entry is present.</summary>
        private readonly ILocalIpAddressAssignee _localIpAddressAssignee;

        /// <summary>Receives skip and resolution-count logs.</summary>
        private readonly ILogger<BindAddressResolver> _logger;

        /// <summary>
        /// Creates a resolver that expands wildcard entries through <paramref name="localIpAddressAssignee"/>.
        /// </summary>
        /// <param name="localIpAddressAssignee">Assigned unicast addresses. Queried only when a wildcard entry is present.</param>
        /// <param name="logger">Receives omitted-address and resolution-count logs.</param>
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
        public ResolvedBindAddresses Resolve(AcmeCloudflareOptions options)
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
                if (AcmeCloudflareOptions.IsBindAddressWildcard(trimmed))
                {
                    localUnicast ??= _localIpAddressAssignee.GetAssignedUnicastAddresses();
                    AppendWildcardAddresses(trimmed, localUnicast, collected);
                    continue;
                }

                if (!IPAddress.TryParse(trimmed, out var explicitAddress))
                {
                    // Configuration validation should already reject this; keep resolver defensive.
                    NetworkingLogMessages.IgnoringNonIpBindAddress(_logger);
                    continue;
                }

                if (!IpAddressEligibility.IsEligibleForDns(explicitAddress))
                {
                    NetworkingLogMessages.BindAddressNotEligibleForDns(
                        _logger,
                        IpAddressEligibility.ToDnsContent(explicitAddress));
                    continue;
                }

                collected.Add(explicitAddress);
            }

            var resolved = new ResolvedBindAddresses(collected);
            NetworkingLogMessages.BindAddressesResolved(
                _logger,
                resolved.All.Count,
                resolved.IPv4.Count,
                resolved.IPv6.Count);

            return resolved;
        }

        /// <summary>
        /// Appends DNS-eligible assigned addresses selected by <paramref name="wildcardEntry"/>.
        /// </summary>
        /// <param name="wildcardEntry">Trimmed wildcard. <c>0.0.0.0</c> selects IPv4, <c>::</c> selects IPv6, and any other wildcard selects both.</param>
        /// <param name="localUnicast">Assigned unicast addresses. Ineligible addresses are skipped.</param>
        /// <param name="collected">Destination list. Duplicates are not removed here.</param>
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
}
