// <copyright file="DnsWireNameReader.cs" company="Usenet Ninja">
// Copyright (c) Chris Knipe &lt;cknipe@opticnetworks.net&gt;. Licensed under the Apache License, Version 2.0 (see LICENSE).
// </copyright>
// HOT PATH: no LINQ; no allocations on success; no closures; no boxing; prefer Span/stackalloc; throws extracted + NoInlining where documented.
// DnsWireNameReader.cs -- Reads a DNS domain name from a wire-format packet (RFC 1035), following compression pointers.

namespace VectorNNTP.NNTPD.Dns.Wire
{
    /// <summary>
    /// Reads a DNS domain name from a wire-format packet (RFC 1035), following compression pointers.
    /// </summary>
    public static class DnsWireNameReader
    {
        /// <summary>
        /// Maximum compression-pointer hops followed before treating a name as malformed.
        /// </summary>
        private const int MaxPointerHops = 128;

        /// <summary>
        /// Maximum expanded wire name length in bytes per RFC 1035.
        /// </summary>
        private const int MaxExpandedNameLengthBytes = 255;

        /// <summary>
        /// Reads a domain name starting at <paramref name="offset"/>; updates <paramref name="offset"/> to the first byte
        /// after the name encoding (after the root label or compression jump return).
        /// </summary>
        /// <param name="packet">DNS packet bytes.</param>
        /// <param name="offset">Offset to start reading from; updated on success.</param>
        /// <param name="name">Decoded dotted name on success; empty string for the root label.</param>
        /// <returns><see langword="true"/> on success; <see langword="false"/> on malformed encoding.</returns>
        public static bool TryReadDomainName(ReadOnlySpan<byte> packet, ref int offset, out string name)
        {
            name = string.Empty;

            List<string>? labels = null;
            bool jumped = false;
            int jumpBack = 0;
            int pos = offset;
            int expandedBytes = 0;

            for (int hop = 0; hop < MaxPointerHops; hop++)
            {
                if (pos >= packet.Length)
                {
                    return false;
                }

                byte len = packet[pos];
                if ((len & 0xC0) == 0xC0)
                {
                    if (pos + 1 >= packet.Length)
                    {
                        return false;
                    }

                    if (!jumped)
                    {
                        jumped = true;
                        jumpBack = pos + 2;
                    }

                    pos = ((len & 0x3F) << 8) | packet[pos + 1];
                    if (pos >= packet.Length)
                    {
                        return false;
                    }

                    continue;
                }

                if (len == 0)
                {
                    pos++;
                    offset = jumped ? jumpBack : pos;
                    name = labels is null || labels.Count == 0 ? string.Empty : string.Join(".", labels);
                    return true;
                }

                if (len > 63)
                {
                    return false;
                }

                if (pos + 1 + len > packet.Length)
                {
                    return false;
                }

                pos++;
                labels ??= [];

                expandedBytes += (labels.Count == 0 ? 0 : 1) + len;
                if (expandedBytes > MaxExpandedNameLengthBytes)
                {
                    return false;
                }

                labels.Add(System.Text.Encoding.ASCII.GetString(packet.Slice(pos, len)));
                pos += len;
            }

            return false;
        }
    }
}
