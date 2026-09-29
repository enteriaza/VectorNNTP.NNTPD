// <copyright file="DnsWireRecordTypes.cs" company="Usenet Ninja">
// Copyright (c) Chris Knipe &lt;cknipe@opticnetworks.net&gt;. Licensed under the Apache License, Version 2.0 (see LICENSE).
// </copyright>
// COLD PATH: diagnostics/formatting/startup validation; readability over micro-optimization; allocations acceptable.
// DnsWireRecordTypes.cs -- DNS resource record TYPE/QTYPE constants (RFC 1035).

namespace VectorNNTP.NNTPD.Dns.Wire
{
    /// <summary>
    /// DNS resource record TYPE/QTYPE constants (RFC 1035).
    /// </summary>
    public static class DnsWireRecordTypes
    {
        /// <summary>
        /// A record (IPv4 address).
        /// </summary>
        public const ushort A = 1;

        /// <summary>
        /// NS record (authoritative name server).
        /// </summary>
        public const ushort Ns = 2;

        /// <summary>
        /// TXT record (arbitrary character-string payloads, commonly used for DNS-01 challenges).
        /// </summary>
        public const ushort Txt = 16;

        /// <summary>
        /// AAAA record (IPv6 address).
        /// </summary>
        public const ushort Aaaa = 28;
    }
}
