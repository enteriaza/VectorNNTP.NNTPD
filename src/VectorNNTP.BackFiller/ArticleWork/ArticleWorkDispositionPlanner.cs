namespace VectorNNTP.BackFiller.ArticleWork
{
    /// <summary>
    /// Maps an Article Work outcome to ACK/NACK and response-publication policy.
    /// </summary>
    internal static class ArticleWorkDispositionPlanner
    {
        /// <summary>
        /// Creates the settlement plan for one classified result.
        /// </summary>
        /// <param name="outcome">Processing or parse the outcome.</param>
        /// <param name="replyable">
        /// Whether AMQP <c>CorrelationId</c> and <c>ReplyTo</c> are both present.
        /// Only <see cref="ArticleWorkOutcome.InvalidRequest"/> uses this to decide publication.
        /// </param>
        /// <param name="cancellationRequested">
        /// When <see langword="true"/>, forces <see cref="ArticleWorkOutcome.Cancelled"/> settlement
        /// (NACK requeue, no terminal response) and ignores <paramref name="outcome"/>.
        /// </param>
        /// <returns>
        /// The disposition to apply. An unrecognized <paramref name="outcome"/> is NACK requeue without publication.
        /// </returns>
        internal static ArticleWorkDisposition Create(
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
                // Definitive absence / unusable article: processing completed successfully.
                // ACK removes the message; NNTPD may publish a NEW request to another backbone.
                ArticleWorkOutcome.ArticleNotFound => new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: true),
                ArticleWorkOutcome.InvalidArticle => new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: true),
                ArticleWorkOutcome.InvalidRequest => new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: replyable),
                // Known retryable outcomes. The same settlement as an unrecognized value, and listed as a
                // later change to the discard arm cannot retarget them.
                ArticleWorkOutcome.ProviderFailure
                    or ArticleWorkOutcome.Cancelled
                    or ArticleWorkOutcome.UnexpectedFailure
                    or ArticleWorkOutcome.RetentionRejected =>
                    new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false),
                _ => new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false),
            };
        }
    }
}
