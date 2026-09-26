namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Deterministic settlement plan for one Article Work delivery.
/// </summary>
/// <param name="Acknowledge">
/// <see langword="true"/> for ACK (Success after a published response).
/// <see langword="false"/> for NACK.
/// </param>
/// <param name="Requeue">NACK requeue flag. Ignored when <see cref="Acknowledge"/> is <see langword="true"/>.</param>
/// <param name="PublishResponse">
/// Whether a terminal RPC response should be attempted before settlement.
/// Retryable outcomes never publish.
/// </param>
public readonly record struct ArticleWorkDisposition(
    bool Acknowledge,
    bool Requeue,
    bool PublishResponse);
