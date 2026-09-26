namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Maps an Article Work outcome to ACK/NACK and response-publication policy.
/// </summary>
public static class ArticleWorkDispositionPlanner
{
    /// <summary>
    /// Creates the settlement plan for one classified result.
    /// </summary>
    /// <param name="outcome">Processing or parse outcome.</param>
    /// <param name="replyable">
    /// Whether AMQP <c>CorrelationId</c> and <c>ReplyTo</c> are both present.
    /// Only <see cref="ArticleWorkOutcome.InvalidRequest"/> uses this to decide publication.
    /// </param>
    /// <param name="cancellationRequested">
    /// When <see langword="true"/>, forces <see cref="ArticleWorkOutcome.Cancelled"/> settlement
    /// (NACK requeue, no terminal response).
    /// </param>
    /// <returns>The disposition to apply.</returns>
    public static ArticleWorkDisposition Create(
        ArticleWorkOutcome outcome,
        bool replyable,
        bool cancellationRequested)
    {
        if (cancellationRequested)
        {
            return new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false);
        }

        return outcome switch
        {
            ArticleWorkOutcome.Success => new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: true),
            ArticleWorkOutcome.ArticleNotFound => new ArticleWorkDisposition(Acknowledge: false, Requeue: false, PublishResponse: true),
            ArticleWorkOutcome.InvalidArticle => new ArticleWorkDisposition(Acknowledge: false, Requeue: false, PublishResponse: true),
            ArticleWorkOutcome.InvalidRequest => new ArticleWorkDisposition(Acknowledge: false, Requeue: false, PublishResponse: replyable),
            ArticleWorkOutcome.ProviderFailure => new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false),
            ArticleWorkOutcome.Cancelled => new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false),
            ArticleWorkOutcome.UnexpectedFailure => new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false),
            ArticleWorkOutcome.RetentionRejected => new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false),
            _ => new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false),
        };
    }
}
