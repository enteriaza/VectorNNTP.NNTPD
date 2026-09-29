using System.Buffers.Binary;

namespace VectorNNTP.NNTPD.Dns;

/// <summary>
/// Shared DNS response header checks (transaction ID, QR, OPCODE, TC, RCODE).
/// </summary>
internal static class DnsResponseHeader
{
    /// <summary>
    /// Mask requiring QR=1, OPCODE=0, TC=0, and RCODE=0 (AA/RD/RA/Z ignored).
    /// </summary>
    private const ushort ResponseSanityMask = 0xFA0F;

    /// <summary>Expected masked value: response with NoError and no truncation.</summary>
    private const ushort ResponseSanityValue = 0x8000;

    /// <summary>DNS header TC (truncated) flag.</summary>
    public const ushort TruncatedFlag = 0x0200;

    /// <summary>
    /// Returns <see langword="true"/> when the buffer has a header, matching ID, and sane response flags.
    /// </summary>
    public static bool TryReadCounts(
        ReadOnlySpan<byte> buffer,
        ushort expectedId,
        out ushort questionCount,
        out ushort answerCount,
        out ushort authorityCount,
        out ushort additionalCount)
    {
        questionCount = 0;
        answerCount = 0;
        authorityCount = 0;
        additionalCount = 0;

        if (buffer.Length < DnsWireFormat.HeaderSize)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(buffer) != expectedId)
        {
            return false;
        }

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..]);
        if ((flags & ResponseSanityMask) != ResponseSanityValue)
        {
            return false;
        }

        questionCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[4..]);
        answerCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[6..]);
        authorityCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[8..]);
        additionalCount = BinaryPrimitives.ReadUInt16BigEndian(buffer[10..]);
        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the UDP response header indicates truncation (TC).
    /// </summary>
    public static bool IsTruncated(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < DnsWireFormat.HeaderSize)
        {
            return true;
        }

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(buffer[2..]);
        return (flags & TruncatedFlag) != 0;
    }

    /// <summary>
    /// Advances past the question section.
    /// </summary>
    public static bool TrySkipQuestions(ReadOnlySpan<byte> span, ref int offset, ushort questionCount)
    {
        for (int q = 0; q < questionCount; q++)
        {
            if (!DnsNameCodec.TrySkipName(span, ref offset))
            {
                return false;
            }

            if (offset + DnsWireFormat.QuestionSuffixSize > span.Length)
            {
                return false;
            }

            offset += DnsWireFormat.QuestionSuffixSize;
        }

        return true;
    }
}
