using System.Buffers.Binary;
using System.Text;

namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// Deterministic encoders for Listener v1 frames. Found responses keep payload memory separate.
/// </summary>
public static class ListenerProtocolEncoder
{
    /// <summary>Encodes a GetRequest for a canonical 32-character lowercase MD5.</summary>
    public static byte[] EncodeGetRequest(uint requestId, string messageIdMd5)
    {
        if (requestId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(messageIdMd5);
        if (messageIdMd5.Length != ListenerProtocol.GetRequestPayloadLength)
        {
            throw new ArgumentException("MessageIdMd5 must be exactly 32 characters.", nameof(messageIdMd5));
        }

        var payload = Encoding.ASCII.GetBytes(messageIdMd5);
        if (!ListenerProtocolParser.IsValidCanonicalMessageIdMd5Payload(payload.AsSpan()))
        {
            throw new ArgumentException("MessageIdMd5 must be lowercase hexadecimal.", nameof(messageIdMd5));
        }

        var frame = new byte[ListenerProtocol.HeaderLengthBytes + ListenerProtocol.GetRequestPayloadLength];
        new ListenerFrameHeader(
                ListenerProtocol.Version1,
                ListenerOpcode.GetRequest,
                ListenerProtocol.HeaderLengthBytes,
                requestId,
                ListenerProtocol.GetRequestPayloadLength,
                0)
            .WriteTo(frame);
        payload.CopyTo(frame, ListenerProtocol.HeaderLengthBytes);
        return frame;
    }

    /// <summary>Encodes a Found header that is written immediately before <paramref name="payload"/>.</summary>
    public static ListenerFoundResponseFrame EncodeGetResponseFound(uint requestId, ReadOnlyMemory<byte> payload)
    {
        if (requestId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        var headerBytes = new byte[ListenerProtocol.HeaderLengthBytes];
        new ListenerFrameHeader(
                ListenerProtocol.Version1,
                ListenerOpcode.GetResponseFound,
                ListenerProtocol.HeaderLengthBytes,
                requestId,
                checked((uint)payload.Length),
                0)
            .WriteTo(headerBytes);
        return new ListenerFoundResponseFrame(headerBytes, payload);
    }

    /// <summary>Encodes GetResponseNotFound with reason 0x00.</summary>
    public static byte[] EncodeGetResponseNotFound(uint requestId)
    {
        if (requestId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        var frame = new byte[ListenerProtocol.HeaderLengthBytes + ListenerProtocol.GetResponseNotFoundPayloadLength];
        new ListenerFrameHeader(
                ListenerProtocol.Version1,
                ListenerOpcode.GetResponseNotFound,
                ListenerProtocol.HeaderLengthBytes,
                requestId,
                ListenerProtocol.GetResponseNotFoundPayloadLength,
                0)
            .WriteTo(frame);
        frame[ListenerProtocol.HeaderLengthBytes] = ListenerProtocol.NotFoundReasonUnavailable;
        return frame;
    }

    /// <summary>Encodes GetResponseError with a big-endian uint16 code.</summary>
    public static byte[] EncodeGetResponseError(uint requestId, ListenerProtocolErrorCode errorCode)
    {
        if (requestId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        var frame = new byte[ListenerProtocol.HeaderLengthBytes + ListenerProtocol.GetResponseErrorPayloadLength];
        new ListenerFrameHeader(
                ListenerProtocol.Version1,
                ListenerOpcode.GetResponseError,
                ListenerProtocol.HeaderLengthBytes,
                requestId,
                ListenerProtocol.GetResponseErrorPayloadLength,
                0)
            .WriteTo(frame);
        BinaryPrimitives.WriteUInt16BigEndian(
            frame.AsSpan(ListenerProtocol.HeaderLengthBytes, 2),
            (ushort)errorCode);
        return frame;
    }

    /// <summary>Encodes an empty-payload GetReceiptAck.</summary>
    public static byte[] EncodeGetReceiptAck(uint requestId)
    {
        if (requestId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        var frame = new byte[ListenerProtocol.HeaderLengthBytes];
        new ListenerFrameHeader(
                ListenerProtocol.Version1,
                ListenerOpcode.GetReceiptAck,
                ListenerProtocol.HeaderLengthBytes,
                requestId,
                ListenerProtocol.GetReceiptAckPayloadLength,
                0)
            .WriteTo(frame);
        return frame;
    }
}
