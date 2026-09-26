using VectorNNTP.BackFiller.RabbitMq;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Parses, classifies, optionally records a response intent, and settles one delivery.
/// </summary>
public sealed class ArticleWorkDeliveryPipeline
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
    public ArticleWorkDeliveryPipeline(
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
    public async Task<ArticleWorkOutcome> ProcessAsync(
        BackFillerRabbitMqConsumedDelivery delivery,
        string consumingBackbone,
        IBackFillerRabbitMqChannel channel,
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

            await PublishIfRequiredAsync(
                    invalid,
                    ArticleWorkOutcome.InvalidRequest,
                    failure.Identities.RequestId,
                    failure.Identities.MessageId,
                    failure.Identities.Backbone,
                    delivery.CorrelationId,
                    delivery.ReplyTo,
                    failure.Reason,
                    cancellationToken)
                .ConfigureAwait(false);
            await lease.TrySettleAsync(
                    invalid,
                    channelStillCurrent() && lease.IsOriginalChannel(channel),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return ArticleWorkOutcome.InvalidRequest;
        }

        var item = new ArticleWorkItem(
            parsed.Request!,
            delivery.CorrelationId!,
            delivery.ReplyTo!,
            consumingBackbone,
            lease);

        ArticleWorkOutcome outcome;
        string? error;
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

        if (disposition.PublishResponse
            && !await PublishIfRequiredAsync(
                    disposition,
                    outcome,
                    item.Request.RequestId,
                    item.Request.MessageId,
                    item.Request.Backbone,
                    item.CorrelationId,
                    item.ReplyTo,
                    error,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            var retry = new ArticleWorkDisposition(Acknowledge: false, Requeue: true, PublishResponse: false);
            await lease.TrySettleAsync(
                    retry,
                    channelStillCurrent() && lease.IsOriginalChannel(channel),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return ArticleWorkOutcome.UnexpectedFailure;
        }

        await lease.TrySettleAsync(
                disposition,
                channelStillCurrent() && lease.IsOriginalChannel(channel),
                CancellationToken.None)
            .ConfigureAwait(false);
        return outcome;
    }

    private async Task<bool> PublishIfRequiredAsync(
        ArticleWorkDisposition disposition,
        ArticleWorkOutcome outcome,
        Guid? requestId,
        string? messageId,
        string? backbone,
        string? correlationId,
        string? replyTo,
        string? error,
        CancellationToken cancellationToken)
    {
        if (!disposition.PublishResponse)
        {
            return true;
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
                        error),
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
