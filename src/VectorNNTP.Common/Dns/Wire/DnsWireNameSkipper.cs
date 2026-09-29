// <copyright file="DnsWireNameSkipper.cs" company="Usenet Ninja">
// Copyright (c) Chris Knipe &lt;cknipe@opticnetworks.net&gt;. Licensed under the Apache License, Version 2.0 (see LICENSE).
// </copyright>
// HOT PATH: no LINQ; no allocations on success; no closures; no boxing; prefer Span/stackalloc; throws extracted + NoInlining where documented.
// DnsWireNameSkipper.cs -- Skips DNS wire-format names (inline labels and compression pointers).

namespace VectorNNTP.NNTPD.Dns.Wire
{
    /// <summary>
    /// Skips DNS wire-format domain names in response packets (RFC 1035), including compression pointers.
    /// </summary>
    /// <remarks>
    /// <para><b>Performance:</b> HOT PATH — pure offset arithmetic with no allocations on success.</para>
    /// </remarks>
    public static class DnsWireNameSkipper
    {
        /// <summary>
        /// Maximum compression pointer hops before aborting traversal.
        /// </summary>
        private const int MaxCompressionPointerHops = 128;

        /// <summary>
        /// Advances <paramref name="offset"/> past a DNS name encoding (labels or compression pointer).
        /// </summary>
        /// <param name="packet">DNS packet bytes.</param>
        /// <param name="offset">Current read position; advanced on success.</param>
        /// <returns><see langword="true"/> when the name was skipped successfully.</returns>
        public static bool TrySkipName(ReadOnlySpan<byte> packet, ref int offset)
        {
            int hops = 0;
            while (offset < packet.Length)
            {
                if (++hops > MaxCompressionPointerHops)
                {
                    return false;
                }

                byte labelLength = packet[offset];
                if (labelLength == 0)
                {
                    offset++;
                    return true;
                }

                if ((labelLength & 0xC0) == 0xC0)
                {
                    if (offset + 2 > packet.Length)
                    {
                        return false;
                    }

                    offset += 2;
                    return true;
                }

                if ((labelLength & 0xC0) != 0)
                {
                    return false;
                }

                int advance = 1 + labelLength;
                if (offset + advance > packet.Length)
                {
                    return false;
                }

                offset += advance;
            }

            return false;
        }
    }
}
