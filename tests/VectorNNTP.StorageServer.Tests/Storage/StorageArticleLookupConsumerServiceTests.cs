using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Messaging.Cache;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Tests.Fixtures;
using VectorNNTP.StorageServer.Tests.TestDoubles;

namespace VectorNNTP.StorageServer.Tests.Storage;

public sealed class StorageArticleLookupConsumerServiceTests
{
    private static readonly ArticleId ArticleX = ArticleId.FromMessageId("<article-x@example.com>"u8);

    [Fact]
    public async Task StartAsync_DeclaresEphemeralPerInstanceQueue_BoundToRequestsFanout()
    {
        var factory = new RecordingStorageServerRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var runtime = CreateRuntime(1);
        var consumer = new StorageArticleLookupConsumerService(
            rabbit,
            runtime,
            NullStorageArticlePresence.Instance,
            NullLogger<StorageArticleLookupConsumerService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await consumer.StartAsync(CancellationToken.None);

        Assert.Equal("cache.cache01.usenet.ninja", consumer.CurrentQueueName);
        var channel = Assert.Single(factory.LastConnection!.ManualAckChannels);
        Assert.Contains(
            channel.Exchanges,
            static e => e.Name == CacheFleetTopology.RequestsExchangeName && e.Type == "fanout" && e.Durable);
        var queue = Assert.Single(channel.Queues);
        Assert.Equal("cache.cache01.usenet.ninja", queue.Name);
        Assert.False(queue.Durable);
        Assert.True(queue.Exclusive);
        Assert.True(queue.AutoDelete);
        var binding = Assert.Single(channel.Bindings);
        Assert.Equal(queue.Name, binding.Queue);
        Assert.Equal(CacheFleetTopology.RequestsExchangeName, binding.Exchange);

        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ThreeServers_ReceiveIndependently_AndOnlyOwnerResponds()
    {
        var servers = new[]
        {
            await StartServerAsync(1, ownsArticle: false),
            await StartServerAsync(2, ownsArticle: true),
            await StartServerAsync(3, ownsArticle: false),
        };

        try
        {
            var request = new StorageArticleLookupRequest(1, Guid.NewGuid(), ArticleX);
            var body = StorageArticleLookupWireProtocol.SerializeRequestV1(request);
            const string correlationId = "corr-1";
            const string replyTo = "nntpd.01.reply.cache.lookup";

            foreach (var server in servers)
            {
                await server.Channel.DeliverAsync(10, body, correlationId, replyTo);
            }

            Assert.Empty(servers[0].PublishChannel.Publications);
            Assert.Empty(servers[2].PublishChannel.Publications);
            var publication = Assert.Single(servers[1].PublishChannel.Publications);
            Assert.Equal(string.Empty, publication.Exchange);
            Assert.Equal(replyTo, publication.RoutingKey);
            Assert.Equal(correlationId, publication.CorrelationId);
            Assert.True(StorageArticleLookupWireProtocol.TryParseResponseV1(
                publication.Body.Span,
                out var response,
                out _));
            Assert.NotNull(response);
            Assert.Equal("cache02.usenet.ninja", response.Fqdn);
            Assert.Equal(2, response.ServerId);
            Assert.All(servers, static s => Assert.Contains(10UL, s.Channel.Acks));
        }
        finally
        {
            foreach (var server in servers)
            {
                await server.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task MissingArticle_ProducesNoResponse_AndAcks()
    {
        await using var server = await StartServerAsync(1, ownsArticle: false);
        var request = new StorageArticleLookupRequest(1, Guid.NewGuid(), ArticleX);
        await server.Channel.DeliverAsync(
            7,
            StorageArticleLookupWireProtocol.SerializeRequestV1(request),
            "corr",
            "reply-queue");
        Assert.Empty(server.PublishChannel.Publications);
        Assert.Contains(7UL, server.Channel.Acks);
    }

    private static async Task<ServerHarness> StartServerAsync(int serverId, bool ownsArticle)
    {
        var factory = new RecordingStorageServerRabbitMqConnectionFactory();
        var rabbit = CreateRabbitMq(factory);
        var presence = new InMemoryStorageArticlePresence();
        if (ownsArticle)
        {
            presence.Add(ArticleX);
        }

        var consumer = new StorageArticleLookupConsumerService(
            rabbit,
            CreateRuntime(serverId),
            presence,
            NullLogger<StorageArticleLookupConsumerService>.Instance);
        await rabbit.StartAsync(CancellationToken.None);
        await consumer.StartAsync(CancellationToken.None);
        var connection = factory.LastConnection!;
        return new ServerHarness(
            rabbit,
            consumer,
            Assert.Single(connection.ManualAckChannels),
            Assert.Single(connection.PublishChannels));
    }

    private static RabbitMqService CreateRabbitMq(IRabbitMqConnectionFactory factory)
    {
        var options = new RabbitMqOptions
        {
            Hosts = ["127.0.0.1"],
            Port = 5672,
            VirtualHost = "/",
            EnableSsl = false,
            Username = "guest",
            Password = "guest",
            PoolReconnectBaseDelayMs = 50,
            PoolReconnectMaxDelayMs = 50,
        };
        return new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTP.StorageServer:test"),
            NullLogger<RabbitMqService>.Instance);
    }

    private static StorageServerRuntimeOptions CreateRuntime(int serverId)
    {
        var options = StorageServerTestOptions.CreateValid();
        options.ServerId = serverId;
        return StorageServerRuntimeOptionsFactory.Create(options);
    }

    private sealed class ServerHarness : IAsyncDisposable
    {
        private readonly RabbitMqService _rabbit;
        private readonly StorageArticleLookupConsumerService _consumer;

        public ServerHarness(
            RabbitMqService rabbit,
            StorageArticleLookupConsumerService consumer,
            RecordingManualAckChannel channel,
            RecordingPublishChannel publishChannel)
        {
            _rabbit = rabbit;
            _consumer = consumer;
            Channel = channel;
            PublishChannel = publishChannel;
        }

        public RecordingManualAckChannel Channel { get; }

        public RecordingPublishChannel PublishChannel { get; }

        public async ValueTask DisposeAsync()
        {
            await _consumer.StopAsync(CancellationToken.None);
            await _rabbit.DisposeAsync();
        }
    }
}
