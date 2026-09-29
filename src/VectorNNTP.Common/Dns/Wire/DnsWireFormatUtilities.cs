// <copyright file="DnsWireFormatUtilities.cs" company="Usenet Ninja">
// Copyright (c) Chris Knipe &lt;cknipe@opticnetworks.net&gt;. Licensed under the Apache License, Version 2.0 (see LICENSE).
// </copyright>
// HOT PATH: no LINQ; no allocations on success; no closures; no boxing; prefer Span/stackalloc; throws extracted + NoInlining where documented.
// DnsWireFormatUtilities.cs -- DNS wire-format constants, validation, and QNAME encoding helpers (RFC 1035).
//
// Allocation characteristics:
//   - Label splitting uses stackalloc Range buffers for typical names.
//   - Error strings allocate only on validation failure paths.
//
// Thread safety:
//   All methods are static and stateless. Safe for concurrent use from any thread.

using System.Buffers;
using System.Text;

namespace VectorNNTP.NNTPD.Dns.Wire
{
    /// <summary>
    /// DNS wire-format constants, validation, and QNAME encoding helpers per RFC 1035.
    /// </summary>
    /// <remarks>
    /// <para><b>Purpose:</b> Centralizes label and name length limits, validation, and dotted-name to QNAME encoding so
    /// callers build DNS packets consistently and without unnecessary allocations.</para>
    ///
    /// <para><b>Thread safety:</b> All members are <see langword="static"/> and stateless.</para>
    ///
    /// <para><b>Performance:</b> HOT PATH — label splitting uses <c>stackalloc</c>; error strings allocate only on
    /// validation failure.</para>
    /// </remarks>
    public static class DnsWireFormatUtilities
    {
        /// <summary>
        /// Maximum length of a single DNS label in bytes (RFC 1035 §2.3.4).
        /// </summary>
        public const int MaxLabelLength = 63;

        /// <summary>
        /// Maximum DNS name length in wire format in bytes, including the trailing root label (RFC 1035 §3.1).
        /// </summary>
        public const int MaxWireNameLength = 255;

        /// <summary>
        /// Maximum presentation-form DNS hostname length (RFC 1035 §2.3.4).
        /// </summary>
        public const int MaxPresentationNameLength = 253;

        /// <summary>
        /// Fixed DNS header size in bytes (RFC 1035 §4.1.1).
        /// </summary>
        public const int DnsHeaderSize = 12;

        /// <summary>
        /// Size of QTYPE (2) + QCLASS (2) suffix appended after QNAME in the question section (RFC 1035 §4.1.2).
        /// </summary>
        public const int QuestionSuffixSize = 4;

        /// <summary>
        /// Maximum number of labels in a DNS name used for stack-allocated split buffers.
        /// </summary>
        public const int MaxLabelCount = 128;

        /// <summary>
        /// Validates a DNS name and computes wire QNAME length in a single label split.
        /// </summary>
        /// <param name="name">DNS name in dotted form.</param>
        /// <param name="labelRanges">Stack buffer for label ranges.</param>
        /// <param name="labelCount">Number of labels when validation succeeds.</param>
        /// <param name="qnameLength">Wire QNAME length including root label.</param>
        /// <param name="error">Error message on failure.</param>
        /// <returns><see langword="true"/> when the name is valid.</returns>
        public static bool TryGetWireNameLayout(
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
        /// Validates a DNS name for wire-format encoding: non-empty, no empty labels, per-label length limits, total QNAME
        /// length, and ASCII-only content.
        /// </summary>
        /// <param name="name">DNS name in dotted form (e.g. <c>_acme-challenge.example.com</c>).</param>
        /// <param name="error">On failure, a descriptive error string; on success, <see langword="null"/>.</param>
        /// <returns><see langword="true"/> if valid; otherwise <see langword="false"/>.</returns>
        public static bool TryValidateDnsName(string name, out string? error)
        {
            return TryValidateDnsName(name.AsSpan(), out error);
        }

        /// <summary>
        /// Validates a DNS name for wire-format encoding: non-empty, no empty labels, per-label length limits, total QNAME
        /// length, and ASCII-only content.
        /// </summary>
        /// <param name="name">DNS name in dotted form.</param>
        /// <param name="error">On failure, a descriptive error string; on success, <see langword="null"/>.</param>
        /// <returns><see langword="true"/> if valid; otherwise <see langword="false"/>.</returns>
        public static bool TryValidateDnsName(ReadOnlySpan<char> name, out string? error)
        {
            Span<Range> labelRanges = stackalloc Range[MaxLabelCount];
            return TryGetWireNameLayout(name, labelRanges, out _, out _, out error);
        }

        /// <summary>
        /// Computes the wire-format QNAME length for a dotted DNS name, including the trailing root label.
        /// </summary>
        /// <param name="name">DNS name in dotted form (already validated).</param>
        /// <returns>Wire-format QNAME length in bytes.</returns>
        public static int ComputeWireNameLength(string name)
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
        /// <param name="name">DNS name in dotted form (already validated).</param>
        /// <param name="destination">Destination buffer.</param>
        /// <returns>Bytes written (QNAME length).</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too short.</exception>
        public static int EncodeDnsName(string name, Span<byte> destination)
        {
            ReadOnlySpan<char> span = name.AsSpan();
            Span<Range> labelRanges = stackalloc Range[MaxLabelCount];
            if (!TryGetWireNameLayout(span, labelRanges, out int labelCount, out int qnameLength, out string? error))
            {
                throw new ArgumentException(error, nameof(name));
            }

            EnsureDestinationLength(qnameLength, destination.Length, nameof(destination));

            int offset = 0;
            for (int i = 0; i < labelCount; i++)
            {
                ReadOnlySpan<char> label = span[labelRanges[i]];
                destination[offset++] = (byte)label.Length;
                offset += EncodeAsciiLabel(label, destination[offset..]);
            }

            destination[offset++] = 0;
            return offset;
        }

        /// <summary>
        /// Encodes a pre-split dotted DNS name into QNAME wire format without re-splitting labels.
        /// </summary>
        /// <param name="name">Presentation-form DNS name.</param>
        /// <param name="labelRanges">Label ranges from <see cref="TryGetWireNameLayout"/>.</param>
        /// <param name="labelCount">Number of labels in <paramref name="labelRanges"/>.</param>
        /// <param name="destination">Destination buffer.</param>
        /// <returns>Bytes written (QNAME length).</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too short.</exception>
        public static int EncodeDnsName(
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

        private static int EncodeAsciiLabel(ReadOnlySpan<char> label, Span<byte> destination)
        {
            if (Ascii.FromUtf16(label, destination, out int written) != OperationStatus.Done)
            {
                throw new ArgumentException("DNS label contains non-ASCII characters.", nameof(label));
            }

            return written;
        }

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
        /// Splits a dotted DNS name into label ranges.
        /// </summary>
        /// <param name="name">Presentation-form DNS name.</param>
        /// <param name="labelRanges">Stack buffer for label ranges.</param>
        /// <param name="labelCount">Number of labels when the split succeeds.</param>
        /// <param name="error">Error message when the split fails.</param>
        /// <returns><see langword="true"/> when the name was split successfully.</returns>
        private static bool TrySplitDnsLabels(ReadOnlySpan<char> name, Span<Range> labelRanges, out int labelCount, out string? error)
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
