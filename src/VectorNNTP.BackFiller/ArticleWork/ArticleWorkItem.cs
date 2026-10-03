namespace VectorNNTP.BackFiller.ArticleWork
{
    /// <summary>
    /// Admitted Article Work unit: validated request plus settlement ownership.
    /// </summary>
    /// <param name="Request">Validated application payload.</param>
    /// <param name="CorrelationId">AMQP RPC correlation identity.</param>
    /// <param name="ReplyTo">AMQP reply destination.</param>
    /// <param name="Settlement">Exactly-once lease bound to the original consumer channel.</param>
    internal sealed record ArticleWorkItem(
        ArticleWorkRequest Request,
        string CorrelationId,
        string ReplyTo,
        ArticleWorkSettlementLease Settlement);
}
