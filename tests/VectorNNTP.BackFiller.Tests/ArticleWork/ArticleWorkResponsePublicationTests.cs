using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.RabbitMq;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.ArticleWork;

public sealed class ArticleWorkResponsePublicationTests
{
    [Fact]
    public async Task Success_publishes_confirmed_json_then_acks_exactly_once()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
        var handler = SuccessHandler();
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var pipeline = new ArticleWorkDeliveryPipeline(handler, context.Publisher, 1024);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.Success, outcome);
        var publication = Assert.Single(context.PublishChannel.Publications);
        Assert.Equal(ArticleWorkTestDeliveries.CanonicalReplyTo, publication.ReplyTo);
        Assert.Equal(ArticleWorkTestDeliveries.CanonicalCorrelationId, publication.CorrelationId);
        Assert.Equal(ArticleWorkResponseWireProtocol.JsonContentType, publication.ContentType);
        Assert.Equal(ArticleWorkTestDeliveries.CanonicalRequestId, publication.RequestIdHeader);
        Assert.Equal(ArticleWorkResponseWireProtocol.ExpirationMilliseconds, publication.ExpirationMilliseconds);
        Assert.True(Guid.TryParse(publication.MessageId, out var amqpMessageId));
        Assert.NotEqual(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), amqpMessageId);
        Assert.NotEqual(ArticleWorkTestDeliveries.CanonicalCorrelationId, publication.MessageId);
        Assert.Equal(
            ArticleWorkTestDeliveries.CanonicalSuccessResponseJson,
            Encoding.UTF8.GetString(publication.Body.Span));
        Assert.DoesNotContain("From:", Encoding.UTF8.GetString(publication.Body.Span), StringComparison.Ordinal);
        var settlement = Assert.Single(channel.Settlements);
        Assert.True(settlement.Acknowledge);
        Assert.Equal(7UL, settlement.DeliveryTag);
        Assert.DoesNotContain(
            context.PublishChannel.GetType().GetMethods(),
            static method => method.Name is "BasicAckAsync" or "BasicNackAsync");
    }

    [Fact]
    public async Task Redelivered_success_may_publish_again_but_each_delivery_settles_once()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
        var handler = SuccessHandler();
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var pipeline = new ArticleWorkDeliveryPipeline(handler, context.Publisher, 1024);

        await pipeline.ProcessAsync(ArticleWorkTestDeliveries.Canonical(deliveryTag: 7), "Giganews", channel, static () => true, CancellationToken.None);
        await pipeline.ProcessAsync(ArticleWorkTestDeliveries.Canonical(deliveryTag: 8), "Giganews", channel, static () => true, CancellationToken.None);

        Assert.Equal(2, context.PublishChannel.Publications.Count);
        Assert.Equal(2, channel.Settlements.Count);
        Assert.All(channel.Settlements, static settlement => Assert.True(settlement.Acknowledge));
        Assert.Equal(new ulong[] { 7, 8 }, channel.Settlements.Select(static settlement => settlement.DeliveryTag));
        Assert.Equal(2, handler.HandleCount);
    }

    [Fact]
    public async Task Confirm_failures_are_retryable_and_never_ack()
    {
        FakePublishConfirmBehavior[] behaviors =
        [
            FakePublishConfirmBehavior.ThrowOnPublish,
            FakePublishConfirmBehavior.Nack,
            FakePublishConfirmBehavior.Timeout,
            FakePublishConfirmBehavior.CloseChannel,
        ];

        foreach (var behavior in behaviors)
        {
            await using var context = await PublicationContext.StartAsync(behavior);
            var channel = new FakeBackFillerRabbitMqChannel(1);
            var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
            Assert.False(Assert.Single(channel.Settlements).Acknowledge);
            Assert.True(Assert.Single(channel.Settlements).Requeue);
        }
    }

    [Fact]
    public async Task Generation_disappears_during_publication_does_not_ack()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
        context.PublishChannel.AfterEnqueue = () => context.Factory.LastConnection!.SimulateLost();
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            () => context.Connections.TryGetCurrent(out var handle) && handle.Generation == 1,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
        Assert.Empty(channel.Settlements);
    }

    [Fact]
    public async Task Generation_change_after_confirm_before_ack_does_not_settle()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);
        var seen = 0;

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            () => Interlocked.Increment(ref seen) == 1,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.Success, outcome);
        Assert.Single(context.PublishChannel.Publications);
        Assert.Empty(channel.Settlements);
    }

    [Fact]
    public async Task Closed_original_channel_is_not_settled_by_a_replacement()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
        var original = new FakeBackFillerRabbitMqChannel(1);
        var replacement = new FakeBackFillerRabbitMqChannel(2);
        var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);
        original.IsOpen = false;

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            original,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.Success, outcome);
        Assert.Empty(original.Settlements);
        Assert.Empty(replacement.Settlements);
    }

    [Theory]
    [InlineData(ArticleWorkOutcome.ArticleNotFound, false)]
    [InlineData(ArticleWorkOutcome.InvalidArticle, false)]
    public async Task Terminal_failure_confirms_then_nacks_without_requeue(
        ArticleWorkOutcome handlerOutcome,
        bool requeue)
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
        var handler = new ControllableArticleWorkHandler { Outcome = handlerOutcome, Error = "missing" };
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var pipeline = new ArticleWorkDeliveryPipeline(handler, context.Publisher, 1024);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(handlerOutcome, outcome);
        using var document = JsonDocument.Parse(Assert.Single(context.PublishChannel.Publications).Body);
        Assert.Equal(handlerOutcome.ToString(), document.RootElement.GetProperty("outcome").GetString());
        Assert.False(document.RootElement.TryGetProperty("uri", out _));
        var settlement = Assert.Single(channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.Equal(requeue, settlement.Requeue);
    }

    [Fact]
    public async Task InvalidRequest_confirms_then_nacks_without_requeue()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Create("{"),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.InvalidRequest, outcome);
        using var document = JsonDocument.Parse(Assert.Single(context.PublishChannel.Publications).Body);
        Assert.Equal("InvalidRequest", document.RootElement.GetProperty("outcome").GetString());
        Assert.False(Assert.Single(channel.Settlements).Requeue);
    }

    [Fact]
    public async Task Terminal_response_confirm_failure_nacks_with_requeue()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Nack);
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var pipeline = new ArticleWorkDeliveryPipeline(
            new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.ArticleNotFound, Error = "missing" },
            context.Publisher,
            1024);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
        Assert.True(Assert.Single(channel.Settlements).Requeue);
        Assert.False(Assert.Single(channel.Settlements).Acknowledge);
    }

    [Fact]
    public async Task InvalidRequest_publish_failure_nacks_with_requeue()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.ThrowOnPublish);
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Create("{"),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
        Assert.True(Assert.Single(channel.Settlements).Requeue);
    }

    [Theory]
    [InlineData(ArticleWorkOutcome.ProviderFailure)]
    [InlineData(ArticleWorkOutcome.Cancelled)]
    [InlineData(ArticleWorkOutcome.UnexpectedFailure)]
    public async Task Retryable_outcomes_do_not_publish(ArticleWorkOutcome handlerOutcome)
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
        var handler = new ControllableArticleWorkHandler
        {
            Outcome = handlerOutcome,
            Error = "retry",
        };
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var pipeline = new ArticleWorkDeliveryPipeline(handler, context.Publisher, 1024);
        using var cts = new CancellationTokenSource();
        if (handlerOutcome == ArticleWorkOutcome.Cancelled)
        {
            cts.Cancel();
        }

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            cts.Token);

        Assert.Equal(handlerOutcome, outcome);
        Assert.Empty(context.PublishChannel.Publications);
        Assert.True(Assert.Single(channel.Settlements).Requeue);
        Assert.False(Assert.Single(channel.Settlements).Acknowledge);
    }

    [Fact]
    public async Task Stale_publisher_channel_cannot_replace_or_dispose_the_current_channel()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
        var current = context.PublishChannel;
        var stale = new FakeBackFillerRabbitMqPublishChannel(0);

        await context.Publisher.InstallPublishChannelAsync(stale);

        Assert.Same(current, context.Publisher.Channel);
        Assert.Equal(0, current.DisposeCount);
        Assert.Equal(1, stale.DisposeCount);
    }

    [Fact]
    public async Task Publisher_rebuilds_on_generation_replacement()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
        var first = context.PublishChannel;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Factory.Connected = secondConnected;
        context.Factory.LastConnection!.SimulateLost();
        await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await ArticleWorkTestDeliveries.WaitUntilAsync(
            () => context.Publisher.Generation == 2 && context.Publisher.Channel is not null,
            TimeSpan.FromSeconds(2));

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(2, context.Publisher.Generation);
        Assert.NotSame(first, context.Publisher.Channel);
    }

    [Fact]
    public async Task Startup_fails_when_a_publish_channel_cannot_be_created()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var connections = BackFillerRabbitMqServiceTests.CreateService(factory);
        await connections.StartAsync(CancellationToken.None);
        factory.LastConnection!.CreatePublishChannelException = new InvalidOperationException("no channel");
        var publisher = new ArticleWorkResponsePublisher(
            connections,
            BackFillerRabbitMqServiceTests.CreateFastRuntime(),
            NullLogger<ArticleWorkResponsePublisher>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.StartAsync(CancellationToken.None));
        Assert.Equal(ArticleWorkResponsePublisherState.Stopped, publisher.State);
        await connections.DisposeAsync();
    }

    [Fact]
    public async Task Shutdown_during_confirm_does_not_ack()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Wait);
        context.PublishChannel.Enqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);
        var processing = pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);
        await context.PublishChannel.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await context.Publisher.DisposeAsync();
        var outcome = await processing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, outcome);
        Assert.True(Assert.Single(channel.Settlements).Requeue);
        Assert.False(Assert.Single(channel.Settlements).Acknowledge);
        Assert.Equal(ArticleWorkResponsePublisherState.Stopped, context.Publisher.State);
    }

    [Fact]
    public async Task Shutdown_after_confirm_before_ack_does_not_ack_on_a_stale_context()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var pipeline = new ArticleWorkDeliveryPipeline(SuccessHandler(), context.Publisher, 1024);
        var seen = 0;

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            () => Interlocked.Increment(ref seen) == 1,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.Success, outcome);
        Assert.Single(context.PublishChannel.Publications);
        Assert.Empty(channel.Settlements);
    }

    [Fact]
    public async Task Publisher_never_settles_the_original_delivery()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Confirm);
        var intent = new ArticleWorkResponseIntent(
            ArticleWorkOutcome.Success,
            Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId),
            ArticleWorkTestDeliveries.CanonicalMessageId,
            "Giganews",
            ArticleWorkTestDeliveries.CanonicalCorrelationId,
            ArticleWorkTestDeliveries.CanonicalReplyTo,
            Error: null,
            ArticleWorkTestDeliveries.CanonicalCacheUri);
        var consumer = new FakeBackFillerRabbitMqChannel(1);

        await context.Publisher.PublishAsync(intent, CancellationToken.None);

        Assert.Empty(consumer.Settlements);
        Assert.Single(context.PublishChannel.Publications);
    }

    [Fact]
    public async Task One_delivery_is_not_retried_locally_after_publish_failure()
    {
        await using var context = await PublicationContext.StartAsync(FakePublishConfirmBehavior.Nack);
        var handler = SuccessHandler();
        var channel = new FakeBackFillerRabbitMqChannel(1);
        var pipeline = new ArticleWorkDeliveryPipeline(handler, context.Publisher, 1024);

        await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(1, handler.HandleCount);
        Assert.Single(context.PublishChannel.Publications);
        Assert.Single(channel.Settlements);
    }

    [Fact]
    public void Production_response_code_does_not_block_synchronously()
    {
        var directories = new[]
        {
            FindSourceDirectory("ArticleWork"),
            FindSourceDirectory("RabbitMq"),
        };
        foreach (var file in directories.SelectMany(static directory => Directory.GetFiles(directory, "*.cs")))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
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

    private static string FindSourceDirectory(string folder)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "VectorNNTP.BackFiller", folder);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate src/VectorNNTP.BackFiller/{folder}.");
    }

    private sealed class PublicationContext : IAsyncDisposable
    {
        private PublicationContext(
            FakeBackFillerRabbitMqConnectionFactory factory,
            BackFillerRabbitMqService connections,
            ArticleWorkResponsePublisher publisher)
        {
            Factory = factory;
            Connections = connections;
            Publisher = publisher;
        }

        public FakeBackFillerRabbitMqConnectionFactory Factory { get; }

        public BackFillerRabbitMqService Connections { get; }

        public ArticleWorkResponsePublisher Publisher { get; }

        public FakeBackFillerRabbitMqPublishChannel PublishChannel =>
            Assert.IsType<FakeBackFillerRabbitMqPublishChannel>(Publisher.Channel);

        public static async Task<PublicationContext> StartAsync(FakePublishConfirmBehavior behavior)
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = BackFillerRabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            factory.LastConnection!.DefaultPublishConfirmBehavior = behavior;
            var publisher = new ArticleWorkResponsePublisher(
                connections,
                BackFillerRabbitMqServiceTests.CreateFastRuntime(),
                NullLogger<ArticleWorkResponsePublisher>.Instance);
            await publisher.StartAsync(CancellationToken.None);
            Assert.Equal(ArticleWorkResponsePublisherState.Running, publisher.State);
            return new PublicationContext(factory, connections, publisher);
        }

        public async ValueTask DisposeAsync()
        {
            await Publisher.DisposeAsync();
            await Connections.DisposeAsync();
        }
    }
}
