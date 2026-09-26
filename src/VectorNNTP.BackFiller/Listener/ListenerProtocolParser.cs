using System.Buffers;

namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// Incremental, transport-agnostic parser for Listener v1 frames.
/// </summary>
public static class ListenerProtocolParser
{
    /// <summary>Attempts to parse exactly one frame from contiguous bytes.</summary>
    public static ListenerFrameParseResult ParseOneFrame(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ReadOnlySequence<byte> sequence = new(input);
        return ParseOneFrame(in sequence);
    }

    /// <summary>Attempts to parse exactly one frame from a possibly fragmented sequence.</summary>
    public static ListenerFrameParseResult ParseOneFrame(in ReadOnlySequence<byte> input)
    {
        if (input.Length < ListenerProtocol.HeaderLengthBytes)
        {
            return ListenerFrameParseResult.Incomplete();
        }

        var reader = new SequenceReader<byte>(input);
        Span<byte> headerBytes = stackalloc byte[ListenerProtocol.HeaderLengthBytes];
        if (!reader.TryCopyTo(headerBytes))
        {
            return ListenerFrameParseResult.Incomplete();
        }

        var header = ListenerFrameHeader.ReadFrom(headerBytes);
        var headerError = ValidateHeader(header);
        long frameLength = (long)ListenerProtocol.HeaderLengthBytes + header.PayloadLength;
        if (frameLength < ListenerProtocol.HeaderLengthBytes)
        {
            return ListenerFrameParseResult.Invalid(ListenerFrameParseError.InvalidFrameLength, ListenerProtocol.HeaderLengthBytes);
        }

        if (input.Length < frameLength)
        {
            return ListenerFrameParseResult.Incomplete();
        }

        if (headerError != ListenerFrameParseError.None)
        {
            return ListenerFrameParseResult.Invalid(headerError, frameLength);
        }

        reader.Advance(ListenerProtocol.HeaderLengthBytes);
        var payload = input.Slice(reader.Position, header.PayloadLength);
        var payloadError = ValidatePayloadShape(header, payload);
        return payloadError != ListenerFrameParseError.None
            ? ListenerFrameParseResult.Invalid(payloadError, frameLength)
            : ListenerFrameParseResult.Success(frameLength, new ListenerParsedFrame(header, payload));
    }

    /// <summary>Returns whether <paramref name="opcode"/> is a v1 opcode.</summary>
    public static bool IsSupportedOpcode(byte opcode) =>
        opcode is (byte)ListenerOpcode.GetRequest
            or (byte)ListenerOpcode.GetResponseFound
            or (byte)ListenerOpcode.GetResponseNotFound
            or (byte)ListenerOpcode.GetResponseError
            or (byte)ListenerOpcode.GetReceiptAck;

    /// <summary>Returns whether payload is exactly 32 lowercase ASCII hex bytes.</summary>
    public static bool IsValidCanonicalMessageIdMd5Payload(ReadOnlySequence<byte> payload)
    {
        if (payload.Length != ListenerProtocol.GetRequestPayloadLength)
        {
            return false;
        }

        foreach (var segment in payload)
        {
            if (!IsValidCanonicalMessageIdMd5Payload(segment.Span))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns whether every byte is lowercase ASCII hex.</summary>
    public static bool IsValidCanonicalMessageIdMd5Payload(ReadOnlySpan<byte> payload)
    {
        for (var i = 0; i < payload.Length; i++)
        {
            var value = payload[i];
            if (value is not (>= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static ListenerFrameParseError ValidateHeader(in ListenerFrameHeader header)
    {
        if (header.Version != ListenerProtocol.Version1)
        {
            return ListenerFrameParseError.UnsupportedVersion;
        }

        if (!IsSupportedOpcode((byte)header.Opcode))
        {
            return ListenerFrameParseError.UnsupportedOpcode;
        }

        if (header.HeaderLength != ListenerProtocol.HeaderLengthBytes)
        {
            return ListenerFrameParseError.InvalidHeaderLength;
        }

        if (header.Reserved != 0)
        {
            return ListenerFrameParseError.InvalidReserved;
        }

        return header.RequestId == 0
            ? ListenerFrameParseError.InvalidRequestId
            : ListenerFrameParseError.None;
    }

    private static ListenerFrameParseError ValidatePayloadShape(in ListenerFrameHeader header, in ReadOnlySequence<byte> payload)
    {
        switch (header.Opcode)
        {
            case ListenerOpcode.GetRequest:
                if (header.PayloadLength != ListenerProtocol.GetRequestPayloadLength
                    || payload.Length != ListenerProtocol.GetRequestPayloadLength)
                {
                    return ListenerFrameParseError.InvalidFrameLength;
                }

                return IsValidCanonicalMessageIdMd5Payload(payload)
                    ? ListenerFrameParseError.None
                    : ListenerFrameParseError.InvalidMessageIdMd5;

            case ListenerOpcode.GetResponseNotFound:
                if (header.PayloadLength != ListenerProtocol.GetResponseNotFoundPayloadLength
                    || payload.Length != ListenerProtocol.GetResponseNotFoundPayloadLength)
                {
                    return ListenerFrameParseError.InvalidFrameLength;
                }

                var notFound = new SequenceReader<byte>(payload);
                return notFound.TryRead(out var reason) && reason == ListenerProtocol.NotFoundReasonUnavailable
                    ? ListenerFrameParseError.None
                    : ListenerFrameParseError.InvalidFrameLength;

            case ListenerOpcode.GetResponseError:
                return header.PayloadLength == ListenerProtocol.GetResponseErrorPayloadLength
                       && payload.Length == ListenerProtocol.GetResponseErrorPayloadLength
                    ? ListenerFrameParseError.None
                    : ListenerFrameParseError.InvalidFrameLength;

            case ListenerOpcode.GetReceiptAck:
                return header.PayloadLength == ListenerProtocol.GetReceiptAckPayloadLength
                       && payload.Length == ListenerProtocol.GetReceiptAckPayloadLength
                    ? ListenerFrameParseError.None
                    : ListenerFrameParseError.InvalidFrameLength;

            case ListenerOpcode.GetResponseFound:
                return payload.Length == header.PayloadLength
                    ? ListenerFrameParseError.None
                    : ListenerFrameParseError.InvalidFrameLength;

            default:
                return ListenerFrameParseError.UnsupportedOpcode;
        }
    }
}
