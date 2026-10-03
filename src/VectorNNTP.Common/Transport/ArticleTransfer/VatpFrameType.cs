namespace VectorNNTP.Common.Transport.ArticleTransfer
{
    /// <summary>
    /// VATP v1 frame types. Numeric values are stable wire assignments.
    /// </summary>
    internal enum VatpFrameType : byte
    {
        /// <summary>Connection HELLO after TLS. StreamId must be 0.</summary>
        Hello = 0x00,

        /// <summary>Client opens a transfer. StreamId must be non-zero. Payload: RequestId + ArticleId.</summary>
        Open = 0x01,

        /// <summary>Server sends canonical transfer META. StreamId must be non-zero. Payload: 76 bytes.</summary>
        Meta = 0x02,

        /// <summary>ArtData chunk. StreamId must be non-zero. Flags.FIN marks the last DATA.</summary>
        Data = 0x03,

        /// <summary>Transfer framing complete. StreamId must be non-zero. Empty payload.</summary>
        End = 0x04,

        /// <summary>Protocol or transfer failure. StreamId 0 = connection; otherwise stream-scoped.</summary>
        Fail = 0x05,

        /// <summary>Cancel an in-flight transfer. StreamId must be non-zero. Empty payload.</summary>
        Cancel = 0x06,

        /// <summary>Flow-control credit add. StreamId must be non-zero. Payload: <c>u32</c>.</summary>
        Window = 0x07,

        /// <summary>
        /// Client stores one article. StreamId must be non-zero. Payload: 32-byte <c>ArticleId</c>.
        /// </summary>
        Store = 0x08,

        /// <summary>
        /// Server placement outcome after <c>AcceptAsync</c> returns. StreamId must be non-zero.
        /// Payload: one byte, the <c>ArticleAcceptOutcome</c> value.
        /// </summary>
        Result = 0x09,
    }
}
