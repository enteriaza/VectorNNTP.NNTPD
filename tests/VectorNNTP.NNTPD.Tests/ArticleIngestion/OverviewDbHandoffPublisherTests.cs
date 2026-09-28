using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.RabbitMq;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.RabbitMq;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.ArticleIngestion;

/// <summary>OverviewDB RabbitMQ publisher confirm and AMQP property contracts.</summary>
public sealed class OverviewDbHandoffPublisherTests
{
    [Fact]
    public async Task PublishConfirmedAsync_SetsRequiredAmqpProperties_AndUsesDefaultExchange()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);

        var nntpd = TestHostFactory.CreateValidOptions();
        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(nntpd),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        await publisher.PublishConfirmedAsync("overview-one"u8.ToArray(), CancellationToken.None);
        await publisher.PublishConfirmedAsync("overview-two"u8.ToArray(), CancellationToken.None);

        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        Assert.Empty(connection.RpcChannels);
        var channel = Assert.Single(connection.PublishChannels);
        Assert.Equal(2, channel.Publications.Count);

        Assert.All(
            channel.Publications,
            publication =>
            {
                Assert.Equal(OverviewDbTopology.DefaultExchange, publication.Exchange);
                Assert.Equal(OverviewDbTopology.QueueName, publication.RoutingKey);
                Assert.Equal(nntpd.Fqdn, publication.AppId);
                Assert.Equal("nntpd01.usenet.ninja", publication.AppId);
                Assert.Equal(OverviewDbTopology.ExpirationMilliseconds, publication.Expiration);
                Assert.Equal("2000", publication.Expiration);
                Assert.True(publication.Persistent);
                Assert.True(OverviewDbTopology.Mandatory);
                Assert.True(publication.Mandatory);
                Assert.True(Guid.TryParse(publication.MessageId, out var messageId));
                Assert.NotEqual(Guid.Empty, messageId);
            });

        Assert.NotEqual(channel.Publications[0].MessageId, channel.Publications[1].MessageId);
        Assert.Equal("overview-one"u8.ToArray(), channel.Publications[0].Body);
        Assert.Equal("overview-two"u8.ToArray(), channel.Publications[1].Body);
    }

    [Fact]
    public void CreateHandoffProperties_SetsPerMessageExpirationMilliseconds_OnBasicProperties()
    {
        var properties = RabbitMqClientPublishChannel.CreateHandoffProperties(
            "11111111-1111-1111-1111-111111111111",
            "nntpd01.usenet.ninja",
            OverviewDbTopology.ExpirationMilliseconds);

        Assert.Equal("2000", properties.Expiration);
        Assert.Equal(OverviewDbTopology.ExpirationMilliseconds, properties.Expiration);
        Assert.Equal("11111111-1111-1111-1111-111111111111", properties.MessageId);
        Assert.Equal("nntpd01.usenet.ninja", properties.AppId);
        Assert.Equal(DeliveryModes.Persistent, properties.DeliveryMode);
        Assert.True(properties.Persistent);
        Assert.Null(properties.CorrelationId);
        Assert.Null(properties.ReplyTo);
        Assert.Null(properties.ContentType);
    }

    [Fact]
    public async Task PublishConfirmedAsync_Nack_IsSurfacedAsFailure()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);

        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(TestHostFactory.CreateValidOptions()),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        await publisher.PublishConfirmedAsync("accepted"u8.ToArray(), CancellationToken.None);

        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        var channel = Assert.Single(connection.PublishChannels);
        channel.PublishException =
            new InvalidOperationException("RabbitMQ negatively acknowledged the OverviewDB handoff.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => publisher.PublishConfirmedAsync("nack"u8.ToArray(), CancellationToken.None));

        Assert.Contains("negatively acknowledged", ex.Message, StringComparison.Ordinal);
        Assert.Single(channel.Publications);
        Assert.Empty(connection.RpcChannels);
    }

    [Fact]
    public async Task PublishConfirmedAsync_ChannelNotOpen_IsSurfacedAsFailure()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);

        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(TestHostFactory.CreateValidOptions()),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        await publisher.PublishConfirmedAsync("accepted"u8.ToArray(), CancellationToken.None);
        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        var channel = Assert.Single(connection.PublishChannels);
        channel.CloseBeforePublish = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => publisher.PublishConfirmedAsync("closed"u8.ToArray(), CancellationToken.None));
        Assert.Contains("not open", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishConfirmedAsync_ConfirmTimeout_IsSurfacedAsFailure()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);

        var options = RabbitMqOptionsTests.CreateValid();
        options.PublishConfirmTimeoutSeconds = 1;
        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(TestHostFactory.CreateValidOptions()),
            Options.Create(options));

        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        await publisher.PublishConfirmedAsync("accepted"u8.ToArray(), CancellationToken.None);
        var channel = Assert.Single(connection.PublishChannels);
        channel.HoldUntilCancelled = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => publisher.PublishConfirmedAsync("timeout"u8.ToArray(), CancellationToken.None));
        Assert.Single(channel.Publications);
    }

    [Fact]
    public async Task PublishConfirmedAsync_Unroutable_IsSurfacedAsFailure()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);

        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(TestHostFactory.CreateValidOptions()),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        await publisher.PublishConfirmedAsync("accepted"u8.ToArray(), CancellationToken.None);
        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        var channel = Assert.Single(connection.PublishChannels);
        channel.PublishException =
            new InvalidOperationException("RabbitMQ returned the OverviewDB handoff as unroutable.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => publisher.PublishConfirmedAsync("returned"u8.ToArray(), CancellationToken.None));
        Assert.Contains("unroutable", ex.Message, StringComparison.Ordinal);
        Assert.Single(channel.Publications);
    }

    [Fact]
    public void Publisher_HasNoOverviewDbRpcOrDatabaseDependency()
    {
        var ctor = typeof(OverviewDbHandoffPublisher).GetConstructors().Single();
        var types = ctor.GetParameters().Select(static p => p.ParameterType).ToArray();
        Assert.Contains(typeof(IRabbitMqService), types);
        Assert.DoesNotContain(types, static t => t.Name.Contains("Rpc", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, static t => t.Name.Contains("NntpDb", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, static t => t.Name.Contains("MySql", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, static t => t.Name.Contains("Http", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, static t => t.Name.Contains("Grpc", StringComparison.OrdinalIgnoreCase));
        Assert.Null(typeof(OverviewDbHandoffPublisher).GetInterface("IArticleWorkRpcClient"));
    }

    private static RabbitMqService CreateRabbitMqService(FakeRabbitMqConnectionFactory factory)
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = 50;
        options.PoolReconnectMaxDelayMs = 50;
        return new RabbitMqService(
            factory,
            Options.Create(options),
            Options.Create(TestHostFactory.CreateValidOptions()),
            NullLogger<RabbitMqService>.Instance);
    }
}
