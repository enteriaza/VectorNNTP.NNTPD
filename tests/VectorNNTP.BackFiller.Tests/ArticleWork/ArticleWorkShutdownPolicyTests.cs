using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.RabbitMq;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.ArticleWork;

public sealed class ArticleWorkShutdownPolicyTests
{
    [Fact]
    public async Task Drain_true_finish_true_queued_work_starts_and_active_work_finishes()
    {
        var firstStarted = NewSource();
        var firstGate = NewSource();
        var secondStarted = NewSource();
        var handler = SuccessHandler();
        handler.Stages.Enqueue(new ArticleWorkControlStage(firstStarted, firstGate, ArticleWorkOutcome.Success, CacheUri: ArticleWorkTestDeliveries.CanonicalCacheUri));
        handler.Stages.Enqueue(new ArticleWorkControlStage(secondStarted, Gate: null, ArticleWorkOutcome.Success, CacheUri: ArticleWorkTestDeliveries.CanonicalCacheUri));
        await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: true, prefetch: 2);
        var first = context.Deliver(7);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = context.Deliver(8);
        await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);

        var retire = context.Session.RetireAsync();
        await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);
        firstGate.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, handler.HandleCount);
        Assert.Equal(2, context.Channel.Settlements.Count);
        Assert.All(context.Channel.Settlements, static settlement => Assert.True(settlement.Acknowledge));
        Assert.Equal(2, context.Publisher.Published.Count);
        Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
    }

    [Fact]
    public async Task Drain_true_finish_false_queued_work_may_start_and_active_work_is_cancelled()
    {
        var handler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.Success,
            CacheUri = ArticleWorkTestDeliveries.CanonicalCacheUri,
            Started = NewSource(),
            Gate = NewSource(),
        };
        await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: false, prefetch: 2);
        var first = context.Deliver(7);
        await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = context.Deliver(8);
        await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);

        var retire = context.Session.RetireAsync();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, handler.HandleCount);
        Assert.Equal(2, context.Channel.Settlements.Count);
        Assert.All(context.Channel.Settlements, static settlement =>
        {
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        });
        Assert.Empty(context.Publisher.Published);
    }

    [Fact]
    public async Task Drain_false_finish_true_queued_work_is_requeued_while_active_work_finishes()
    {
        var handler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.Success,
            CacheUri = ArticleWorkTestDeliveries.CanonicalCacheUri,
            Started = NewSource(),
            Gate = NewSource(),
        };
        await using var context = await ShutdownContext.StartAsync(handler, drainQueued: false, finishActive: true, prefetch: 2);
        var first = context.Deliver(7);
        await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = context.Deliver(8);
        await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);

        var retire = context.Session.RetireAsync();
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, handler.HandleCount);
        var queued = Assert.Single(context.Channel.Settlements);
        Assert.Equal(8UL, queued.DeliveryTag);
        Assert.False(queued.Acknowledge);
        Assert.True(queued.Requeue);
        Assert.Empty(context.Publisher.Published);

        handler.Gate!.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, context.Channel.Settlements.Count);
        var active = context.Channel.Settlements.Single(static settlement => settlement.DeliveryTag == 7);
        Assert.True(active.Acknowledge);
        Assert.False(active.Requeue);
        Assert.Single(context.Publisher.Published);
        Assert.DoesNotContain(context.Channel.Settlements, static settlement => settlement.DeliveryTag == 8 && settlement.Acknowledge);
    }

    [Fact]
    public async Task Drain_false_finish_false_queued_work_does_not_start_and_active_work_is_cancelled()
    {
        var handler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.Success,
            CacheUri = ArticleWorkTestDeliveries.CanonicalCacheUri,
            Started = NewSource(),
            Gate = NewSource(),
        };
        await using var context = await ShutdownContext.StartAsync(handler, drainQueued: false, finishActive: false, prefetch: 2);
        var first = context.Deliver(7);
        await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = context.Deliver(8);
        await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);

        var retire = context.Session.RetireAsync();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, handler.HandleCount);
        Assert.Equal(2, context.Channel.Settlements.Count);
        Assert.All(context.Channel.Settlements, static settlement =>
        {
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        });
        Assert.Empty(context.Publisher.Published);
        Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
    }

    [Fact]
    public async Task Grace_expiry_cancels_finish_true_work_and_does_not_block_shutdown()
    {
        var handler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.Success,
            CacheUri = ArticleWorkTestDeliveries.CanonicalCacheUri,
            Started = NewSource(),
            Gate = NewSource(),
        };
        await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: true);
        var processing = context.Deliver(7);
        await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var grace = new CancellationTokenSource();
        var retire = context.Session.RetireAsync(grace.Token);
        await grace.CancelAsync();
        await retire.WaitAsync(TimeSpan.FromSeconds(2));
        await processing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
        Assert.DoesNotContain(context.Channel.Settlements, static settlement => settlement.Acknowledge);
        Assert.Empty(context.Publisher.Published);
    }

    [Fact]
    public async Task Shutdown_during_queued_to_active_transition_uses_queued_policy()
    {
        var handler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.Success,
            CacheUri = ArticleWorkTestDeliveries.CanonicalCacheUri,
        };
        await using var context = await ShutdownContext.StartAsync(handler, drainQueued: false, finishActive: true);
        context.Session.DispatchAcquired = NewSource();
        context.Session.DispatchAcquireHold = NewSource();
        var processing = context.Deliver(7);
        await context.Session.DispatchAcquired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, context.Session.ActiveCount);
        Assert.Equal(1, context.Session.QueuedCount);

        var retire = context.Session.RetireAsync();
        context.Session.DispatchAcquireHold.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, handler.HandleCount);
        var settlement = Assert.Single(context.Channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.True(settlement.Requeue);
    }

    [Fact]
    public async Task Shutdown_as_provider_retrieval_completes_does_not_ack()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        var block = NewSource();
        var server = harness.EnqueueArticle("From: a@b\r\n\r\nbody"u8.ToArray(), block);
        await using var context = await ShutdownContext.StartAsync(
            harness.Handler,
            harness.Publisher,
            drainQueued: true,
            finishActive: false,
            connections: harness.Connections);
        var processing = context.Deliver(7);
        await server.ArticleStarted!.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var retire = context.Session.RetireAsync();
        await ArticleWorkTestDeliveries.WaitUntilAsync(
            () => context.Session.WorkCancellationToken.IsCancellationRequested,
            TimeSpan.FromSeconds(2));
        block.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.DoesNotContain(context.Channel.Settlements, static settlement => settlement.Acknowledge);
        Assert.Empty(harness.PublishChannel.Publications);
    }

    [Fact]
    public async Task Shutdown_after_retention_before_publish_keeps_the_article_and_does_not_ack()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        harness.EnqueueArticle("From: a@b\r\n\r\nbody"u8.ToArray());
        var publisher = new GatedArticleWorkResponsePublisher { Gate = NewSource() };
        await using var context = await ShutdownContext.StartAsync(
            harness.Handler,
            publisher,
            drainQueued: true,
            finishActive: false,
            connections: harness.Connections);
        var processing = context.Deliver(7);
        await publisher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, harness.Retention.RetainedCount);

        var retire = context.Session.RetireAsync();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        var settlement = Assert.Single(context.Channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.True(settlement.Requeue);
        Assert.Empty(publisher.Published);
        Assert.Equal(1, harness.Retention.RetainedCount);
    }

    [Fact]
    public async Task Shutdown_after_publish_before_confirm_does_not_ack()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync(FakePublishConfirmBehavior.Wait);
        harness.EnqueueArticle("From: a@b\r\n\r\nbody"u8.ToArray());
        harness.PublishChannel.Enqueued = NewSource();
        await using var context = await ShutdownContext.StartAsync(
            harness.Handler,
            harness.Publisher,
            drainQueued: true,
            finishActive: false,
            connections: harness.Connections);
        var processing = context.Deliver(7);
        await harness.PublishChannel.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var retire = context.Session.RetireAsync();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(Assert.Single(context.Channel.Settlements).Acknowledge);
        Assert.True(context.Channel.Settlements[0].Requeue);
    }

    [Fact]
    public async Task Shutdown_after_confirm_before_ack_still_acks_when_the_context_is_current()
    {
        var handler = SuccessHandler();
        var publisher = new GatedArticleWorkResponsePublisher { AfterConfirmHold = NewSource() };
        await using var context = await ShutdownContext.StartAsync(handler, publisher, drainQueued: true, finishActive: false);
        var processing = context.Deliver(7);
        await publisher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(publisher.Published);

        var retire = context.Session.RetireAsync();
        publisher.AfterConfirmHold.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(Assert.Single(context.Channel.Settlements).Acknowledge);
    }

    [Fact]
    public async Task Shutdown_during_ack_does_not_double_settle()
    {
        var handler = SuccessHandler();
        await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: true);
        context.Channel.AckStarted = NewSource();
        context.Channel.AckGate = NewSource();
        var processing = context.Deliver(7);
        await context.Channel.AckStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var retire = context.Session.RetireAsync();
        context.Channel.AckGate.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(Assert.Single(context.Channel.Settlements).Acknowledge);
    }

    [Fact]
    public async Task Shutdown_during_provider_session_acquisition_does_not_ack()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        harness.Nntp.ConnectStarted = NewSource();
        harness.Nntp.BlockConnect = NewSource();
        await using var context = await ShutdownContext.StartAsync(
            harness.Handler,
            harness.Publisher,
            drainQueued: true,
            finishActive: false,
            connections: harness.Connections);
        var processing = context.Deliver(7);
        await harness.Nntp.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var retire = context.Session.RetireAsync();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(Assert.Single(context.Channel.Settlements).Acknowledge);
        Assert.True(context.Channel.Settlements[0].Requeue);
        Assert.Empty(harness.PublishChannel.Publications);
    }

    [Fact]
    public async Task Consumer_retirement_does_not_cancel_an_in_flight_mysql_refresh()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        var runtime = BackFillerRabbitMqServiceTests.CreateFastRuntime() with
        {
            Shutdown = new BackFillerShutdownRuntimeOptions(TimeSpan.FromSeconds(30), DrainQueuedWork: false, FinishActiveArticles: false),
        };
        var consumer = new ArticleWorkConsumerService(
            harness.Connections,
            runtime,
            harness.Handler,
            harness.Publisher,
            NullLogger<ArticleWorkConsumerService>.Instance);
        await consumer.StartAsync(CancellationToken.None);
        harness.Accounts.Block = NewSource();
        harness.Accounts.BlockAfterQueryCount = harness.Accounts.QueryCount + 1;
        using var queryCts = new CancellationTokenSource();
        var query = harness.AccountService.RefreshOnceAsync(queryCts.Token);
        await ArticleWorkTestDeliveries.WaitUntilAsync(
            () => harness.AccountService.RefreshInProgress,
            TimeSpan.FromSeconds(2));

        await consumer.StopAsync(CancellationToken.None);
        Assert.True(harness.AccountService.RefreshInProgress);
        Assert.False(query.IsCompleted);

        await queryCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.WaitAsync(TimeSpan.FromSeconds(2)));
        harness.Accounts.Block.TrySetResult();
        await consumer.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_shutdown_work_is_not_acked_and_is_requeued_once()
    {
        var handler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.Success,
            Started = NewSource(),
            Gate = NewSource(),
        };
        await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: false);
        var processing = context.Deliver(7);
        await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await context.Session.RetireAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await processing.WaitAsync(TimeSpan.FromSeconds(2));

        var settlement = Assert.Single(context.Channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.True(settlement.Requeue);
        Assert.False(settlement.Acknowledge);
    }

    [Fact]
    public async Task Terminal_shutdown_settlement_does_not_disappear()
    {
        var handler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.ArticleNotFound,
            Error = "missing",
            Started = NewSource(),
            Gate = NewSource(),
        };
        await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: true);
        context.Publisher.CompletesSuccessPublication = true;
        var processing = context.Deliver(7);
        await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var retire = context.Session.RetireAsync();
        handler.Gate!.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        var settlement = Assert.Single(context.Channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.False(settlement.Requeue);
        Assert.Single(context.Publisher.Published);
    }

    [Fact]
    public async Task One_active_completion_does_not_settle_another_delivery()
    {
        var firstStarted = NewSource();
        var firstGate = NewSource();
        var secondStarted = NewSource();
        var secondGate = NewSource();
        var handler = SuccessHandler();
        handler.Stages.Enqueue(new ArticleWorkControlStage(firstStarted, firstGate, ArticleWorkOutcome.Success, CacheUri: ArticleWorkTestDeliveries.CanonicalCacheUri));
        handler.Stages.Enqueue(new ArticleWorkControlStage(secondStarted, secondGate, ArticleWorkOutcome.Success, CacheUri: ArticleWorkTestDeliveries.CanonicalCacheUri));
        await using var context = await ShutdownContext.StartAsync(handler, drainQueued: true, finishActive: true, prefetch: 2);
        var first = context.Deliver(7);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = context.Deliver(8);
        await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);

        firstGate.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(context.Channel.Settlements);
        Assert.Equal(7UL, context.Channel.Settlements[0].DeliveryTag);

        secondGate.TrySetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, context.Channel.Settlements.Count);
    }

    [Fact]
    public async Task Retiring_one_session_does_not_cancel_unrelated_active_work()
    {
        var giganewsHandler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.Success,
            CacheUri = ArticleWorkTestDeliveries.CanonicalCacheUri,
            Started = NewSource(),
            Gate = NewSource(),
        };
        var ewekaHandler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.Success,
            CacheUri = ArticleWorkTestDeliveries.CanonicalCacheUri,
            Started = NewSource(),
            Gate = NewSource(),
        };
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var connections = BackFillerRabbitMqServiceTests.CreateService(factory);
        await connections.StartAsync(CancellationToken.None);
        var giganewsPublisher = new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true };
        var ewekaPublisher = new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true };
        var shutdown = new BackFillerShutdownRuntimeOptions(TimeSpan.FromSeconds(30), DrainQueuedWork: true, FinishActiveArticles: false);
        var giganews = new ArticleWorkConsumerSession(
            "Giganews",
            1,
            new ArticleWorkDeliveryPipeline(giganewsHandler, giganewsPublisher, 1024),
            connections,
            NullLogger.Instance,
            shutdown);
        var eweka = new ArticleWorkConsumerSession(
            "Eweka",
            1,
            new ArticleWorkDeliveryPipeline(ewekaHandler, ewekaPublisher, 1024),
            connections,
            NullLogger.Instance,
            shutdown);
        await giganews.StartAsync(CancellationToken.None);
        await eweka.StartAsync(CancellationToken.None);
        var giganewsChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(giganews.Channel);
        var ewekaChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(eweka.Channel);
        var giganewsWork = giganewsChannel.DeliverAsync(ArticleWorkTestDeliveries.Canonical(7));
        var ewekaWork = ewekaChannel.DeliverAsync(
            ArticleWorkTestDeliveries.Create(
                ArticleWorkTestDeliveries.ProtocolValidExampleJson,
                requestIdHeader: "d0648b54-b1b8-4717-95e1-7b31bf7fd1bd",
                deliveryTag: 8));
        await giganewsHandler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await ewekaHandler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await giganews.RetireAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await giganewsWork.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(Assert.Single(giganewsChannel.Settlements).Acknowledge);
        Assert.Empty(ewekaChannel.Settlements);

        ewekaHandler.Gate!.TrySetResult();
        await ewekaWork.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(Assert.Single(ewekaChannel.Settlements).Acknowledge);

        await giganews.DisposeAsync();
        await eweka.DisposeAsync();
        await connections.DisposeAsync();
    }

    [Fact]
    public async Task Multiple_queued_deliveries_obey_drain_queued_work_consistently()
    {
        var handler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.Success,
            CacheUri = ArticleWorkTestDeliveries.CanonicalCacheUri,
            Started = NewSource(),
            Gate = NewSource(),
        };
        await using var context = await ShutdownContext.StartAsync(handler, drainQueued: false, finishActive: true, prefetch: 3);
        var first = context.Deliver(7);
        await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = context.Deliver(8);
        var third = context.Deliver(9);
        await WaitQueuedActiveAsync(context.Session, queued: 2, active: 1);

        var retire = context.Session.RetireAsync();
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        await third.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, handler.HandleCount);
        Assert.Equal(2, context.Channel.Settlements.Count);
        Assert.All(context.Channel.Settlements, static settlement =>
        {
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);
        });

        handler.Gate!.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, context.Channel.Settlements.Count);
        Assert.True(context.Channel.Settlements.Single(static settlement => settlement.DeliveryTag == 7).Acknowledge);
        Assert.All(
            context.Channel.Settlements.Where(static settlement => settlement.DeliveryTag != 7),
            static settlement => Assert.False(settlement.Acknowledge));
    }

    [Fact]
    public async Task Runtime_shutdown_snapshot_is_used_and_does_not_mutate_during_retirement()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.Shutdown.DrainQueuedWork = false;
        options.Shutdown.FinishActiveArticles = false;
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            options,
            BackFillerTestOptions.CreateValidConnectionStrings());
        var handler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.Success,
            Started = NewSource(),
            Gate = NewSource(),
        };
        await using var context = await ShutdownContext.StartAsync(
            handler,
            new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true },
            runtime.Shutdown,
            prefetch: 2);
        Assert.Same(runtime.Shutdown, context.Session.Shutdown);
        var first = context.Deliver(7);
        await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = context.Deliver(8);
        await WaitQueuedActiveAsync(context.Session, queued: 1, active: 1);

        options.Shutdown.DrainQueuedWork = true;
        options.Shutdown.FinishActiveArticles = true;
        var retire = context.Session.RetireAsync();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(context.Session.Shutdown.DrainQueuedWork);
        Assert.False(context.Session.Shutdown.FinishActiveArticles);
        Assert.Equal(1, handler.HandleCount);
        Assert.All(context.Channel.Settlements, static settlement => Assert.False(settlement.Acknowledge));
    }

    [Fact]
    public async Task Consumer_service_stop_uses_the_runtime_snapshot()
    {
        var handler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.Success,
            Started = NewSource(),
            Gate = NewSource(),
        };
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var connections = BackFillerRabbitMqServiceTests.CreateService(factory);
        await connections.StartAsync(CancellationToken.None);
        var runtime = BackFillerRabbitMqServiceTests.CreateFastRuntime() with
        {
            Shutdown = new BackFillerShutdownRuntimeOptions(TimeSpan.FromSeconds(30), DrainQueuedWork: true, FinishActiveArticles: false),
        };
        var consumer = new ArticleWorkConsumerService(
            connections,
            runtime,
            handler,
            new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true },
            NullLogger<ArticleWorkConsumerService>.Instance);
        await consumer.StartAsync(CancellationToken.None);
        var session = consumer.Sessions.Single(static item => item.Backbone == "Giganews");
        Assert.False(session.Shutdown.FinishActiveArticles);
        var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel);
        var processing = channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());
        await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await consumer.StopAsync(CancellationToken.None);
        await processing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(Assert.Single(channel.Settlements).Acknowledge);
        await consumer.DisposeAsync();
        await connections.DisposeAsync();
    }

    [Fact]
    public void Shutdown_policy_is_not_read_from_mutable_options_per_request()
    {
        var directory = FindArticleWorkSourceDirectory();
        foreach (var name in new[] { "ArticleWorkConsumerSession.cs", "ArticleWorkConsumerService.cs" })
        {
            var text = File.ReadAllText(Path.Combine(directory, name));
            Assert.DoesNotContain("IOptions<", text, StringComparison.Ordinal);
            Assert.DoesNotContain("GetRequiredService<BackFillerOptions>", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
        }
    }

    private static ControllableArticleWorkHandler SuccessHandler() =>
        new()
        {
            Outcome = ArticleWorkOutcome.Success,
            CacheUri = ArticleWorkTestDeliveries.CanonicalCacheUri,
        };

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task WaitQueuedActiveAsync(ArticleWorkConsumerSession session, int queued, int active) =>
        ArticleWorkTestDeliveries.WaitUntilAsync(
            () => session.QueuedCount == queued && session.ActiveCount == active,
            TimeSpan.FromSeconds(2));

    private static string FindArticleWorkSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "VectorNNTP.BackFiller", "ArticleWork");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/VectorNNTP.BackFiller/ArticleWork.");
    }

    private sealed class ShutdownContext : IAsyncDisposable
    {
        private ShutdownContext(
            BackFillerRabbitMqService? connections,
            bool ownsConnections,
            ArticleWorkConsumerSession session,
            FakeBackFillerRabbitMqChannel channel,
            RecordingArticleWorkResponsePublisher? recordingPublisher)
        {
            Connections = connections;
            OwnsConnections = ownsConnections;
            Session = session;
            Channel = channel;
            Publisher = recordingPublisher ?? new RecordingArticleWorkResponsePublisher();
        }

        public BackFillerRabbitMqService? Connections { get; }

        public bool OwnsConnections { get; }

        public ArticleWorkConsumerSession Session { get; }

        public FakeBackFillerRabbitMqChannel Channel { get; }

        public RecordingArticleWorkResponsePublisher Publisher { get; }

        public Task Deliver(ulong deliveryTag) =>
            Channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical(deliveryTag));

        public static Task<ShutdownContext> StartAsync(
            IArticleWorkHandler handler,
            bool drainQueued,
            bool finishActive,
            ushort prefetch = 1) =>
            StartAsync(
                handler,
                new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = true },
                new BackFillerShutdownRuntimeOptions(TimeSpan.FromSeconds(30), drainQueued, finishActive),
                prefetch);

        public static Task<ShutdownContext> StartAsync(
            IArticleWorkHandler handler,
            IArticleWorkResponsePublisher publisher,
            bool drainQueued,
            bool finishActive,
            IBackFillerRabbitMqService? connections = null) =>
            StartAsync(
                handler,
                publisher,
                new BackFillerShutdownRuntimeOptions(TimeSpan.FromSeconds(30), drainQueued, finishActive),
                prefetch: 1,
                connections);

        public static async Task<ShutdownContext> StartAsync(
            IArticleWorkHandler handler,
            IArticleWorkResponsePublisher publisher,
            BackFillerShutdownRuntimeOptions shutdown,
            ushort prefetch = 1,
            IBackFillerRabbitMqService? connections = null)
        {
            BackFillerRabbitMqService? owned = null;
            IBackFillerRabbitMqService service;
            if (connections is null)
            {
                var factory = new FakeBackFillerRabbitMqConnectionFactory();
                owned = BackFillerRabbitMqServiceTests.CreateService(factory);
                await owned.StartAsync(CancellationToken.None);
                service = owned;
            }
            else
            {
                service = connections;
            }

            var recording = publisher as RecordingArticleWorkResponsePublisher;
            var session = new ArticleWorkConsumerSession(
                "Giganews",
                prefetch,
                new ArticleWorkDeliveryPipeline(handler, publisher, 1024),
                service,
                NullLogger.Instance,
                shutdown);
            await session.StartAsync(CancellationToken.None);
            var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(session.Channel);
            return new ShutdownContext(owned, owned is not null, session, channel, recording);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            if (OwnsConnections && Connections is not null)
            {
                await Connections.DisposeAsync();
            }
        }
    }
}
