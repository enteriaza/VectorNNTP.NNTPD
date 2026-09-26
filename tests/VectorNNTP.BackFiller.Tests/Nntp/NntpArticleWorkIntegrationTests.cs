using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.RabbitMq;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Nntp;

public sealed class NntpArticleWorkIntegrationTests
{
    [Fact]
    public async Task Retrieved_article_reaches_success_without_ack()
    {
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        server.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n");
        factory.Enqueue(server);
        var (handler, publisher, pipeline) = CreatePipeline(factory);
        var channel = new FakeBackFillerRabbitMqChannel(1);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.Success, outcome);
        Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, handler.LastKind);
        Assert.Equal(ArticleRetentionKind.Retained, handler.LastRetentionKind);
        Assert.NotNull(handler.LastPayload);
        Assert.Contains("body"u8, handler.LastPayload);
        Assert.Equal(
            "cache://backfiller.test:1190/30edc94157aa16fe644a45a1f1ffe160",
            handler.LastCacheUri);
        Assert.Empty(publisher.Published);
        var settlement = Assert.Single(channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.True(settlement.Requeue);
    }

    [Fact]
    public async Task Retrieved_article_publishes_confirmed_success_and_acks()
    {
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        server.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n");
        factory.Enqueue(server);
        var rabbitFactory = new FakeBackFillerRabbitMqConnectionFactory();
        var connections = BackFillerRabbitMqServiceTests.CreateService(rabbitFactory);
        await connections.StartAsync(CancellationToken.None);
        rabbitFactory.LastConnection!.DefaultPublishConfirmBehavior = FakePublishConfirmBehavior.Confirm;
        var publisher = new ArticleWorkResponsePublisher(
            connections,
            BackFillerRabbitMqServiceTests.CreateFastRuntime(),
            NullLogger<ArticleWorkResponsePublisher>.Instance);
        await publisher.StartAsync(CancellationToken.None);
        var (handler, _, _) = CreatePipeline(factory);
        var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);
        var channel = new FakeBackFillerRabbitMqChannel(1);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.Success, outcome);
        Assert.Equal(
            "cache://backfiller.test:1190/30edc94157aa16fe644a45a1f1ffe160",
            handler.LastCacheUri);
        var publication = Assert.Single(
            Assert.IsType<FakeBackFillerRabbitMqPublishChannel>(publisher.Channel).Publications);
        using var document = JsonDocument.Parse(publication.Body);
        Assert.Equal("Success", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(handler.LastCacheUri, document.RootElement.GetProperty("uri").GetString());
        Assert.Equal(ArticleWorkTestDeliveries.CanonicalRequestId, document.RootElement.GetProperty("requestId").GetString());
        Assert.True(Assert.Single(channel.Settlements).Acknowledge);
        Assert.NotNull(handler.LastPayload);
        await publisher.DisposeAsync();
        await connections.DisposeAsync();
    }

    [Fact]
    public async Task ArticleNotFound_maps_to_nack_without_requeue()
    {
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        server.Respond(static _ => "430 No such article\r\n");
        factory.Enqueue(server);
        var (_, publisher, pipeline) = CreatePipeline(factory);
        var channel = new FakeBackFillerRabbitMqChannel(1);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, outcome);
        var intent = Assert.Single(publisher.Published);
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, intent.Outcome);
        Assert.False(Assert.Single(channel.Settlements).Requeue);
    }

    [Fact]
    public async Task Provider_and_auth_failures_requeue_without_terminal_response()
    {
        var down = new ScriptedNntpTransportFactory { ConnectException = new IOException("down") };
        var auth = new ScriptedNntpTransportFactory();
        var authServer = new ScriptedNntpServer();
        authServer.Respond(static _ => "481 rejected\r\n");
        auth.Enqueue(authServer);

        var downPipeline = CreatePipeline(down);
        var authPipeline = CreatePipeline(auth, user: "u", password: "p");
        var downChannel = new FakeBackFillerRabbitMqChannel(1);
        var authChannel = new FakeBackFillerRabbitMqChannel(1);

        var downOutcome = await downPipeline.Pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            downChannel,
            static () => true,
            CancellationToken.None);
        var authOutcome = await authPipeline.Pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            authChannel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.ProviderFailure, downOutcome);
        Assert.Equal(ArticleWorkOutcome.ProviderFailure, authOutcome);
        Assert.Empty(downPipeline.Publisher.Published);
        Assert.Empty(authPipeline.Publisher.Published);
        Assert.True(Assert.Single(downChannel.Settlements).Requeue);
        Assert.True(Assert.Single(authChannel.Settlements).Requeue);
    }

    [Fact]
    public async Task Cancellation_requeues_and_is_not_not_found()
    {
        var factory = new ScriptedNntpTransportFactory();
        factory.Enqueue(new ScriptedNntpServer());
        var (_, publisher, pipeline) = CreatePipeline(factory);
        var channel = new FakeBackFillerRabbitMqChannel(1);
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
        Assert.True(Assert.Single(channel.Settlements).Requeue);
    }

    [Fact]
    public async Task Stale_generation_skips_settlement_after_retrieval()
    {
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        server.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n");
        factory.Enqueue(server);
        var (handler, publisher, pipeline) = CreatePipeline(factory);
        var channel = new FakeBackFillerRabbitMqChannel(1);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => false,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.Success, outcome);
        Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, handler.LastKind);
        Assert.Empty(publisher.Published);
        Assert.Empty(channel.Settlements);
    }

    [Fact]
    public async Task Retention_capacity_failure_is_not_article_not_found_and_does_not_ack()
    {
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        server.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n");
        factory.Enqueue(server);
        var catalog = new StaticBackFillerProviderCatalog(
        [
            new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
        ]);
        var registry = new NntpProviderRegistry(
            catalog,
            factory,
            NntpSessionOptions.Default with
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                CommandTimeout = TimeSpan.FromSeconds(2),
                ReceiveTimeout = TimeSpan.FromSeconds(2),
            },
            TimeSpan.FromSeconds(2),
            NullLogger<NntpProviderRegistry>.Instance);
        var handler = new ProviderArticleWorkHandler(
            new NntpArticleRetriever(registry, NullLogger<NntpArticleRetriever>.Instance),
            CreateRetention(maxBytes: 1));
        var publisher = new RecordingArticleWorkResponsePublisher();
        var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);
        var channel = new FakeBackFillerRabbitMqChannel(1);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.RetentionRejected, outcome);
        Assert.Equal(ArticleRetentionKind.PayloadExceedsCapacity, handler.LastRetentionKind);
        Assert.NotEqual(ArticleWorkOutcome.ArticleNotFound, outcome);
        Assert.Empty(publisher.Published);
        Assert.False(Assert.Single(channel.Settlements).Acknowledge);
        Assert.True(Assert.Single(channel.Settlements).Requeue);
    }

    [Fact]
    public async Task Missing_provider_is_provider_failure()
    {
        var factory = new ScriptedNntpTransportFactory();
        var handler = new ProviderArticleWorkHandler(
            new NntpArticleRetriever(
                new NntpProviderRegistry(
                    new StaticBackFillerProviderCatalog(),
                    factory,
                    NntpSessionOptions.Default,
                    TimeSpan.FromSeconds(2),
                    NullLogger<NntpProviderRegistry>.Instance),
                NullLogger<NntpArticleRetriever>.Instance),
            CreateRetention(maxBytes: 1024));
        var publisher = new RecordingArticleWorkResponsePublisher();
        var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);
        var channel = new FakeBackFillerRabbitMqChannel(1);

        var outcome = await pipeline.ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(),
            "Giganews",
            channel,
            static () => true,
            CancellationToken.None);

        Assert.Equal(ArticleWorkOutcome.ProviderFailure, outcome);
        Assert.True(Assert.Single(channel.Settlements).Requeue);
    }

    private static (ProviderArticleWorkHandler Handler, RecordingArticleWorkResponsePublisher Publisher, ArticleWorkDeliveryPipeline Pipeline)
        CreatePipeline(ScriptedNntpTransportFactory factory, string? user = null, string? password = null)
    {
        var catalog = new StaticBackFillerProviderCatalog(
        [
            new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, user, password, 0, 1),
        ]);
        var registry = new NntpProviderRegistry(
            catalog,
            factory,
            NntpSessionOptions.Default with
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                CommandTimeout = TimeSpan.FromSeconds(2),
                ReceiveTimeout = TimeSpan.FromSeconds(2),
            },
            TimeSpan.FromSeconds(2),
            NullLogger<NntpProviderRegistry>.Instance);
        var handler = new ProviderArticleWorkHandler(
            new NntpArticleRetriever(registry, NullLogger<NntpArticleRetriever>.Instance),
            CreateRetention(maxBytes: 1024 * 1024));
        var publisher = new RecordingArticleWorkResponsePublisher();
        return (handler, publisher, new ArticleWorkDeliveryPipeline(handler, publisher, 1024));
    }

    internal static ArticleRetentionAuthority CreateRetention(int maxBytes) =>
        new(
            new BackFiller.Configuration.BackFillerArticleRetentionRuntimeOptions(
                maxBytes,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromSeconds(1)),
            "backfiller.test",
            1190,
            TimeProvider.System,
            NullLogger.Instance);
}
