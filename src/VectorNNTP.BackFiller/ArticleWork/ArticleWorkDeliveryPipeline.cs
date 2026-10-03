using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.ArticleWork
{
    /// <summary>
    /// Parses, classifies, publishes a confirmed terminal response when required, and settles one delivery.
    /// </summary>
    internal sealed class ArticleWorkDeliveryPipeline
    {
        /// <summary>Handler for validated work. This pipeline does not serialize calls to it.</summary>
        private readonly IArticleWorkHandler _handler;

        /// <summary>Response-publish seam. Confirm serialization belongs to the publisher.</summary>
        private readonly IArticleWorkResponsePublisher _publisher;

        /// <summary>Maximum JSON body size passed to <see cref="ArticleWorkRequestParser.Parse"/>.</summary>
        private readonly int _maxPayloadBytes;

        /// <summary>
        /// Initializes a new pipeline.
        /// </summary>
        /// <param name="handler">Admitted-work handler.</param>
        /// <param name="publisher">Response-publish seam.</param>
        /// <param name="maxPayloadBytes">Maximum accepted JSON body size. Must be at least 1.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="handler"/> or <paramref name="publisher"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxPayloadBytes"/> is less than 1.</exception>
        internal ArticleWorkDeliveryPipeline(
            IArticleWorkHandler handler,
            IArticleWorkResponsePublisher publisher,
            int maxPayloadBytes)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(publisher);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 1);
            _handler = handler;
            _publisher = publisher;
            _maxPayloadBytes = maxPayloadBytes;
        }

        /// <summary>
        /// Parses one delivery, handles a valid request, publishes a confirmed terminal response when required, and attempts settlement on <paramref name="channel"/>.
        /// </summary>
        /// <param name="delivery">Consumed manual-ack delivery.</param>
        /// <param name="consumingBackbone">Queue backbone context passed to the parser.</param>
        /// <param name="channel">Original consumer channel bound into the settlement lease.</param>
        /// <param name="channelStillCurrent">
        /// Evaluated again before publication and settlement. Must not be captured only at admission: a generation
        /// can disappear while work is in flight.
        /// </param>
        /// <param name="cancellationToken">
        /// Processing cancellation. When it is already cancelled, a valid request becomes
        /// <see cref="ArticleWorkOutcome.Cancelled"/> and the handler is not called.
        /// <see cref="OperationCanceledException"/> from the handler is classified the same way when this token is cancelled.
        /// The invalid-request path still publishes when replyable; this token only cancels that publication.
        /// </param>
        /// <returns>
        /// The handler or parse outcome when that disposition was submitted for settlement, or when the channel was already stale and settlement was skipped.
        /// Returns <see cref="ArticleWorkOutcome.UnexpectedFailure"/> when publication is cancelled, or when publication fails for a disposition that requeues; those paths NACK requeue.
        /// A handler result of <see cref="ArticleWorkOutcome.InvalidRequest"/> is treated as <see cref="ArticleWorkOutcome.UnexpectedFailure"/>.
        /// When <see cref="IArticleWorkResponsePublisher.CompletesSuccessPublication"/> is <see langword="false"/>, Success is NACK-requeued without publication, and this method still returns <see cref="ArticleWorkOutcome.Success"/>.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="consumingBackbone"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="channel"/> or <paramref name="channelStillCurrent"/> is null.</exception>
        /// <remarks>
        /// Settlement calls use <see cref="CancellationToken.None"/>. A stale <paramref name="channelStillCurrent"/> result, or a lease that is not the original channel, returns without ACK or NACK.
        /// After the handler returns, a cancelled token forces <see cref="ArticleWorkOutcome.Cancelled"/> settlement even when the handler outcome was terminal.
        /// A non-cancellation publication failure still settles a disposition that does not requeue, including ACK.
        /// Cancellation of the publication attempt always NACK-requeues and returns <see cref="ArticleWorkOutcome.UnexpectedFailure"/>.
        /// Any <see cref="ArticleWorkHandlerResult.Article"/> is disposed before settlement. Other handler exceptions become
        /// <see cref="ArticleWorkOutcome.UnexpectedFailure"/> and the error text is the exception type name.
        /// </remarks>
        internal async Task<ArticleWorkOutcome> ProcessAsync(
            RabbitMqManualAckDelivery delivery,
            string consumingBackbone,
            IRabbitMqManualAckChannel channel,
            Func<bool> channelStillCurrent,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(consumingBackbone);
            ArgumentNullException.ThrowIfNull(channel);
            ArgumentNullException.ThrowIfNull(channelStillCurrent);

            var parsed = ArticleWorkRequestParser.Parse(delivery, consumingBackbone, _maxPayloadBytes);
            var lease = new ArticleWorkSettlementLease(channel, delivery.DeliveryTag, delivery.Generation);
            var replyable = !string.IsNullOrWhiteSpace(delivery.CorrelationId)
                            && !string.IsNullOrWhiteSpace(delivery.ReplyTo);

            if (!parsed.IsValid)
            {
                var failure = parsed.Failure!;
                var invalid = ArticleWorkDispositionPlanner.Create(
                    ArticleWorkOutcome.InvalidRequest,
                    replyable,
                    cancellationRequested: false);
                if (!channelStillCurrent() || !lease.IsOriginalChannel(channel))
                {
                    return ArticleWorkOutcome.InvalidRequest;
                }

                var invalidPublished = await TryPublishIfRequiredAsync(
                        invalid,
                        ArticleWorkOutcome.InvalidRequest,
                        failure.Identities.RequestId,
                        failure.Identities.MessageId,
                        failure.Identities.Backbone,
                        delivery.CorrelationId,
                        delivery.ReplyTo,
                        failure.Reason,
                        fqdn: null,
                        vatpPort: null,
                        articleIdHex: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                return await CompleteAfterPublishAttemptAsync(
                        invalidPublished,
                        invalid,
                        ArticleWorkOutcome.InvalidRequest,
                        lease,
                        channel,
                        channelStillCurrent)
                    .ConfigureAwait(false);
            }

            var item = new ArticleWorkItem(
                parsed.Request!,
                delivery.CorrelationId!,
                delivery.ReplyTo!,
                lease);

            ArticleWorkOutcome outcome;
            string? error;
            string? fqdn = null;
            int? vatpPort = null;
            string? articleIdHex = null;
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    outcome = ArticleWorkOutcome.Cancelled;
                    error = null;
                }
                else
                {
                    var result = await _handler.HandleAsync(item, cancellationToken).ConfigureAwait(false);
                    outcome = result.Outcome == ArticleWorkOutcome.InvalidRequest
                        ? ArticleWorkOutcome.UnexpectedFailure
                        : result.Outcome;
                    error = result.Error;
                    fqdn = result.Fqdn;
                    vatpPort = result.VatpPort;
                    articleIdHex = result.ArticleId?.ToLowerHexString();
                    result.Article?.Dispose();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = ArticleWorkOutcome.Cancelled;
                error = null;
            }
            catch (Exception ex)
            {
                outcome = ArticleWorkOutcome.UnexpectedFailure;
                error = ex.GetType().Name;
            }

            var disposition = ArticleWorkDispositionPlanner.Create(
                outcome,
                replyable: true,
                cancellationToken.IsCancellationRequested || outcome == ArticleWorkOutcome.Cancelled);

            if (!channelStillCurrent() || !lease.IsOriginalChannel(channel))
            {
                return outcome;
            }

            if (outcome == ArticleWorkOutcome.Success && !_publisher.CompletesSuccessPublication)
            {
                var pending = new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false);
                await lease.TrySettleAsync(
                        pending,
                        channelStillCurrent() && lease.IsOriginalChannel(channel),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return outcome;
            }

            var published = ArticleWorkPublishAttempt.Confirmed;
            if (disposition.PublishResponse)
            {
                published = await TryPublishIfRequiredAsync(
                        disposition,
                        outcome,
                        item.Request.RequestId,
                        item.Request.MessageId,
                        item.Request.Backbone,
                        item.CorrelationId,
                        item.ReplyTo,
                        error,
                        fqdn,
                        vatpPort,
                        articleIdHex,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await CompleteAfterPublishAttemptAsync(
                    published,
                    disposition,
                    outcome,
                    lease,
                    channel,
                    channelStillCurrent)
                .ConfigureAwait(false);
        }

        /// <summary>NACK-requeues the lease when the original channel is still current.</summary>
        /// <param name="lease">Settlement lease for this delivery.</param>
        /// <param name="channel">Channel originally bound to <paramref name="lease"/>.</param>
        /// <param name="channelStillCurrent">Rechecked immediately before the NACK.</param>
        /// <returns>
        /// A task that completes when the NACK attempt finishes. The lease swallows broker failures and does not retry them.
        /// </returns>
        private static async Task SettleRetryableAsync(
            ArticleWorkSettlementLease lease,
            IRabbitMqManualAckChannel channel,
            Func<bool> channelStillCurrent)
        {
            var retry = new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false);
            await lease.TrySettleAsync(
                    retry,
                    channelStillCurrent() && lease.IsOriginalChannel(channel),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Settles after a publication attempt. A non-cancellation failure keeps a non-requeue disposition; cancellation always NACK-requeues.
        /// </summary>
        /// <param name="published">Result of the publication attempt. <see cref="ArticleWorkPublishAttempt.Confirmed"/> includes attempts that were not required.</param>
        /// <param name="disposition">Plan produced before publication.</param>
        /// <param name="outcome">Outcome that <paramref name="disposition"/> was planned from.</param>
        /// <param name="lease">Settlement lease for this delivery.</param>
        /// <param name="channel">Channel originally bound to <paramref name="lease"/>.</param>
        /// <param name="channelStillCurrent">Rechecked immediately before settlement.</param>
        /// <returns>
        /// <paramref name="outcome"/> when the original disposition is submitted.
        /// <see cref="ArticleWorkOutcome.UnexpectedFailure"/> when publication was cancelled, or failed and <paramref name="disposition"/> requeues.
        /// </returns>
        /// <remarks>
        /// Broker RPCs use <see cref="CancellationToken.None"/>. A failed publication of an ACK disposition still ACKs.
        /// </remarks>
        private static async Task<ArticleWorkOutcome> CompleteAfterPublishAttemptAsync(
            ArticleWorkPublishAttempt published,
            ArticleWorkDisposition disposition,
            ArticleWorkOutcome outcome,
            ArticleWorkSettlementLease lease,
            IRabbitMqManualAckChannel channel,
            Func<bool> channelStillCurrent)
        {
            if (published == ArticleWorkPublishAttempt.Cancelled
                || (published == ArticleWorkPublishAttempt.Failed && disposition.Requeue))
            {
                await SettleRetryableAsync(lease, channel, channelStillCurrent).ConfigureAwait(false);
                return ArticleWorkOutcome.UnexpectedFailure;
            }

            await lease.TrySettleAsync(
                    disposition,
                    channelStillCurrent() && lease.IsOriginalChannel(channel),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return outcome;
        }

        /// <summary>
        /// Publishes when <paramref name="disposition"/> requires a terminal response.
        /// </summary>
        /// <param name="disposition">Settlement plan. When it does not publish, the publisher is not called.</param>
        /// <param name="outcome">Outcome written into the response intent.</param>
        /// <param name="requestId">Recovered request id. May be null for <see cref="ArticleWorkOutcome.InvalidRequest"/>.</param>
        /// <param name="messageId">Recovered Message-ID. May be null for <see cref="ArticleWorkOutcome.InvalidRequest"/>.</param>
        /// <param name="backbone">Recovered JSON backbone. May be null for <see cref="ArticleWorkOutcome.InvalidRequest"/>.</param>
        /// <param name="correlationId">AMQP correlation id to echo. May be null when the delivery is not replyable.</param>
        /// <param name="replyTo">AMQP reply destination. May be null when the delivery is not replyable.</param>
        /// <param name="error">Failure text for a non-success outcome. Null for success.</param>
        /// <param name="fqdn">Success FQDN. Ignored unless <paramref name="outcome"/> is <see cref="ArticleWorkOutcome.Success"/>.</param>
        /// <param name="vatpPort">Success VATP port. Ignored unless <paramref name="outcome"/> is <see cref="ArticleWorkOutcome.Success"/>.</param>
        /// <param name="articleIdHex">Success article id hex. Ignored unless <paramref name="outcome"/> is <see cref="ArticleWorkOutcome.Success"/>.</param>
        /// <param name="cancellationToken">Cancellation passed to <see cref="IArticleWorkResponsePublisher.PublishAsync"/>.</param>
        /// <returns>
        /// <see cref="ArticleWorkPublishAttempt.Confirmed"/> when publication is not required or <see cref="IArticleWorkResponsePublisher.PublishAsync"/> completes.
        /// <see cref="ArticleWorkPublishAttempt.Cancelled"/> when it throws <see cref="OperationCanceledException"/>.
        /// <see cref="ArticleWorkPublishAttempt.Failed"/> when it throws any other exception. Exceptions are not propagated.
        /// </returns>
        private async Task<ArticleWorkPublishAttempt> TryPublishIfRequiredAsync(
            ArticleWorkDisposition disposition,
            ArticleWorkOutcome outcome,
            Guid? requestId,
            string? messageId,
            string? backbone,
            string? correlationId,
            string? replyTo,
            string? error,
            string? fqdn,
            int? vatpPort,
            string? articleIdHex,
            CancellationToken cancellationToken)
        {
            if (!disposition.PublishResponse)
            {
                return ArticleWorkPublishAttempt.Confirmed;
            }

            try
            {
                await _publisher.PublishAsync(
                        new ArticleWorkResponseIntent(
                            outcome,
                            requestId,
                            messageId,
                            backbone,
                            correlationId,
                            replyTo,
                            error,
                            outcome == ArticleWorkOutcome.Success ? fqdn : null,
                            outcome == ArticleWorkOutcome.Success ? vatpPort : null,
                            outcome == ArticleWorkOutcome.Success ? articleIdHex : null),
                        cancellationToken)
                    .ConfigureAwait(false);
                return ArticleWorkPublishAttempt.Confirmed;
            }
            catch (OperationCanceledException)
            {
                return ArticleWorkPublishAttempt.Cancelled;
            }
            catch (Exception)
            {
                return ArticleWorkPublishAttempt.Failed;
            }
        }

        /// <summary>Result of one terminal-response publication attempt inside this pipeline.</summary>
        private enum ArticleWorkPublishAttempt
        {
            /// <summary>Publication was not required, or the publisher completed the attempt.</summary>
            Confirmed,

            /// <summary>The publisher threw <see cref="OperationCanceledException"/>.</summary>
            Cancelled,

            /// <summary>The publisher threw an exception other than <see cref="OperationCanceledException"/>.</summary>
            Failed,
        }
    }
}
