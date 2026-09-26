using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.RabbitMq;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.ArticleWork;

public sealed class ArticleWorkSettlementTests
{
    [Theory]
    [InlineData(ArticleWorkOutcome.Success, true, false, true)]
    [InlineData(ArticleWorkOutcome.ArticleNotFound, false, false, true)]
    [InlineData(ArticleWorkOutcome.InvalidArticle, false, false, true)]
    [InlineData(ArticleWorkOutcome.InvalidRequest, false, false, true)]
    [InlineData(ArticleWorkOutcome.ProviderFailure, false, true, false)]
    [InlineData(ArticleWorkOutcome.Cancelled, false, true, false)]
    [InlineData(ArticleWorkOutcome.UnexpectedFailure, false, true, false)]
    public void Planner_maps_terminal_and_retryable_outcomes(
        ArticleWorkOutcome outcome,
        bool acknowledge,
        bool requeue,
        bool publish)
    {
        var disposition = ArticleWorkDispositionPlanner.Create(outcome, replyable: true, cancellationRequested: false);
        Assert.Equal(acknowledge, disposition.Acknowledge);
        Assert.Equal(requeue, disposition.Requeue);
        Assert.Equal(publish, disposition.PublishResponse);
    }

    [Fact]
    public void InvalidRequest_without_reply_coordinates_does_not_publish()
    {
        var disposition = ArticleWorkDispositionPlanner.Create(
            ArticleWorkOutcome.InvalidRequest,
            replyable: false,
            cancellationRequested: false);

        Assert.False(disposition.Acknowledge);
        Assert.False(disposition.Requeue);
        Assert.False(disposition.PublishResponse);
    }

    [Fact]
    public void Cancellation_overrides_a_terminal_outcome()
    {
        var disposition = ArticleWorkDispositionPlanner.Create(
            ArticleWorkOutcome.Success,
            replyable: true,
            cancellationRequested: true);

        Assert.False(disposition.Acknowledge);
        Assert.True(disposition.Requeue);
        Assert.False(disposition.PublishResponse);
    }

    [Fact]
    public async Task Valid_work_is_admitted_as_provider_failure_without_a_fake_success()
    {
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var publisher = new RecordingArticleWorkResponsePublisher();
        var pipeline = new ArticleWorkDeliveryPipeline(
            new DeferredArticleWorkHandler(),
            publisher,
            1024);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.ProviderFailure, outcome);
        Assert.Empty(publisher.Published);
        var settlement = Assert.Single(channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.True(settlement.Requeue);
        Assert.Equal(7UL, settlement.DeliveryTag);
    }

    [Fact]
    public async Task InvalidRequest_nacks_without_requeue_and_records_a_response_intent()
    {
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var publisher = new RecordingArticleWorkResponsePublisher();
        var pipeline = new ArticleWorkDeliveryPipeline(
            new DeferredArticleWorkHandler(),
            publisher,
            1024);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Create("{"),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.InvalidRequest, outcome);
        var intent = Assert.Single(publisher.Published);
        Assert.Equal(ArticleWorkOutcome.InvalidRequest, intent.Outcome);
        Assert.Null(intent.RequestId);
        var settlement = Assert.Single(channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.False(settlement.Requeue);
    }

    [Theory]
    [InlineData(ArticleWorkOutcome.Success, true, false)]
    [InlineData(ArticleWorkOutcome.ArticleNotFound, false, false)]
    [InlineData(ArticleWorkOutcome.InvalidArticle, false, false)]
    public async Task Terminal_handler_outcomes_publish_and_settle_exactly_once(
        ArticleWorkOutcome handlerOutcome,
        bool acknowledge,
        bool requeue)
    {
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var publisher = new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true };
        var handler = new ControllableArticleWorkHandler { Outcome = handlerOutcome, Error = "test" };
        var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);
        var delivery = ArticleWorkTestDeliveries.Canonical();

        var first = await pipeline.ProcessAsync(delivery, "Giganews", channel, static () => true, CancellationToken.None);
        var lease = handler.LastItem!.Settlement;
        var second = await lease.TrySettleAsync(
            ArticleWorkDispositionPlanner.Create(handlerOutcome, true, false),
            channelStillCurrent: true,
            CancellationToken.None);

        Assert.Equal(handlerOutcome, first);
        Assert.False(second);
        Assert.Single(publisher.Published);
        var settlement = Assert.Single(channel.Settlements);
        Assert.Equal(acknowledge, settlement.Acknowledge);
        Assert.Equal(requeue, settlement.Requeue);
    }

    [Fact]
    public async Task Retryable_failures_nack_requeue_without_a_terminal_response()
    {
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var publisher = new RecordingArticleWorkResponsePublisher();
        var handler = new ControllableArticleWorkHandler { Throw = new InvalidOperationException("boom") };
        var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
        Assert.Empty(publisher.Published);
        Assert.True(Assert.Single(channel.Settlements).Requeue);
    }

    [Fact]
    public async Task Cancellation_nacks_requeue_without_a_terminal_response()
    {
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var publisher = new RecordingArticleWorkResponsePublisher();
        var pipeline = new ArticleWorkDeliveryPipeline(
            new DeferredArticleWorkHandler(),
            publisher,
            1024);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            cts.Token);

        Assert.Equal(ArticleWorkOutcome.Cancelled, outcome);
        Assert.Empty(publisher.Published);
        var settlement = Assert.Single(channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.True(settlement.Requeue);
    }

    [Fact]
    public async Task Publish_failure_is_retryable_and_does_not_ack()
    {
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var publisher = new RecordingArticleWorkResponsePublisher
        {
            CompletesSuccessPublication = true,
            PublishException = new InvalidOperationException("confirm failed"),
        };
        var handler = new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Success };
        var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
        Assert.Empty(publisher.Published);
        var settlement = Assert.Single(channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.True(settlement.Requeue);
    }

    [Fact]
    public async Task Settlement_uses_only_the_original_channel()
    {
        var original = new FakeBackFillerRabbitMqChannel(1);
        var replacement = new FakeBackFillerRabbitMqChannel(2);
        var lease = new ArticleWorkSettlementLease(original, 11, 1);
        var disposition = new ArticleWorkDisposition(Acknowledge: true, Requeue: false, PublishResponse: false);

        Assert.True(lease.IsOriginalChannel(original));
        Assert.False(lease.IsOriginalChannel(replacement));
        Assert.True(await lease.TrySettleAsync(disposition, channelStillCurrent: true, CancellationToken.None));
        Assert.Single(original.Settlements);
        Assert.Empty(replacement.Settlements);
    }

    [Fact]
    public async Task Stale_channel_cannot_settle_through_a_replacement()
    {
        var original = new FakeBackFillerRabbitMqChannel(1);
        var replacement = new FakeBackFillerRabbitMqChannel(2);
        var lease = new ArticleWorkSettlementLease(original, 4, 1);
        original.IsOpen = false;

        var settled = await lease.TrySettleAsync(
            new ArticleWorkDisposition(false, true, false),
            channelStillCurrent: lease.IsOriginalChannel(replacement),
            CancellationToken.None);

        Assert.False(settled);
        Assert.False(lease.IsSettled);
        Assert.Empty(original.Settlements);
        Assert.Empty(replacement.Settlements);
    }

    [Fact]
    public async Task Lost_generation_skips_settlement_even_when_the_old_channel_object_is_still_open()
    {
        var original = new FakeBackFillerRabbitMqChannel(1);
        var lease = new ArticleWorkSettlementLease(original, 5, 1);

        var settled = await lease.TrySettleAsync(
            new ArticleWorkDisposition(false, false, false),
            channelStillCurrent: false,
            CancellationToken.None);

        Assert.False(settled);
        Assert.False(lease.IsSettled);
        Assert.Empty(original.Settlements);
    }

    [Fact]
    public async Task Duplicate_settlement_is_prevented()
    {
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var lease = new ArticleWorkSettlementLease(channel, 3, 1);
        var nack = new ArticleWorkDisposition(false, false, false);

        Assert.True(await lease.TrySettleAsync(nack, true, CancellationToken.None));
        Assert.False(await lease.TrySettleAsync(new ArticleWorkDisposition(true, false, false), true, CancellationToken.None));
        Assert.True(lease.IsSettled);
        var settlement = Assert.Single(channel.Settlements);
        Assert.False(settlement.Acknowledge);
    }

    [Fact]
    public async Task In_flight_delivery_does_not_settle_on_a_replaced_generation()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var connections = BackFillerRabbitMqServiceTests.CreateService(factory);
        await connections.StartAsync(CancellationToken.None);
        var handler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.Success,
            Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var publisher = new RecordingArticleWorkResponsePublisher();
        var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);
        var session = new ArticleWorkConsumerSession(
            "Giganews",
            1,
            pipeline,
            connections,
            NullLogger.Instance);
        await session.StartAsync(CancellationToken.None);
        var firstChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel);

        var processing = firstChannel.DeliverAsync(ArticleWorkTestDeliveries.Canonical(generation: 1));
        await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;
        factory.LastConnection!.SimulateLost();
        await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        handler.Gate!.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));

        var replacement = new ArticleWorkConsumerSession(
            "Giganews",
            1,
            pipeline,
            connections,
            NullLogger.Instance);
        await replacement.StartAsync(CancellationToken.None);
        var secondChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(replacement.Channel);

        Assert.Empty(firstChannel.Settlements);
        Assert.Empty(secondChannel.Settlements);
        Assert.Empty(publisher.Published);
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(1, session.Generation);
        Assert.Equal(2, replacement.Generation);

        await session.DisposeAsync();
        await replacement.DisposeAsync();
        await connections.DisposeAsync();
    }
}
