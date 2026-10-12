namespace VectorNNTP.Common.Transport.ArticleTransfer
{
    /// <summary>
    /// Runtime resource policy defaults for VATP. Not wire-protocol invariants.
    /// </summary>
    /// <remarks>
    /// <see cref="Default"/> is the production value used by NNTPD, BackFiller, and StorageServer.
    /// HELLO negotiates only the maximum DATA payload, so every peer must assume this same initial
    /// credit. A per-host override would let a sender transmit more than the receiver has granted.
    /// Tests and the StorageServer benchmark pass a different instance when they need another window.
    /// </remarks>
    public sealed class ArticleTransferLimits
    {
        /// <summary>Creates limits with production-sensible defaults.</summary>
        internal ArticleTransferLimits()
        {
        }

        /// <summary>Maximum concurrent transfer streams per connection (default 64).</summary>
        internal int MaxStreamsPerConnection { get; init; } = 64;

        /// <summary>
        /// Initial per-stream send and receive credit in bytes. The production default is 1 MiB.
        /// </summary>
        /// <remarks>
        /// A 768 KiB article fits in 1 MiB, so the sender can finish that body before the first WINDOW
        /// refund. Measured 2 MiB and 4 MiB windows did not separate from 1 MiB. Credit is still capped
        /// by <see cref="MaxStreamCreditBytes"/>, and each accepted DATA chunk is refunded once.
        /// </remarks>
        internal int InitialStreamWindowBytes { get; init; } = 1024 * 1024;

        /// <summary>Default HELLO-advertised max DATA payload (default 64 KiB).</summary>
        internal uint DefaultMaxFramePayload { get; init; } = VatpProtocol.DefaultMaxFramePayload;

        /// <summary>Maximum credit a stream may hold after WINDOW adds (default 64 MiB).</summary>
        internal long MaxStreamCreditBytes { get; init; } = 64L * 1024 * 1024;

        /// <summary>Gets the shared default instance.</summary>
        internal static ArticleTransferLimits Default { get; } = new();
    }
}
