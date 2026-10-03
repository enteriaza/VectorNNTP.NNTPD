using System.Buffers;

namespace VectorNNTP.Common.Transport.ArticleTransfer
{
    /// <summary>
    /// Incremental, transport-agnostic parser for VATP v1 frames.
    /// </summary>
    /// <remarks>
    /// Operates over <see cref="ReadOnlySequence{T}"/>. Does not allocate for ordinary
    /// header validation. Payload is a slice of the input sequence (no article copy).
    /// Callers must supply the HELLO-negotiated max frame payload (or the protocol default
    /// before HELLO completes). The parser never allocates based on an attacker-controlled
    /// length beyond rejecting oversized frames.
    /// </remarks>
    internal static class VatpFrameParser
    {
        /// <summary>Attempts to parse exactly one frame from contiguous bytes.</summary>
        internal static VatpFrameParseResult ParseOneFrame(byte[] input, uint maxFramePayload) =>
            ParseOneFrame(new ReadOnlySequence<byte>(input), maxFramePayload);

        /// <summary>Attempts to parse exactly one frame from a possibly fragmented sequence.</summary>
        internal static VatpFrameParseResult ParseOneFrame(in ReadOnlySequence<byte> input, uint maxFramePayload)
        {
            if (maxFramePayload == 0)
            {
                return VatpFrameParseResult.Invalid(VatpErrorCode.InvalidMaxFramePayload, 0);
            }

            if (input.Length < VatpProtocol.HeaderLengthBytes)
            {
                return VatpFrameParseResult.Incomplete();
            }

            var reader = new SequenceReader<byte>(input);
            Span<byte> headerBytes = stackalloc byte[VatpProtocol.HeaderLengthBytes];
            if (!reader.TryCopyTo(headerBytes))
            {
                return VatpFrameParseResult.Incomplete();
            }

            var header = VatpFrameHeader.ReadFrom(headerBytes);
            var headerError = ValidateHeader(header, maxFramePayload);
            if (headerError != VatpErrorCode.None)
            {
                // Reject malformed/oversized headers without waiting for a declared payload that
                // may never arrive (and without allocating based on PayloadLength).
                long declared = (long)VatpProtocol.HeaderLengthBytes + header.PayloadLength;
                var consumed = declared < VatpProtocol.HeaderLengthBytes
                    ? VatpProtocol.HeaderLengthBytes
                    : input.Length >= declared
                        ? declared
                        : VatpProtocol.HeaderLengthBytes;
                return VatpFrameParseResult.Invalid(headerError, consumed);
            }

            long frameLength = (long)VatpProtocol.HeaderLengthBytes + header.PayloadLength;
            if (frameLength < VatpProtocol.HeaderLengthBytes)
            {
                return VatpFrameParseResult.Invalid(VatpErrorCode.InvalidFrameLength, VatpProtocol.HeaderLengthBytes);
            }

            if (input.Length < frameLength)
            {
                return VatpFrameParseResult.Incomplete();
            }

            reader.Advance(VatpProtocol.HeaderLengthBytes);
            var payload = input.Slice(reader.Position, header.PayloadLength);
            var payloadError = ValidatePayloadShape(header, payload);
            return payloadError != VatpErrorCode.None
                ? VatpFrameParseResult.Invalid(payloadError, frameLength)
                : VatpFrameParseResult.Success(frameLength, new VatpParsedFrame(header, payload));
        }

        /// <summary>Returns whether <paramref name="type"/> is a defined v1 frame type.</summary>
        private static bool IsSupportedFrameType(byte type) =>
            type is (byte)VatpFrameType.Hello
                or (byte)VatpFrameType.Open
                or (byte)VatpFrameType.Meta
                or (byte)VatpFrameType.Data
                or (byte)VatpFrameType.End
                or (byte)VatpFrameType.Fail
                or (byte)VatpFrameType.Cancel
                or (byte)VatpFrameType.Window
                or (byte)VatpFrameType.Store
                or (byte)VatpFrameType.Result;

        /// <summary>
        /// Checks version, frame type, header length, flags, payload limit, and stream id.
        /// </summary>
        /// <param name="header">Decoded 16-byte header.</param>
        /// <param name="maxFramePayload">Maximum accepted payload length. A larger <see cref="VatpFrameHeader.PayloadLength"/> is <see cref="VatpErrorCode.FrameTooLarge"/>.</param>
        /// <returns><see cref="VatpErrorCode.None"/> when the header is acceptable; otherwise the first failing code.</returns>
        /// <remarks>
        /// FIN is legal only on DATA. Any bit in <see cref="VatpProtocol.ReservedFlagsMask"/> is <see cref="VatpErrorCode.InvalidFlags"/>.
        /// </remarks>
        private static VatpErrorCode ValidateHeader(in VatpFrameHeader header, uint maxFramePayload)
        {
            if (header.Version != VatpProtocol.Version1)
            {
                return VatpErrorCode.UnsupportedVersion;
            }

            if (!IsSupportedFrameType((byte)header.Type))
            {
                return VatpErrorCode.InvalidFrameType;
            }

            if (header.HeaderLength != VatpProtocol.HeaderLengthBytes)
            {
                return VatpErrorCode.InvalidHeaderLength;
            }

            if ((header.Flags & VatpProtocol.ReservedFlagsMask) != 0)
            {
                return VatpErrorCode.InvalidFlags;
            }

            if (header.HasFin && header.Type != VatpFrameType.Data)
            {
                return VatpErrorCode.InvalidFlags;
            }

            if (header.PayloadLength > maxFramePayload)
            {
                return VatpErrorCode.FrameTooLarge;
            }

            return ValidateStreamId(header);
        }

        /// <summary>
        /// HELLO must use stream 0. OPEN, META, DATA, END, CANCEL, WINDOW, STORE, and RESULT must not.
        /// FAIL may use either.
        /// </summary>
        /// <param name="header">Header whose type and stream id are checked.</param>
        /// <returns><see cref="VatpErrorCode.InvalidStreamId"/>, <see cref="VatpErrorCode.InvalidFrameType"/>, or <see cref="VatpErrorCode.None"/>.</returns>
        private static VatpErrorCode ValidateStreamId(in VatpFrameHeader header)
        {
            switch (header.Type)
            {
                case VatpFrameType.Hello:
                    return header.StreamId == VatpProtocol.ConnectionStreamId
                        ? VatpErrorCode.None
                        : VatpErrorCode.InvalidStreamId;

                case VatpFrameType.Fail:
                    // Connection-level or stream-scoped FAIL is allowed.
                    return VatpErrorCode.None;

                case VatpFrameType.Open:
                case VatpFrameType.Meta:
                case VatpFrameType.Data:
                case VatpFrameType.End:
                case VatpFrameType.Cancel:
                case VatpFrameType.Window:
                case VatpFrameType.Store:
                case VatpFrameType.Result:
                    return header.StreamId == VatpProtocol.ConnectionStreamId
                        ? VatpErrorCode.InvalidStreamId
                        : VatpErrorCode.None;

                default:
                    return VatpErrorCode.InvalidFrameType;
            }
        }

        /// <summary>Checks that the buffered payload length matches the header and the fixed size for that frame type.</summary>
        /// <param name="header">Header already accepted by <see cref="ValidateHeader"/>.</param>
        /// <param name="payload">Bytes following the header. DATA length is not further constrained here.</param>
        /// <returns>
        /// <see cref="VatpErrorCode.None"/> when the length matches. HELLO, OPEN, and META use their specific error codes;
        /// other mismatches use <see cref="VatpErrorCode.InvalidFrameLength"/>.
        /// </returns>
        /// <remarks>
        /// END and CANCEL must be empty. WINDOW is 4 bytes. FAIL is 2 bytes plus at most
        /// <see cref="VatpProtocol.FailMaxReasonLength"/> reason bytes. STORE is 32 bytes. RESULT is 1 byte.
        /// </remarks>
        private static VatpErrorCode ValidatePayloadShape(in VatpFrameHeader header, in ReadOnlySequence<byte> payload)
        {
            if (payload.Length != header.PayloadLength)
            {
                return VatpErrorCode.InvalidFrameLength;
            }

            switch (header.Type)
            {
                case VatpFrameType.Hello:
                    return header.PayloadLength == VatpProtocol.HelloPayloadLength
                        ? VatpErrorCode.None
                        : VatpErrorCode.InvalidHello;

                case VatpFrameType.Open:
                    return header.PayloadLength == VatpProtocol.OpenPayloadLength
                        ? VatpErrorCode.None
                        : VatpErrorCode.InvalidOpen;

                case VatpFrameType.Store:
                    return header.PayloadLength == VatpProtocol.StorePayloadLength
                        ? VatpErrorCode.None
                        : VatpErrorCode.InvalidFrameLength;

                case VatpFrameType.Result:
                    return header.PayloadLength == VatpProtocol.ResultPayloadLength
                        ? VatpErrorCode.None
                        : VatpErrorCode.InvalidFrameLength;

                case VatpFrameType.Meta:
                    return header.PayloadLength == VatpProtocol.MetaPayloadLength
                        ? VatpErrorCode.None
                        : VatpErrorCode.InvalidMeta;

                case VatpFrameType.Window:
                    return header.PayloadLength == VatpProtocol.WindowPayloadLength
                        ? VatpErrorCode.None
                        : VatpErrorCode.InvalidFrameLength;

                case VatpFrameType.End:
                case VatpFrameType.Cancel:
                    return header.PayloadLength == VatpProtocol.EmptyPayloadLength
                        ? VatpErrorCode.None
                        : VatpErrorCode.InvalidFrameLength;

                case VatpFrameType.Fail:
                    return header.PayloadLength is >= VatpProtocol.FailMinPayloadLength
                           and <= VatpProtocol.FailMaxPayloadLength
                        ? VatpErrorCode.None
                        : VatpErrorCode.InvalidFrameLength;

                case VatpFrameType.Data:
                    return VatpErrorCode.None;

                default:
                    return VatpErrorCode.InvalidFrameType;
            }
        }
    }
}
