using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VectorNNTP.Common.Dns
{
    /// <summary>
    /// Discovers authoritative nameserver addresses for a configured DNS zone apex via recursive bootstrap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Product contract:</b> queries NS for the configured zone apex (<c>DnsSuffix</c>) only. There is no
    /// label-walk from challenge hostnames; apex configuration remains authoritative for ACME DNS-01.
    /// </para>
    /// <para>
    /// Bootstrap uses OS-configured or public recursive resolvers (RD=1) solely to obtain apex NS names and
    /// resolve NS hostnames (glue, then recursive wire A/AAAA, then OS stub). Callers must query the returned
    /// addresses directly for TXT with RD=0 (see <see cref="AuthoritativeTxtClient"/>).
    /// </para>
    /// <para>
    /// Supported record types: NS, A, and AAAA only. This type does not resolve TXT and does not expose a
    /// generic <c>ResolveAsync</c> API.
    /// </para>
    /// </remarks>
    internal static class ZoneApexNameserverDiscovery
    {
        private const int UdpTimeoutMs = 5_000;

        private static readonly IPAddress[] RecursiveResolvers = ResolveRecursiveResolvers();

        private static Exception? s_recursiveResolverDiscoveryException;
        private static int s_loggedRecursiveResolverFallback;

        /// <summary>
        /// Resolves distinct authoritative NS addresses for <paramref name="zoneApex"/>.
        /// </summary>
        /// <returns>Distinct NS addresses; empty when discovery fails.</returns>
        internal static async Task<IReadOnlyList<IPAddress>> DiscoverAddressesAsync(
            string zoneApex,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneApex);
            LogRecursiveResolverFallbackIfNeeded(logger);

            string apex = zoneApex.TrimEnd('.');

            foreach (IPAddress resolver in RecursiveResolvers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] query = DnsQueryBuilder.Build(apex, DnsRecordType.Ns, out ushort queryId, recursionDesired: true);
                DnsUdpExchange.Result exchange = await DnsUdpExchange
                    .QueryAsync(resolver, query, UdpTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (exchange.Buffer is null)
                {
                    continue;
                }

                if (!TryParseNsResponse(exchange.Buffer, queryId, out List<string> nsHostnames, out Dictionary<string, List<IPAddress>> glue))
                {
                    continue;
                }

                if (nsHostnames.Count == 0)
                {
                    continue;
                }

                List<IPAddress> result = [];
                foreach (string ns in nsHostnames)
                {
                    if (glue.TryGetValue(NormalizeDnsName(ns), out List<IPAddress>? ips))
                    {
                        foreach (IPAddress ip in ips)
                        {
                            AddUnique(result, ip);
                        }
                    }
                    else
                    {
                        IReadOnlyList<IPAddress> resolved = await ResolveNsHostnameViaWireThenOsAsync(ns, logger, cancellationToken)
                            .ConfigureAwait(false);
                        foreach (IPAddress ip in resolved)
                        {
                            AddUnique(result, ip);
                        }
                    }
                }

                if (result.Count > 0)
                {
                    return result;
                }
            }

            if (logger is not null)
            {
                DnsLogMessages.ZoneApexNsDiscoveryFailed(logger, zoneApex);
            }

            return [];
        }

        private static IPAddress[] ResolveRecursiveResolvers()
        {
            try
            {
                List<IPAddress> servers = [];
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    IPInterfaceProperties props = ni.GetIPProperties();
                    foreach (IPAddress ip in props.DnsAddresses)
                    {
                        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
                            ip.Equals(IPAddress.Loopback) || ip.Equals(IPAddress.IPv6Loopback))
                        {
                            continue;
                        }

                        bool exists = false;
                        for (int i = 0; i < servers.Count; i++)
                        {
                            if (servers[i].Equals(ip))
                            {
                                exists = true;
                                break;
                            }
                        }

                        if (!exists)
                        {
                            servers.Add(ip);
                        }
                    }
                }

                if (servers.Count > 0)
                {
                    return [.. servers];
                }
            }
            catch (Exception ex)
            {
                s_recursiveResolverDiscoveryException = ex;
            }

            return
            [
                IPAddress.Parse("1.1.1.1"),
                IPAddress.Parse("8.8.8.8"),
            ];
        }

        private static void LogRecursiveResolverFallbackIfNeeded(ILogger? logger)
        {
            if (logger is null || s_recursiveResolverDiscoveryException is null)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref s_loggedRecursiveResolverFallback, 1, 0) != 0)
            {
                return;
            }

            Exception ex = s_recursiveResolverDiscoveryException;
            DnsLogMessages.RecursiveResolverDiscoveryFallback(logger, ex.GetType().Name, ex);
        }

        private static void AddUnique(List<IPAddress> list, IPAddress ip)
        {
            foreach (IPAddress existing in list)
            {
                if (existing.Equals(ip))
                {
                    return;
                }
            }

            list.Add(ip);
        }

        private static string NormalizeDnsName(string name)
            => name.TrimEnd('.').ToLowerInvariant();

        private static async Task<IReadOnlyList<IPAddress>> ResolveNsHostnameViaWireThenOsAsync(
            string host,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            List<IPAddress> wire = [];
            foreach (IPAddress resolver in RecursiveResolvers)
            {
                byte[] q4 = DnsQueryBuilder.Build(host, DnsRecordType.A, out ushort id4, recursionDesired: true);
                DnsUdpExchange.Result r4 = await DnsUdpExchange
                    .QueryAsync(resolver, q4, UdpTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (r4.Buffer is not null)
                {
                    CollectAddressAnswers(r4.Buffer, id4, DnsRecordType.A, 4, wire);
                }

                byte[] q6 = DnsQueryBuilder.Build(host, DnsRecordType.Aaaa, out ushort id6, recursionDesired: true);
                DnsUdpExchange.Result r6 = await DnsUdpExchange
                    .QueryAsync(resolver, q6, UdpTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (r6.Buffer is not null)
                {
                    CollectAddressAnswers(r6.Buffer, id6, DnsRecordType.Aaaa, 16, wire);
                }

                if (wire.Count > 0)
                {
                    return wire;
                }
            }

            return await ResolveHostAddressesAsync(host, logger, cancellationToken).ConfigureAwait(false);
        }

        private static void CollectAddressAnswers(
            byte[] buffer,
            ushort expectedId,
            ushort expectedType,
            int expectedRdLength,
            List<IPAddress> dest)
        {
            ReadOnlySpan<byte> span = buffer;
            if (!DnsResponseHeader.TryReadCounts(
                    span,
                    expectedId,
                    out ushort qdCount,
                    out ushort anCount,
                    out _,
                    out _))
            {
                return;
            }

            int offset = DnsWireFormat.HeaderSize;
            if (!DnsResponseHeader.TrySkipQuestions(span, ref offset, qdCount))
            {
                return;
            }

            for (int i = 0; i < anCount; i++)
            {
                if (!TryConsumeResourceRecord(
                        span,
                        ref offset,
                        out _,
                        out ushort rrType,
                        out ushort rrClass,
                        out int rdataStart,
                        out ushort rdLength))
                {
                    return;
                }

                if (rrClass != DnsRecordType.ClassIn || rrType != expectedType || rdLength != expectedRdLength)
                {
                    continue;
                }

                if (rdataStart + rdLength <= span.Length)
                {
                    dest.Add(new IPAddress(span.Slice(rdataStart, rdLength)));
                }
            }
        }

        private static async Task<IReadOnlyList<IPAddress>> ResolveHostAddressesAsync(
            string host,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            try
            {
                IPAddress[] all = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                if (all.Length == 0)
                {
                    return [];
                }

                List<IPAddress> ips = new(all.Length);
                foreach (IPAddress ip in all)
                {
                    if (ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    {
                        ips.Add(ip);
                    }
                }

                return ips;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (logger is not null)
                {
                    DnsLogMessages.NsHostnameOsResolveFailed(logger, host, ex.GetType().Name, ex);
                }

                return [];
            }
        }

        private static bool TryParseNsResponse(
            byte[] buffer,
            ushort expectedId,
            out List<string> nsHostnames,
            out Dictionary<string, List<IPAddress>> glue)
        {
            nsHostnames = [];
            glue = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);
            ReadOnlySpan<byte> span = buffer;

            if (!DnsResponseHeader.TryReadCounts(
                    span,
                    expectedId,
                    out ushort qdCount,
                    out ushort anCount,
                    out ushort authorityCount,
                    out ushort additionalCount))
            {
                return false;
            }

            int offset = DnsWireFormat.HeaderSize;
            if (!DnsResponseHeader.TrySkipQuestions(span, ref offset, qdCount))
            {
                return false;
            }

            ProcessSection(span, ref offset, anCount, nsHostnames, glue);
            ProcessSection(span, ref offset, authorityCount, nsHostnames, glue);
            ProcessSection(span, ref offset, additionalCount, nsHostnames, glue);
            return true;
        }

        private static void ProcessSection(
            ReadOnlySpan<byte> span,
            ref int offset,
            int count,
            List<string> nsHostnames,
            Dictionary<string, List<IPAddress>> glue)
        {
            for (int i = 0; i < count; i++)
            {
                if (!TryConsumeResourceRecord(
                        span,
                        ref offset,
                        out string owner,
                        out ushort rrType,
                        out ushort rrClass,
                        out int rdataStart,
                        out ushort rdLength))
                {
                    return;
                }

                if (rrClass != DnsRecordType.ClassIn)
                {
                    continue;
                }

                if (rrType == DnsRecordType.Ns)
                {
                    int p = rdataStart;
                    if (!DnsNameCodec.TryReadDomainName(span, ref p, out string nsdname) || p != rdataStart + rdLength)
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(nsdname) && !nsHostnames.Contains(nsdname, StringComparer.OrdinalIgnoreCase))
                    {
                        nsHostnames.Add(nsdname);
                    }
                }
                else if (rrType == DnsRecordType.A && rdLength == 4)
                {
                    AddGlue(glue, owner, new IPAddress(span.Slice(rdataStart, rdLength)));
                }
                else if (rrType == DnsRecordType.Aaaa && rdLength == 16)
                {
                    AddGlue(glue, owner, new IPAddress(span.Slice(rdataStart, rdLength)));
                }
            }
        }

        private static void AddGlue(Dictionary<string, List<IPAddress>> glue, string owner, IPAddress ip)
        {
            string key = NormalizeDnsName(owner);
            if (!glue.TryGetValue(key, out List<IPAddress>? list))
            {
                list = [];
                glue[key] = list;
            }

            foreach (IPAddress existing in list)
            {
                if (existing.Equals(ip))
                {
                    return;
                }
            }

            list.Add(ip);
        }

        private static bool TryConsumeResourceRecord(
            ReadOnlySpan<byte> packet,
            ref int offset,
            out string ownerName,
            out ushort rrType,
            out ushort rrClass,
            out int rdataStart,
            out ushort rdLength)
        {
            rrType = 0;
            rrClass = 0;
            rdataStart = 0;
            rdLength = 0;

            if (!DnsNameCodec.TryReadDomainName(packet, ref offset, out ownerName))
            {
                return false;
            }

            if (offset + DnsWireFormat.ResourceRecordFixedFieldsSize > packet.Length)
            {
                return false;
            }

            rrType = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            rrClass = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            rdLength = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
            offset += DnsWireFormat.ResourceRecordFixedFieldsSize;
            rdataStart = offset;
            if (offset + rdLength > packet.Length)
            {
                return false;
            }

            offset += rdLength;
            return true;
        }
    }
}
