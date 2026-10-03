using System.Buffers.Binary;

namespace VectorNNTP.Common.Dns
{
    /// <summary>
    /// Builds a minimal single-question RFC 1035 DNS query.
    /// </summary>
    /// <remarks>
    /// Packets that fit in a stack buffer use <c>stackalloc</c>; larger names allocate a temporary heap buffer
    /// before <c>ToArray()</c>. <see cref="Random.Shared"/> supplies transaction IDs. Stateless and thread-safe.
    /// </remarks>
    public static class DnsQueryBuilder
    {
        private const int MaxStackAllocQuerySize =
            DnsWireFormat.HeaderSize + DnsWireFormat.MaxWireNameLength + DnsWireFormat.QuestionSuffixSize;

        /// <summary>
        /// Builds a DNS query packet for the given QNAME and QTYPE.
        /// </summary>
        /// <param name="name">QNAME in dotted ASCII form. A trailing dot is tolerated and trimmed.</param>
        /// <param name="qtype">DNS query type (for example <see cref="DnsRecordType.Txt"/>).</param>
        /// <param name="queryId">Random query identifier echoed in the response.</param>
        /// <param name="recursionDesired">
        /// When <see langword="true"/>, sets RD for recursive bootstrap resolvers; when <see langword="false"/>,
        /// clears RD for direct authoritative queries.
        /// </param>
        /// <returns>DNS query bytes.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is invalid for DNS wire encoding.</exception>
        public static byte[] Build(string name, ushort qtype, out ushort queryId, bool recursionDesired = false)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            name = name.TrimEnd('.');

            ReadOnlySpan<char> nameSpan = name.AsSpan();
            Span<Range> labelRanges = stackalloc Range[DnsWireFormat.MaxLabelCount];
            if (!DnsWireFormat.TryGetWireNameLayout(
                    nameSpan,
                    labelRanges,
                    out int labelCount,
                    out int qnameLength,
                    out string? error))
            {
                throw new ArgumentException(error, nameof(name));
            }

            queryId = (ushort)Random.Shared.Next(ushort.MaxValue + 1);

            int packetLength = DnsWireFormat.HeaderSize + qnameLength + DnsWireFormat.QuestionSuffixSize;

            Span<byte> span = packetLength <= MaxStackAllocQuerySize
                ? stackalloc byte[MaxStackAllocQuerySize]
                : new byte[packetLength];

            BinaryPrimitives.WriteUInt16BigEndian(span, queryId);
            ushort flags = recursionDesired ? (ushort)0x0100 : (ushort)0;
            BinaryPrimitives.WriteUInt16BigEndian(span[2..], flags);
            BinaryPrimitives.WriteUInt16BigEndian(span[4..], 1);

            int offset = DnsWireFormat.HeaderSize;
            offset += DnsWireFormat.EncodeDnsName(nameSpan, labelRanges, labelCount, span[offset..]);

            BinaryPrimitives.WriteUInt16BigEndian(span[offset..], qtype);
            offset += 2;
            BinaryPrimitives.WriteUInt16BigEndian(span[offset..], DnsRecordType.ClassIn);

            return span[..packetLength].ToArray();
        }
    }
}
