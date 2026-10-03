namespace VectorNNTP.Common.Transport.ArticleTransfer
{
    /// <summary>
    /// Typed VATP protocol error codes used on FAIL frames and in typed parse/state results.
    /// </summary>
    /// <remarks>
    /// Ordinary peer validation uses these codes (or parse status enums), not exceptions.
    /// Exceptions remain for programming errors such as null arguments.
    /// </remarks>
    public enum VatpErrorCode : ushort
    {
        /// <summary>No error.</summary>
        None = 0x0000,

        /// <summary>Unsupported protocol version.</summary>
        UnsupportedVersion = 0x0001,

        /// <summary>Unknown or unsupported frame type.</summary>
        InvalidFrameType = 0x0002,

        /// <summary>HeaderLength is not 16.</summary>
        InvalidHeaderLength = 0x0003,

        /// <summary>Frame or payload length is invalid or overflows.</summary>
        InvalidFrameLength = 0x0004,

        /// <summary>Reserved flag bits are non-zero, or FIN used on a non-DATA frame.</summary>
        InvalidFlags = 0x0005,

        /// <summary>StreamId is invalid for the frame type.</summary>
        InvalidStreamId = 0x0006,

        /// <summary>HELLO magic mismatch or truncated HELLO.</summary>
        InvalidHello = 0x0007,

        /// <summary>HELLO maxFramePayload is zero or exceeds representable policy.</summary>
        InvalidMaxFramePayload = 0x0008,

        /// <summary>Payload exceeds the negotiated/permitted max frame payload.</summary>
        FrameTooLarge = 0x0009,

        /// <summary>META ArtSize exceeds <see cref="Articles.ArticleResourceLimits.MaxArticleBytes"/>.</summary>
        ArticleTooLarge = 0x000A,

        /// <summary>Invalid state transition for the stream.</summary>
        InvalidStateTransition = 0x000B,

        /// <summary>META payload is malformed or fails structural checks.</summary>
        InvalidMeta = 0x000C,

        /// <summary>Message-ID-derived ArticleId does not match OPEN ArticleId.</summary>
        ArticleIdMismatch = 0x000D,

        /// <summary>Recomputed ArtHash does not match META.</summary>
        ArtHashMismatch = 0x000E,

        /// <summary>Received DATA length does not match META.ArtSize.</summary>
        ArtSizeMismatch = 0x000F,

        /// <summary>A FieldTable range is absent with non-zero length, out of bounds, or overflows.</summary>
        InvalidFieldRange = 0x0010,

        /// <summary>Transfer ended without a complete validated CanonicalV1 record.</summary>
        IncompleteTransfer = 0x0011,

        /// <summary>DATA would exceed granted WINDOW credit.</summary>
        FlowControlViolation = 0x0012,

        /// <summary>Duplicate active StreamId or stream table overflow.</summary>
        StreamTableError = 0x0013,

        /// <summary>WINDOW targeted an unknown or terminal stream.</summary>
        UnknownStream = 0x0014,

        /// <summary>OPEN payload shape is invalid.</summary>
        InvalidOpen = 0x0015,

        /// <summary>Canonical transfer factory rejected the assembled article.</summary>
        CanonicalTransferRejected = 0x0016,

        /// <summary>Generic open rejection (unknown/expired/cancelled/consumed/wrong ArticleId).</summary>
        OpenRejected = 0x0017,

        /// <summary>Peer cancelled the stream.</summary>
        Cancelled = 0x0018,
    }
}
