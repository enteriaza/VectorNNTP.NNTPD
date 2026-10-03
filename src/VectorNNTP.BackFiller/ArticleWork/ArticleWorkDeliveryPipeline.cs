using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Parses, classifies, publishes a confirmed terminal response when required, and settles one delivery.
/// </summary>
internal sealed class ArticleWorkDeliveryPipeline
{
    private readonly IArticleWorkHandler _handler;
    private readonly IArticleWorkResponsePublisher _publisher;
    private readonly int _maxPayloadBytes;

    /// <summary>
    /// Initializes a new pipeline.
    /// </summary>
    /// <param name="handler">Admitted-work handler.</param>
    /// <param name="publisher">Response-publish seam.</param>
    /// <param name="maxPayloadBytes">Maximum accepted JSON body size.</param>
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
    /// Processes one delivery on <paramref name="channel"/> for <paramref name="consumingBackbone"/>.
    /// </summary>
    /// <param name="delivery">Consumed delivery.</param>
    /// <param name="consumingBackbone">Queue backbone context.</param>
    /// <param name="channel">Original consumer channel.</param>
    /// <param name="channelStillCurrent">
    /// Evaluated at settlement time. Must not be captured only at admission: a generation
    /// can disappear while work is in flight.
    /// </param>
    /// <param name="cancellationToken">Processing cancellation.</param>
    /// <returns>The outcome that was settled (or attempted).</returns>
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
            consumingBackbone,
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

    private async Task SettleRetryableAsync(
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

    // Execution disposition wins after a publication failure. Cancellation of the
    // publishing attempt (shutdown or confirm timeout) keeps the existing retryable
    // settlement so the in-flight stop is unchanged.
    private async Task<ArticleWorkOutcome> CompleteAfterPublishAttemptAsync(
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

    private enum ArticleWorkPublishAttempt
    {
        Confirmed,
        Cancelled,
        Failed,
    }
}
