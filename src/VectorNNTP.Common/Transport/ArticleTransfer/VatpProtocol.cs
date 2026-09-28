namespace VectorNNTP.Common.Transport.ArticleTransfer;

/// <summary>
/// Wire-level constants for Vector Article Transfer Protocol (VATP) version 1.
/// </summary>
/// <remarks>
/// All multi-byte integers are big-endian. Protocol data remains byte-oriented.
/// Resource limits such as concurrent streams and initial window size live in
/// <see cref="ArticleTransferLimits"/> and are not wire invariants.
/// </remarks>
public static class VatpProtocol
{
    /// <summary>Supported protocol version byte.</summary>
    public const byte Version1 = 0x01;

    /// <summary>Fixed frame header size in bytes.</summary>
    public const ushort HeaderLengthBytes = 16;

    /// <summary>Connection-level stream identifier reserved for HELLO and connection FAIL.</summary>
    public const uint ConnectionStreamId = 0;

    /// <summary>Default maximum DATA payload per frame (policy default advertised in HELLO).</summary>
    public const uint DefaultMaxFramePayload = 64 * 1024;

    /// <summary>HELLO payload size: 8-byte magic + <c>u32</c> maxFramePayload.</summary>
    public const int HelloPayloadLength = 12;

    /// <summary>OPEN payload size: 16-byte RequestId GUID + 32-byte <see cref="Articles.ArticleId"/>.</summary>
    public const int OpenPayloadLength = 48;

    /// <summary>META payload size (fixed layout).</summary>
    public const int MetaPayloadLength = 76;

    /// <summary>WINDOW payload size (<c>u32</c> credit add).</summary>
    public const int WindowPayloadLength = 4;

    /// <summary>END and CANCEL carry no payload.</summary>
    public const int EmptyPayloadLength = 0;

    /// <summary>FAIL carries at least a big-endian <c>u16</c> error code.</summary>
    public const int FailMinPayloadLength = 2;

    /// <summary>Maximum optional ASCII reason bytes after the FAIL error code.</summary>
    public const int FailMaxReasonLength = 256;

    /// <summary>Maximum FAIL payload (code + reason).</summary>
    public const int FailMaxPayloadLength = FailMinPayloadLength + FailMaxReasonLength;

    /// <summary>OPEN RequestId field length in bytes.</summary>
    public const int RequestIdLength = 16;

    /// <summary>OPEN ArticleId field length in bytes.</summary>
    public const int ArticleIdLength = 32;

    /// <summary>
    /// HELLO magic <c>VNATP01\0</c> (8 bytes).
    /// </summary>
    public static ReadOnlySpan<byte> HelloMagic => "VNATP01\0"u8;

    /// <summary>DATA Flags bit 0: last DATA frame for the stream.</summary>
    public const uint FlagFin = 1u;

    /// <summary>Flags bits that must be zero on every frame.</summary>
    public const uint ReservedFlagsMask = ~FlagFin;
}
