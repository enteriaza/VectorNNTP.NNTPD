// <copyright file="DnsWireQueryBuilder.cs" company="Usenet Ninja">
// Copyright (c) Chris Knipe &lt;cknipe@opticnetworks.net&gt;. Licensed under the Apache License, Version 2.0 (see LICENSE).
// </copyright>
// HOT PATH: no LINQ; no allocations on success; no closures; no boxing; prefer Span/stackalloc; throws extracted + NoInlining where documented.
// DnsWireQueryBuilder.cs -- Builds minimal RFC 1035 DNS queries (single-question).
//
// Allocation characteristics:
//   - Uses stackalloc for packets up to MaxStackAllocQuerySize; otherwise allocates a byte array.
//   - queryId uses Random.Shared (thread-safe on .NET 8+).
//
// Thread safety:
//   Build is static and stateless. Safe for concurrent use from any thread.

using System.Buffers.Binary;

namespace VectorNNTP.NNTPD.Dns.Wire
{
    /// <summary>
    /// Builds a minimal RFC 1035 DNS query (single question).
    /// </summary>
    /// <remarks>
    /// <para><b>Allocation:</b> Packets that fit in <see cref="MaxStackAllocQuerySize"/> use <c>stackalloc</c>; larger names
    /// allocate a temporary buffer on the heap before <c>ToArray()</c>.</para>
    ///
    /// <para><b>Thread safety:</b> <see cref="Random.Shared"/> is thread-safe. All methods are stateless.</para>
    /// </remarks>
    public static class DnsWireQueryBuilder
    {
        /// <summary>
        /// IN class constant (RFC 1035).
        /// </summary>
        public const ushort DnsClassIn = 1;

        /// <summary>
        /// Maximum query packet size built with <c>stackalloc</c> before falling back to a heap buffer.
        /// </summary>
        private const int MaxStackAllocQuerySize = DnsWireFormatUtilities.DnsHeaderSize
            + DnsWireFormatUtilities.MaxWireNameLength
            + DnsWireFormatUtilities.QuestionSuffixSize;

        /// <summary>
        /// Builds a DNS query packet for the given QNAME and QTYPE.
        /// </summary>
        /// <param name="name">QNAME in dotted ASCII form. A trailing dot is tolerated and trimmed.</param>
        /// <param name="qtype">DNS query type (e.g. TXT, NS, A).</param>
        /// <param name="queryId">Random query identifier echoed in the response.</param>
        /// <param name="recursionDesired">When true, sets the RD bit for recursive resolvers; false for authoritative targets.</param>
        /// <returns>DNS query bytes.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is invalid for DNS wire encoding.</exception>
        public static byte[] Build(string name, ushort qtype, out ushort queryId, bool recursionDesired = false)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            name = name.TrimEnd('.');

            ReadOnlySpan<char> nameSpan = name.AsSpan();
            Span<Range> labelRanges = stackalloc Range[DnsWireFormatUtilities.MaxLabelCount];
            if (!DnsWireFormatUtilities.TryGetWireNameLayout(nameSpan, labelRanges, out int labelCount, out int qnameLength, out string? error))
            {
                throw new ArgumentException(error, nameof(name));
            }

            queryId = (ushort)Random.Shared.Next(ushort.MaxValue + 1);

            int packetLength = DnsWireFormatUtilities.DnsHeaderSize + qnameLength + DnsWireFormatUtilities.QuestionSuffixSize;

            Span<byte> span = packetLength <= MaxStackAllocQuerySize
                ? stackalloc byte[MaxStackAllocQuerySize]
                : new byte[packetLength];

            BinaryPrimitives.WriteUInt16BigEndian(span, queryId);
            ushort flags = recursionDesired ? (ushort)0x0100 : (ushort)0;
            BinaryPrimitives.WriteUInt16BigEndian(span[2..], flags);
            BinaryPrimitives.WriteUInt16BigEndian(span[4..], 1);

            int offset = DnsWireFormatUtilities.DnsHeaderSize;
            offset += DnsWireFormatUtilities.EncodeDnsName(nameSpan, labelRanges, labelCount, span[offset..]);

            BinaryPrimitives.WriteUInt16BigEndian(span[offset..], qtype);
            offset += 2;
            BinaryPrimitives.WriteUInt16BigEndian(span[offset..], DnsClassIn);

            return span[..packetLength].ToArray();
        }
    }
}
