namespace VectorNNTP.BackFiller.ArticleWork
{
    /// <summary>
    /// Deterministic settlement plan for one Article Work delivery.
    /// </summary>
    /// <param name="Acknowledge">
    /// <see langword="true"/> when the plan is Basic.Ack.
    /// <see langword="false"/> when the plan is Basic.Nack.
    /// </param>
    /// <param name="Requeue">NACK requeue flag. Ignored when <see cref="Acknowledge"/> is <see langword="true"/>.</param>
    /// <param name="PublishResponse">
    /// Whether a terminal RPC response should be attempted before settlement.
    /// Retryable outcomes are never published.
    /// </param>
    internal readonly record struct ArticleWorkDisposition(
        bool Acknowledge,
        bool Requeue,
        bool PublishResponse);
}
