// <copyright file="DnsWireTxtResponseParser.cs" company="Usenet Ninja">
// Copyright (c) Chris Knipe &lt;cknipe@opticnetworks.net&gt;. Licensed under the Apache License, Version 2.0 (see LICENSE).
// </copyright>
// HOT PATH: no LINQ; no allocations on success; no closures; no boxing; prefer Span/stackalloc; throws extracted + NoInlining where documented.
// DnsWireTxtResponseParser.cs -- Parses TXT RDATA from DNS response packets.

using System.Buffers.Binary;

namespace VectorNNTP.NNTPD.Dns.Wire
{
    /// <summary>
    /// Parses TXT resource records from DNS response packets.
    /// </summary>
    /// <remarks>
    /// <para><b>Performance:</b> HOT PATH — single-segment TXT fast path avoids <see cref="string"/> allocation; multi-segment
    /// records allocate one concatenated byte array per RR.</para>
    /// </remarks>
    public static class DnsWireTxtResponseParser
    {
        /// <summary>
        /// Fixed size of TYPE + CLASS + TTL + RDLENGTH fields before RDATA.
        /// </summary>
        private const int RrFixedFieldsSize = 10;

        /// <summary>
        /// Parses TXT answers from a DNS response and returns concatenated character-string bytes per RR.
        /// </summary>
        /// <param name="buffer">Response packet bytes.</param>
        /// <param name="expectedId">Query identifier echoed in the response.</param>
        /// <param name="results">Receives TXT payloads on success; cleared first.</param>
        /// <returns><see langword="true"/> when the header and question section were well-formed.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="results"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// Individual TXT RDATA segments that fail <c>TryReadTxtRdata</c> are skipped without failing the overall parse;
        /// the method still returns <see langword="true"/> when the packet header and question section are valid.
        /// </remarks>
        public static bool TryParseTxtRecords(ReadOnlySpan<byte> buffer, ushort expectedId, List<byte[]> results)
        {
            ArgumentNullException.ThrowIfNull(results);
            results.Clear();

            if (buffer.Length < DnsWireFormatUtilities.DnsHeaderSize)
            {
                return false;
            }

            ushort responseId = BinaryPrimitives.ReadUInt16BigEndian(buffer);
            if (responseId != expectedId)
            {
                return false;
            }

            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..]);
            if ((flags & 0xFA0F) != 0x8000)
            {
                return false;
            }

            ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[4..]);
            ushort anCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[6..]);
            int offset = DnsWireFormatUtilities.DnsHeaderSize;

            for (int q = 0; q < qdCount; q++)
            {
                if (!DnsWireNameSkipper.TrySkipName(buffer, ref offset))
                {
                    return false;
                }

                if (offset + DnsWireFormatUtilities.QuestionSuffixSize > buffer.Length)
                {
                    return false;
                }

                offset += DnsWireFormatUtilities.QuestionSuffixSize;
            }

            for (int a = 0; a < anCount; a++)
            {
                if (!DnsWireNameSkipper.TrySkipName(buffer, ref offset))
                {
                    return false;
                }

                if (offset + RrFixedFieldsSize > buffer.Length)
                {
                    return false;
                }

                ushort rrType = BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);
                ushort rrClass = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 2)..]);
                ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 8)..]);
                offset += RrFixedFieldsSize;

                if (offset + rdLength > buffer.Length)
                {
                    return false;
                }

                if (rrType == DnsWireRecordTypes.Txt && rrClass == DnsWireQueryBuilder.DnsClassIn)
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
        /// <param name="buffer">Response packet bytes.</param>
        /// <param name="expectedId">Query identifier echoed in the response.</param>
        /// <param name="expectedTxt">Expected TXT bytes (ASCII challenge digest).</param>
        /// <returns><see langword="true"/> when a matching TXT record is present.</returns>
        /// <remarks>
        /// Malformed TXT RDATA in non-matching answer records is skipped; the scan continues for other answers.
        /// </remarks>
        public static bool ResponseContainsTxt(ReadOnlySpan<byte> buffer, ushort expectedId, ReadOnlySpan<byte> expectedTxt)
        {
            if (buffer.Length < DnsWireFormatUtilities.DnsHeaderSize)
            {
                return false;
            }

            ushort responseId = BinaryPrimitives.ReadUInt16BigEndian(buffer);
            if (responseId != expectedId)
            {
                return false;
            }

            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..]);
            if ((flags & 0xFA0F) != 0x8000)
            {
                return false;
            }

            ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[4..]);
            ushort anCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[6..]);
            int offset = DnsWireFormatUtilities.DnsHeaderSize;

            for (int q = 0; q < qdCount; q++)
            {
                if (!DnsWireNameSkipper.TrySkipName(buffer, ref offset))
                {
                    return false;
                }

                if (offset + DnsWireFormatUtilities.QuestionSuffixSize > buffer.Length)
                {
                    return false;
                }

                offset += DnsWireFormatUtilities.QuestionSuffixSize;
            }

            for (int a = 0; a < anCount; a++)
            {
                if (!DnsWireNameSkipper.TrySkipName(buffer, ref offset))
                {
                    return false;
                }

                if (offset + RrFixedFieldsSize > buffer.Length)
                {
                    return false;
                }

                ushort rrType = BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]);
                ushort rrClass = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 2)..]);
                ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[(offset + 8)..]);
                offset += RrFixedFieldsSize;

                if (offset + rdLength > buffer.Length)
                {
                    return false;
                }

                if (rrType == DnsWireRecordTypes.Txt && rrClass == DnsWireQueryBuilder.DnsClassIn)
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
        /// Parses legacy string TXT answers for callers that still require <see cref="string"/> results.
        /// </summary>
        /// <param name="buffer">Response packet bytes.</param>
        /// <param name="expectedId">Query identifier echoed in the response.</param>
        /// <returns>TXT record strings (lossy ASCII for untrusted wire data).</returns>
        public static List<string> ParseTxtResponseStrings(byte[] buffer, ushort expectedId)
        {
            List<string> results = [];
            List<byte[]> raw = [];
            if (!TryParseTxtRecords(buffer, expectedId, raw))
            {
                return results;
            }

            for (int i = 0; i < raw.Count; i++)
            {
                byte[] txt = raw[i];
                results.Add(System.Text.Encoding.ASCII.GetString(txt));
            }

            return results;
        }

        /// <summary>
        /// Reads TXT RDATA into a newly allocated byte array.
        /// </summary>
        /// <param name="span">Response buffer.</param>
        /// <param name="offset">Current offset; advanced to the end of RDATA on return.</param>
        /// <param name="rdLength">RDATA length from the RR.</param>
        /// <returns>Concatenated TXT bytes, or <see langword="null"/> when malformed.</returns>
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

        /// <summary>
        /// Compares TXT RDATA to <paramref name="expectedTxt"/> without allocating a string.
        /// </summary>
        /// <param name="span">Response buffer.</param>
        /// <param name="offset">Current offset; advanced past RDATA.</param>
        /// <param name="rdLength">RDATA length.</param>
        /// <param name="expectedTxt">Expected TXT bytes.</param>
        /// <returns><see langword="true"/> when RDATA matches.</returns>
        private static bool TryTxtRdataEquals(ReadOnlySpan<byte> span, ref int offset, ushort rdLength, ReadOnlySpan<byte> expectedTxt)
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
