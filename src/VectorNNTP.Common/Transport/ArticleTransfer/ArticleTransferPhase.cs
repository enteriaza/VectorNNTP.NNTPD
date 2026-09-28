namespace VectorNNTP.Common.Transport.ArticleTransfer;

/// <summary>
/// Receiver-side phase of one VATP transfer stream.
/// </summary>
/// <remarks>
/// Valid progression: <see cref="AwaitingMeta"/> → <see cref="ReceivingData"/> →
/// <see cref="AwaitingEnd"/> → <see cref="Completed"/> (after canonical validation).
/// <see cref="AwaitingEnd"/> requires exact ArtSize bytes and FIN on the final DATA frame.
/// END then runs canonical validation. A stream is consumable only in <see cref="Completed"/>.
/// </remarks>
public enum ArticleTransferPhase : byte
{
    /// <summary>Stream accepted after OPEN; waiting for META.</summary>
    AwaitingMeta = 0,

    /// <summary>META accepted; receiving DATA bytes toward ArtSize.</summary>
    ReceivingData = 1,

    /// <summary>
    /// Exact ArtSize bytes and FIN observed. Waiting for END before canonical validation.
    /// </summary>
    AwaitingEnd = 2,

    /// <summary>
    /// Canonical validation succeeded. Exactly one <see cref="Articles.ArticleRecord"/>
    /// is available. Terminal success.
    /// </summary>
    Completed = 3,

    /// <summary>Stream failed. Terminal.</summary>
    Failed = 4,

    /// <summary>Stream cancelled. Terminal.</summary>
    Cancelled = 5,
}
