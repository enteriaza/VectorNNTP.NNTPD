using System.Buffers.Binary;
using System.Text;

namespace VectorNNTP.Common.Dns
{
    /// <summary>
    /// Parses TXT resource records from DNS response packets for ACME DNS-01 visibility checks.
    /// </summary>
    /// <remarks>
    /// Validates transaction ID, QR, OPCODE, TC, and RCODE before scanning answers. Malformed individual
    /// TXT RDATA segments are skipped without failing the overall parse when the header and question
    /// section are well-formed. Multi-segment TXT RDATA is concatenated per RR.
    /// </remarks>
    public static class DnsTxtParser
    {
        /// <summary>
        /// Parses TXT answers from a DNS response and returns concatenated character-string bytes per RR.
        /// </summary>
        /// <returns><see langword="true"/> when the header and question section were well-formed.</returns>
        public static bool TryParseTxtRecords(ReadOnlySpan<byte> buffer, ushort expectedId, List<byte[]> results)
        {
            ArgumentNullException.ThrowIfNull(results);
            results.Clear();

            if (!DnsResponseHeader.TryReadCounts(
                    buffer,
                    expectedId,
                    out ushort qdCount,
                    out ushort anCount,
                    out _,
                    out _))
            {
                return false;
            }

            int offset = DnsWireFormat.HeaderSize;
            if (!DnsResponseHeader.TrySkipQuestions(buffer, ref offset, qdCount))
            {
                return false;
            }

            for (int a = 0; a < anCount; a++)
            {
                if (!DnsNameCodec.TrySkipName(buffer, ref offset))
                {
                    return false;
                }

                if (offset + DnsWireFormat.ResourceRecordFixedFieldsSize > buffer.Length)
                {
                    return false;
                }

                ushort rrType = BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);
                ushort rrClass = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 2)..]);
                ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 8)..]);
                offset += DnsWireFormat.ResourceRecordFixedFieldsSize;

                if (offset + rdLength > buffer.Length)
                {
                    return false;
                }

                if (rrType == DnsRecordType.Txt && rrClass == DnsRecordType.ClassIn)
                {
                    byte[]? txtBytes = TryReadTxtRdata(buffer, ref offset, rdLength);
                    if (txtBytes is not null)
                    {
                        results.Add(txtBytes);
                    }
                }
                else
                {
                    offset += rdLength;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns <see langword="true"/> when any TXT RR payload equals <paramref name="expectedTxt"/>.
        /// </summary>
        public static bool ResponseContainsTxt(
            ReadOnlySpan<byte> buffer,
            ushort expectedId,
            ReadOnlySpan<byte> expectedTxt)
        {
            if (!DnsResponseHeader.TryReadCounts(
                    buffer,
                    expectedId,
                    out ushort qdCount,
                    out ushort anCount,
                    out _,
                    out _))
            {
                return false;
            }

            int offset = DnsWireFormat.HeaderSize;
            if (!DnsResponseHeader.TrySkipQuestions(buffer, ref offset, qdCount))
            {
                return false;
            }

            for (int a = 0; a < anCount; a++)
            {
                if (!DnsNameCodec.TrySkipName(buffer, ref offset))
                {
                    return false;
                }

                if (offset + DnsWireFormat.ResourceRecordFixedFieldsSize > buffer.Length)
                {
                    return false;
                }

                ushort rrType = BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);
                ushort rrClass = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 2)..]);
                ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 8)..]);
                offset += DnsWireFormat.ResourceRecordFixedFieldsSize;

                if (offset + rdLength > buffer.Length)
                {
                    return false;
                }

                if (rrType == DnsRecordType.Txt && rrClass == DnsRecordType.ClassIn)
                {
                    if (TryTxtRdataEquals(buffer, ref offset, rdLength, expectedTxt))
                    {
                        return true;
                    }
                }
                else
                {
                    offset += rdLength;
                }
            }

            return false;
        }

        /// <summary>
        /// Parses TXT answers as ASCII strings (lossy for non-ASCII wire data).
        /// </summary>
        public static List<string> ParseTxtStrings(byte[] buffer, ushort expectedId)
        {
            List<string> results = [];
            List<byte[]> raw = [];
            if (!TryParseTxtRecords(buffer, expectedId, raw))
            {
                return results;
            }

            for (int i = 0; i < raw.Count; i++)
            {
                results.Add(Encoding.ASCII.GetString(raw[i]));
            }

            return results;
        }

        private static byte[]? TryReadTxtRdata(ReadOnlySpan<byte> span, ref int offset, ushort rdLength)
        {
            int rdEnd = offset + rdLength;
            if (offset >= rdEnd || offset >= span.Length)
            {
                offset = rdEnd;
                return null;
            }

            int totalLength = 0;
            int scan = offset;
            while (scan < rdEnd)
            {
                if (scan >= span.Length)
                {
                    offset = rdEnd;
                    return null;
                }

                int strLen = span[scan++];
                if (scan + strLen > span.Length || scan + strLen > rdEnd)
                {
                    offset = rdEnd;
                    return null;
                }

                totalLength += strLen;
                scan += strLen;
            }

            byte[] result = new byte[totalLength];
            int write = 0;
            while (offset < rdEnd)
            {
                int strLen = span[offset++];
                span.Slice(offset, strLen).CopyTo(result.AsSpan(write));
                write += strLen;
                offset += strLen;
            }

            return result;
        }

        private static bool TryTxtRdataEquals(
            ReadOnlySpan<byte> span,
            ref int offset,
            ushort rdLength,
            ReadOnlySpan<byte> expectedTxt)
        {
            int rdEnd = offset + rdLength;
            if (offset >= rdEnd || offset >= span.Length)
            {
                offset = rdEnd;
                return false;
            }

            int compareIndex = 0;
            while (offset < rdEnd)
            {
                if (offset >= span.Length)
                {
                    offset = rdEnd;
                    return false;
                }

                int strLen = span[offset++];
                if (offset + strLen > span.Length || offset + strLen > rdEnd)
                {
                    offset = rdEnd;
                    return false;
                }

                ReadOnlySpan<byte> segment = span.Slice(offset, strLen);
                if (compareIndex + segment.Length > expectedTxt.Length)
                {
                    offset = rdEnd;
                    return false;
                }

                if (!segment.SequenceEqual(expectedTxt.Slice(compareIndex, segment.Length)))
                {
                    offset = rdEnd;
                    return false;
                }

                compareIndex += segment.Length;
                offset += strLen;
            }

            return compareIndex == expectedTxt.Length;
        }
    }
}
