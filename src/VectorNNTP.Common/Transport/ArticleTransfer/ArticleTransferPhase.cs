namespace VectorNNTP.Common.Transport.ArticleTransfer;

/// <summary>
/// Receiver-side phase of one VATP transfer stream.
/// </summary>
/// <remarks>
/// Valid progression: <see cref="AwaitingMeta"/> → <see cref="ReceivingData"/> →
/// <see cref="AwaitingEnd"/> → <see cref="Completed"/> (after canonical validation).
/// FIN on the last DATA may skip an explicit END and enter validation directly.
/// A stream is consumable only in <see cref="Completed"/>.
/// </remarks>
public enum ArticleTransferPhase : byte
{
    /// <summary>Stream accepted after OPEN; waiting for META.</summary>
    AwaitingMeta = 0,

    /// <summary>META accepted; receiving DATA bytes toward ArtSize.</summary>
    ReceivingData = 1,

    /// <summary>
    /// All ArtSize bytes received (and FIN seen or not yet). Waiting for END when FIN
    /// was not set on the last DATA frame.
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
