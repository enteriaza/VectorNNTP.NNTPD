using System.Buffers;
using System.Buffers.Binary;

namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// Wire-level constants for the BackFiller cache Listener retrieval protocol v1.
/// All multibyte integers are big-endian. Every frame is a 16-byte header plus payload.
/// This is a binary protocol; it has no CRLF request grammar.
/// </summary>
public static class ListenerProtocol
{
    /// <summary>Supported protocol version.</summary>
    public const byte Version1 = 0x01;

    /// <summary>Fixed header size in bytes.</summary>
    public const ushort HeaderLengthBytes = 16;

    /// <summary>GetRequest payload size (32 lowercase ASCII hex MD5 characters).</summary>
    public const uint GetRequestPayloadLength = 32;

    /// <summary>GetResponseNotFound payload size.</summary>
    public const uint GetResponseNotFoundPayloadLength = 1;

    /// <summary>GetResponseError payload size (uint16 error code).</summary>
    public const uint GetResponseErrorPayloadLength = 2;

    /// <summary>GetReceiptAck payload size.</summary>
    public const uint GetReceiptAckPayloadLength = 0;

    /// <summary>Required NotFound reason byte.</summary>
    public const byte NotFoundReasonUnavailable = 0x00;

    /// <summary>Persisted listener PFX name under <c>CertificateDirectory</c>. ACME provisioning is deferred.</summary>
    public const string ListenerPfxFileName = "backfiller-listener.pfx";

    /// <summary>Maximum outstanding RequestIds per connection.</summary>
    public const int MaxOutstandingRequests = 64;

    /// <summary>Maximum concurrent GetRequest handlers per connection.</summary>
    public const int MaxConcurrentProcessingRequests = 8;

    /// <summary>Maximum queued outbound responses per connection.</summary>
    public const int MaxOutboundResponses = 64;
}

/// <summary>Listener v1 opcodes.</summary>
public enum ListenerOpcode : byte
{
    /// <summary>Client request by MessageIdMd5.</summary>
    GetRequest = 0x01,

    /// <summary>Server Found payload.</summary>
    GetResponseFound = 0x11,

    /// <summary>Server NotFound.</summary>
    GetResponseNotFound = 0x12,

    /// <summary>Server protocol error.</summary>
    GetResponseError = 0x13,

    /// <summary>Client receipt acknowledgement.</summary>
    GetReceiptAck = 0x21,
}

/// <summary>GetResponseError payload codes.</summary>
public enum ListenerProtocolErrorCode : ushort
{
    /// <summary>Unsupported version.</summary>
    UnsupportedVersion = 0x0001,

    /// <summary>Unknown opcode.</summary>
    UnsupportedOpcode = 0x0002,

    /// <summary>HeaderLength is not 16.</summary>
    InvalidHeaderLength = 0x0003,

    /// <summary>Frame or payload length is invalid.</summary>
    InvalidFrameLength = 0x0004,

    /// <summary>RequestId is zero or unknown.</summary>
    InvalidRequestId = 0x0005,

    /// <summary>RequestId is already outstanding.</summary>
    DuplicateRequestId = 0x0006,

    /// <summary>Per-connection request table is full.</summary>
    RequestTableOverflow = 0x0007,

    /// <summary>MessageIdMd5 is not 32 lowercase hex bytes.</summary>
    InvalidMessageIdMd5 = 0x0008,

    /// <summary>Session is no longer admitting requests.</summary>
    ServerShuttingDown = 0x0009,

    /// <summary>Internal processing or queued-byte admission failed.</summary>
    InternalError = 0x000A,
}

/// <summary>
/// Fixed 16-byte header: version, opcode, headerLength, requestId, payloadLength, reserved.
/// </summary>
public readonly record struct ListenerFrameHeader(
    byte Version,
    ListenerOpcode Opcode,
    ushort HeaderLength,
    uint RequestId,
    uint PayloadLength,
    uint Reserved)
{
    /// <summary>Writes this header in network byte order.</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < ListenerProtocol.HeaderLengthBytes)
        {
            throw new ArgumentException("Destination is smaller than the protocol header.", nameof(destination));
        }

        destination[0] = Version;
        destination[1] = (byte)Opcode;
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), HeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4, 4), RequestId);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8, 4), PayloadLength);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(12, 4), Reserved);
    }

    /// <summary>Reads one header from network byte order.</summary>
    public static ListenerFrameHeader ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < ListenerProtocol.HeaderLengthBytes)
        {
            throw new ArgumentException("Source is smaller than the protocol header.", nameof(source));
        }

        return new ListenerFrameHeader(
            source[0],
            (ListenerOpcode)source[1],
            BinaryPrimitives.ReadUInt16BigEndian(source.Slice(2, 2)),
            BinaryPrimitives.ReadUInt32BigEndian(source.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(source.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(source.Slice(12, 4)));
    }
}

/// <summary>One parsed frame.</summary>
public readonly record struct ListenerParsedFrame(ListenerFrameHeader Header, ReadOnlySequence<byte> Payload);

/// <summary>Incremental parse status.</summary>
public enum ListenerFrameParseStatus
{
    /// <summary>Need more bytes.</summary>
    Incomplete = 0,

    /// <summary>One valid frame.</summary>
    Success = 1,

    /// <summary>One invalid frame.</summary>
    Invalid = 2,
}

/// <summary>Detailed parse failure.</summary>
public enum ListenerFrameParseError
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>Unsupported version.</summary>
    UnsupportedVersion = 1,

    /// <summary>Unsupported opcode.</summary>
    UnsupportedOpcode = 2,

    /// <summary>Invalid header length.</summary>
    InvalidHeaderLength = 3,

    /// <summary>Non-zero reserved field.</summary>
    InvalidReserved = 4,

    /// <summary>Invalid frame length.</summary>
    InvalidFrameLength = 5,

    /// <summary>Invalid RequestId.</summary>
    InvalidRequestId = 6,

    /// <summary>Invalid MessageIdMd5.</summary>
    InvalidMessageIdMd5 = 7,
}

/// <summary>Result of one parse attempt.</summary>
public readonly record struct ListenerFrameParseResult(
    ListenerFrameParseStatus Status,
    ListenerFrameParseError Error,
    long ConsumedBytes,
    ListenerParsedFrame? Frame)
{
    /// <summary>Creates an incomplete result.</summary>
    public static ListenerFrameParseResult Incomplete() =>
        new(ListenerFrameParseStatus.Incomplete, ListenerFrameParseError.None, 0, null);

    /// <summary>Creates a success result.</summary>
    public static ListenerFrameParseResult Success(long consumedBytes, ListenerParsedFrame frame) =>
        new(ListenerFrameParseStatus.Success, ListenerFrameParseError.None, consumedBytes, frame);

    /// <summary>Creates an invalid result that consumes one declared frame when available.</summary>
    public static ListenerFrameParseResult Invalid(ListenerFrameParseError error, long consumedBytes) =>
        new(ListenerFrameParseStatus.Invalid, error, consumedBytes, null);
}

/// <summary>Found response header plus caller-owned payload (no article-sized copy).</summary>
public readonly record struct ListenerFoundResponseFrame(ReadOnlyMemory<byte> Header, ReadOnlyMemory<byte> Payload);
