using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.RabbitMq;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.ArticleWork;

public sealed class ArticleWorkConsumerLifecycleTests
{
    [Fact]
    public async Task Session_starts_running_on_the_current_generation()
    {
        await using var context = await ConsumerContext.StartSessionAsync();

        Assert.Equal(ArticleWorkConsumerState.Running, context.Session.State);
        Assert.Equal(1, context.Session.Generation);
        Assert.Equal("backfiller.giganews", context.Session.Queue);
        var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(context.Session.Channel);
        Assert.Equal(1, channel.ConsumeCount);
        Assert.Equal((ushort)1, channel.LastPrefetch);
        Assert.Equal("backfiller.giganews", channel.LastQueue);
        Assert.Equal(1, context.Factory.ConnectCount);
    }

    [Fact]
    public async Task Startup_fails_when_the_connection_is_not_ready()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var connections = BackFillerRabbitMqServiceTests.CreateService(factory);
        var session = CreateSession(connections, new DeferredArticleWorkHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(CancellationToken.None));
        Assert.Equal(ArticleWorkConsumerState.Stopped, session.State);
        Assert.Equal(0, factory.ConnectCount);
    }

    [Fact]
    public async Task Startup_fails_when_consume_cannot_be_established()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var connections = BackFillerRabbitMqServiceTests.CreateService(factory);
        await connections.StartAsync(CancellationToken.None);
        factory.LastConnection!.CreateChannelException = new InvalidOperationException("channel refused");
        var session = CreateSession(connections, new DeferredArticleWorkHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(CancellationToken.None));
        Assert.Equal(ArticleWorkConsumerState.Stopped, session.State);
        await connections.DisposeAsync();
    }

    [Fact]
    public async Task Retirement_cancels_the_consumer_disposes_the_channel_and_stops()
    {
        await using var context = await ConsumerContext.StartSessionAsync();
        var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(context.Session.Channel);

        await context.Session.RetireAsync();

        Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
        Assert.Equal(1, channel.CancelCount);
        Assert.Equal(1, channel.DisposeCount);
        Assert.False(channel.IsOpen);
        Assert.Null(context.Session.Channel);
        Assert.Equal(0, context.Factory.LastConnection!.DisposeCount);
    }

    [Fact]
    public async Task Shutdown_drains_admitted_work_on_the_original_channel()
    {
        var handler = new ControllableArticleWorkHandler
        {
            Outcome = ArticleWorkOutcome.ArticleNotFound,
            Error = "missing",
            Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var context = await ConsumerContext.StartSessionAsync(handler);
        var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(context.Session.Channel);
        var processing = channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());
        await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var retire = context.Session.RetireAsync();
        handler.Gate!.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        await retire.WaitAsync(TimeSpan.FromSeconds(2));

        var settlement = Assert.Single(channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.False(settlement.Requeue);
        Assert.Equal(1, channel.DisposeCount);
        Assert.Equal(ArticleWorkConsumerState.Stopped, context.Session.State);
    }

    [Fact]
    public async Task Delivery_arriving_after_retirement_is_not_admitted()
    {
        var handler = new ControllableArticleWorkHandler { Outcome = ArticleWorkOutcome.Success };
        await using var context = await ConsumerContext.StartSessionAsync(handler);
        var channel = Assert.IsType<FakeBackFillerRabbitMqChannel>(context.Session.Channel);
        await context.Session.RetireAsync();

        await channel.DeliverAsync(ArticleWorkTestDeliveries.Canonical());

        Assert.Equal(0, handler.HandleCount);
        Assert.Empty(channel.Settlements);
        Assert.Empty(context.Publisher.Published);
    }

    [Fact]
    public async Task Consumer_service_starts_one_session_per_provider_backbone()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var connections = BackFillerRabbitMqServiceTests.CreateService(factory);
        await connections.StartAsync(CancellationToken.None);
        var consumer = CreateConsumerService(connections, new DeferredArticleWorkHandler());

        await consumer.StartAsync(CancellationToken.None);

        Assert.Equal(BackFillerRabbitMqTopology.ProviderBackbones.Count, consumer.Sessions.Count);
        Assert.All(consumer.Sessions, static session => Assert.Equal(ArticleWorkConsumerState.Running, session.State));
        Assert.Equal(BackFillerRabbitMqTopology.ProviderBackbones.Count, factory.LastConnection!.Channels.Count);
        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(
            BackFillerRabbitMqTopology.ProviderBackbones.Select(BackFillerRabbitMqTopology.ComposeProviderEntity),
            factory.LastConnection.Channels.Select(static channel => channel.LastQueue));

        await consumer.DisposeAsync();
        Assert.All(factory.LastConnection.Channels, static channel => Assert.Equal(1, channel.DisposeCount));
        Assert.Equal(0, factory.LastConnection.DisposeCount);
        await connections.DisposeAsync();
    }

    [Fact]
    public async Task Consumer_service_rebuilds_after_generation_loss_without_opening_a_competing_connection()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var connections = BackFillerRabbitMqServiceTests.CreateService(factory);
        await connections.StartAsync(CancellationToken.None);
        var consumer = CreateConsumerService(connections, new DeferredArticleWorkHandler());
        await consumer.StartAsync(CancellationToken.None);
        var firstChannels = factory.LastConnection!.Channels.ToArray();

        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;
        factory.LastConnection.SimulateLost();
        await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await ArticleWorkTestDeliveries.WaitUntilAsync(
            () => consumer.Sessions.Count == BackFillerRabbitMqTopology.ProviderBackbones.Count
                  && consumer.Sessions.All(static session => session.Generation == 2),
            TimeSpan.FromSeconds(2));

        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(2, connections.ConnectionGeneration);
        Assert.All(consumer.Sessions, static session => Assert.Equal(ArticleWorkConsumerState.Running, session.State));
        Assert.All(firstChannels, static channel => Assert.Equal(1, channel.DisposeCount));

        await consumer.DisposeAsync();
        await connections.DisposeAsync();
    }

    [Fact]
    public async Task Stale_session_does_not_mutate_a_newer_generation()
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
        var consumer = CreateConsumerService(connections, handler);
        await consumer.StartAsync(CancellationToken.None);
        var giganews = consumer.Sessions.Single(static session => session.Backbone == "Giganews");
        var staleChannel = Assert.IsType<FakeBackFillerRabbitMqChannel>(giganews.Channel);
        var processing = staleChannel.DeliverAsync(ArticleWorkTestDeliveries.Canonical(generation: 1));
        await handler.Started!.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;
        factory.LastConnection!.SimulateLost();
        await secondConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        handler.Gate!.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        await ArticleWorkTestDeliveries.WaitUntilAsync(
            () => consumer.Sessions.Count == BackFillerRabbitMqTopology.ProviderBackbones.Count
                  && consumer.Sessions.All(static session => session.Generation == 2),
            TimeSpan.FromSeconds(2));

        var current = Assert.IsType<FakeBackFillerRabbitMqChannel>(
            consumer.Sessions.Single(static session => session.Backbone == "Giganews").Channel);
        Assert.Empty(staleChannel.Settlements);
        Assert.Empty(current.Settlements);
        Assert.Equal(2, factory.ConnectCount);

        await consumer.DisposeAsync();
        await connections.DisposeAsync();
    }

    [Fact]
    public void Production_article_work_code_does_not_block_synchronously()
    {
        var directory = FindArticleWorkSourceDirectory();
        Assert.True(Directory.Exists(directory), directory);
        foreach (var file in Directory.GetFiles(directory, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
        }
    }

    private static ArticleWorkConsumerSession CreateSession(
        IBackFillerRabbitMqService connections,
        IArticleWorkHandler handler,
        RecordingArticleWorkResponsePublisher? publisher = null)
    {
        return new ArticleWorkConsumerSession(
            "Giganews",
            1,
            new ArticleWorkDeliveryPipeline(handler, publisher ?? new RecordingArticleWorkResponsePublisher(), 1024),
            connections,
            NullLogger.Instance);
    }

    private static ArticleWorkConsumerService CreateConsumerService(
        IBackFillerRabbitMqService connections,
        IArticleWorkHandler handler)
    {
        return new ArticleWorkConsumerService(
            connections,
            BackFillerRabbitMqServiceTests.CreateFastRuntime(),
            handler,
            new RecordingArticleWorkResponsePublisher(),
            NullLogger<ArticleWorkConsumerService>.Instance);
    }

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

    private sealed class ConsumerContext : IAsyncDisposable
    {
        private ConsumerContext(
            FakeBackFillerRabbitMqConnectionFactory factory,
            BackFillerRabbitMqService connections,
            ArticleWorkConsumerSession session,
            RecordingArticleWorkResponsePublisher publisher)
        {
            Factory = factory;
            Connections = connections;
            Session = session;
            Publisher = publisher;
        }

        public FakeBackFillerRabbitMqConnectionFactory Factory { get; }

        public BackFillerRabbitMqService Connections { get; }

        public ArticleWorkConsumerSession Session { get; }

        public RecordingArticleWorkResponsePublisher Publisher { get; }

        public static async Task<ConsumerContext> StartSessionAsync(IArticleWorkHandler? handler = null)
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = BackFillerRabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var publisher = new RecordingArticleWorkResponsePublisher();
            var session = CreateSession(connections, handler ?? new DeferredArticleWorkHandler(), publisher);
            await session.StartAsync(CancellationToken.None);
            return new ConsumerContext(factory, connections, session, publisher);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Connections.DisposeAsync();
        }
    }
}
