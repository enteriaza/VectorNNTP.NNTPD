using System.Net;
using System.Net.Sockets;

namespace VectorNNTP.Common.Networking
{
    /// <summary>
    /// Deduplicated set of eligible IP addresses derived from configured <c>BindAddress</c> entries.
    /// </summary>
    public sealed class ResolvedBindAddresses
    {
        /// <summary>
        /// Copies <paramref name="addresses"/> into IPv4 and IPv6 lists, dropping duplicate DNS forms.
        /// </summary>
        /// <param name="addresses">
        /// Candidate addresses. A <see langword="null"/> entry throws. Eligibility is not rechecked;
        /// duplicates are dropped by <see cref="IpAddressEligibility.ToDnsContent"/>.
        /// </param>
        internal ResolvedBindAddresses(IEnumerable<IPAddress> addresses)
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
        internal IReadOnlyList<IPAddress> All { get; }

        /// <summary>Gets eligible IPv4 addresses.</summary>
        internal IReadOnlyList<IPAddress> IPv4 { get; }

        /// <summary>Gets eligible IPv6 addresses.</summary>
        internal IReadOnlyList<IPAddress> IPv6 { get; }

        /// <summary>Gets a value indicating whether at least one eligible address is present.</summary>
        internal bool HasAny => All.Count > 0;
    }
}
