using System.Buffers;
using System.Text;

namespace VectorNNTP.Common.Dns
{
    /// <summary>
    /// DNS wire-format limits, QNAME validation, and presentation-form encoding helpers (RFC 1035).
    /// </summary>
    /// <remarks>
    /// Label splitting uses <c>stackalloc</c> <see cref="Range"/> buffers. Error strings allocate only on
    /// validation failure. All members are stateless and safe for concurrent use.
    /// </remarks>
    internal static class DnsWireFormat
    {
        /// <summary>Maximum length of a single DNS label in bytes (RFC 1035 §2.3.4).</summary>
        public const int MaxLabelLength = 63;

        /// <summary>Maximum DNS name length in wire format, including the trailing root label (RFC 1035 §3.1).</summary>
        public const int MaxWireNameLength = 255;

        /// <summary>Maximum presentation-form DNS hostname length (RFC 1035 §2.3.4).</summary>
        public const int MaxPresentationNameLength = 253;

        /// <summary>Fixed DNS header size in bytes (RFC 1035 §4.1.1).</summary>
        public const int HeaderSize = 12;

        /// <summary>QTYPE (2) + QCLASS (2) after QNAME in the question section (RFC 1035 §4.1.2).</summary>
        public const int QuestionSuffixSize = 4;

        /// <summary>Maximum labels used for stack-allocated split buffers.</summary>
        public const int MaxLabelCount = 128;

        /// <summary>
        /// TYPE + CLASS + TTL + RDLENGTH size before RDATA in a resource record.
        /// </summary>
        public const int ResourceRecordFixedFieldsSize = 10;

        /// <summary>
        /// Validates a DNS name and computes wire QNAME length in a single label split.
        /// </summary>
        internal static bool TryGetWireNameLayout(
            ReadOnlySpan<char> name,
            Span<Range> labelRanges,
            out int labelCount,
            out int qnameLength,
            out string? error)
        {
            qnameLength = 0;
            if (name.IsEmpty)
            {
                labelCount = 0;
                error = "DNS name must not be null or empty.";
                return false;
            }

            if (!TrySplitDnsLabels(name, labelRanges, out labelCount, out error))
            {
                return false;
            }

            qnameLength = 1;
            for (int i = 0; i < labelCount; i++)
            {
                ReadOnlySpan<char> label = name[labelRanges[i]];

                if (label.IsEmpty)
                {
                    error = $"DNS name '{name}' contains an empty label (consecutive dots, leading dot, or trailing dot).";
                    return false;
                }

                if (label.Length > MaxLabelLength)
                {
                    error = $"DNS label '{label}' exceeds the maximum length of {MaxLabelLength} bytes (RFC 1035 §2.3.4).";
                    return false;
                }

                if (!Ascii.IsValid(label))
                {
                    error = $"DNS label '{label}' contains non-ASCII characters. DNS names must be ASCII-only (RFC 1035 §2.3.4).";
                    return false;
                }

                qnameLength += 1 + label.Length;
            }

            if (qnameLength > MaxWireNameLength)
            {
                error = $"DNS name '{name}' exceeds the maximum QNAME length of {MaxWireNameLength} bytes (RFC 1035 §3.1).";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Validates a DNS name for wire-format encoding.
        /// </summary>
        internal static bool TryValidateDnsName(string name, out string? error)
            => TryValidateDnsName(name.AsSpan(), out error);

        /// <summary>
        /// Validates a DNS name for wire-format encoding.
        /// </summary>
        private static bool TryValidateDnsName(ReadOnlySpan<char> name, out string? error)
        {
            Span<Range> labelRanges = stackalloc Range[MaxLabelCount];
            return TryGetWireNameLayout(name, labelRanges, out _, out _, out error);
        }

        /// <summary>
        /// Computes the wire-format QNAME length for a dotted DNS name, including the trailing root label.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is invalid.</exception>
        internal static int ComputeWireNameLength(string name)
        {
            ReadOnlySpan<char> span = name.AsSpan();
            Span<Range> labelRanges = stackalloc Range[MaxLabelCount];
            if (!TryGetWireNameLayout(span, labelRanges, out _, out int qnameLength, out string? error))
            {
                throw new ArgumentException(error, nameof(name));
            }

            return qnameLength;
        }

        /// <summary>
        /// Encodes a dotted DNS name into QNAME wire format.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when the name is invalid or <paramref name="destination"/> is too short.</exception>
        internal static int EncodeDnsName(string name, Span<byte> destination)
        {
            ReadOnlySpan<char> span = name.AsSpan();
            Span<Range> labelRanges = stackalloc Range[MaxLabelCount];
            if (!TryGetWireNameLayout(span, labelRanges, out int labelCount, out int qnameLength, out string? error))
            {
                throw new ArgumentException(error, nameof(name));
            }

            EnsureDestinationLength(qnameLength, destination.Length, nameof(destination));
            return EncodeDnsName(span, labelRanges, labelCount, destination);
        }

        /// <summary>
        /// Encodes a pre-split dotted DNS name into QNAME wire format without re-splitting labels.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too short.</exception>
        internal static int EncodeDnsName(
            ReadOnlySpan<char> name,
            ReadOnlySpan<Range> labelRanges,
            int labelCount,
            Span<byte> destination)
        {
            int qnameLength = 1;
            for (int i = 0; i < labelCount; i++)
            {
                qnameLength += 1 + name[labelRanges[i]].Length;
            }

            EnsureDestinationLength(qnameLength, destination.Length, nameof(destination));

            int offset = 0;
            for (int i = 0; i < labelCount; i++)
            {
                ReadOnlySpan<char> label = name[labelRanges[i]];
                destination[offset++] = (byte)label.Length;
                offset += EncodeAsciiLabel(label, destination[offset..]);
            }

            destination[offset++] = 0;
            return offset;
        }

        /// <summary>Writes one DNS label as ASCII into <paramref name="destination"/>.</summary>
        /// <param name="label">Presentation-form label. The length byte is written by the caller.</param>
        /// <param name="destination">Buffer that must hold the label bytes.</param>
        /// <returns>The number of bytes written.</returns>
        /// <exception cref="ArgumentException">The label contains a non-ASCII character.</exception>
        private static int EncodeAsciiLabel(ReadOnlySpan<char> label, Span<byte> destination)
        {
            if (Ascii.FromUtf16(label, destination, out int written) != OperationStatus.Done)
            {
                throw new ArgumentException("DNS label contains non-ASCII characters.", nameof(label));
            }

            return written;
        }

        /// <summary>Throws when <paramref name="actualLength"/> is shorter than <paramref name="requiredLength"/>.</summary>
        /// <param name="requiredLength">Bytes the encode operation needs.</param>
        /// <param name="actualLength">Bytes available in the destination.</param>
        /// <param name="paramName">Parameter name used on the thrown <see cref="ArgumentException"/>.</param>
        /// <exception cref="ArgumentException"><paramref name="actualLength"/> is too small.</exception>
        private static void EnsureDestinationLength(int requiredLength, int actualLength, string paramName)
        {
            if (actualLength < requiredLength)
            {
                throw new ArgumentException(
                    $"Destination buffer is too short: required {requiredLength}, actual {actualLength}.",
                    paramName);
            }
        }

        /// <summary>
        /// Splits <paramref name="name"/> on dots into <paramref name="labelRanges"/>.
        /// A split that fills the range buffer is treated as too many labels.
        /// </summary>
        /// <param name="name">Presentation-form name without a trailing root dot.</param>
        /// <param name="labelRanges">Buffer of label ranges. The last slot must remain unused for the overflow check.</param>
        /// <param name="labelCount">Number of labels written.</param>
        /// <param name="error">Failure text when the name has too many labels; otherwise null.</param>
        /// <returns><see langword="false"/> when the split fills <paramref name="labelRanges"/>.</returns>
        private static bool TrySplitDnsLabels(
            ReadOnlySpan<char> name,
            Span<Range> labelRanges,
            out int labelCount,
            out string? error)
        {
            labelCount = name.Split(labelRanges, '.', StringSplitOptions.None);

            if (labelCount == labelRanges.Length)
            {
                error = $"DNS name '{name}' contains too many labels ({labelCount}+).";
                return false;
            }

            error = null;
            return true;
        }
    }
}
