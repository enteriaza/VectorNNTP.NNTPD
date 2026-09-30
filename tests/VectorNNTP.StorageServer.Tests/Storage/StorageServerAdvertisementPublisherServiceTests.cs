using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.Common.Messaging.Cache;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Tests.Fixtures;
using VectorNNTP.StorageServer.Tests.TestDoubles;

namespace VectorNNTP.StorageServer.Tests.Storage;

public sealed class StorageServerAdvertisementPublisherServiceTests
{
    [Fact]
    public async Task StartAsync_DeclaresBroadcastExchange_AndPublishesExpectedPayload()
    {
        var factory = new RecordingStorageServerRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var capacity = new FixedStorageCapacityReader(10_000, 4_000, 6_000);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-29T12:00:00Z"));
        var runtime = StorageServerRuntimeOptionsFactory.Create(StorageServerTestOptions.CreateValid());
        var publisher = new StorageServerAdvertisementPublisherService(
            rabbit,
            runtime,
            capacity,
            NullLogger<StorageServerAdvertisementPublisherService>.Instance,
            time);

        await rabbit.StartAsync(CancellationToken.None);
        await publisher.StartAsync(CancellationToken.None);

        var connection = factory.LastConnection!;
        Assert.NotEmpty(connection.TopologyChannels);
        Assert.Contains(
            connection.TopologyChannels.SelectMany(static c => c.Exchanges),
            static e => e.Name == CacheFleetTopology.BroadcastExchangeName
                && e.Type == "fanout"
                && e.Durable
                && !e.AutoDelete);

        await WaitForAsync(() => publisher.PublishedCount >= 1, TimeSpan.FromSeconds(2));
        var channel = Assert.Single(connection.PublishChannels);
        var publication = Assert.Single(channel.Publications);
        Assert.Equal(CacheFleetTopology.BroadcastExchangeName, publication.Exchange);
        Assert.Equal(string.Empty, publication.RoutingKey);
        Assert.Equal(CacheFleetTopology.AdvertisementExpirationMilliseconds, publication.ExpirationMilliseconds);
        Assert.Equal(StorageServerAdvertisementWireProtocol.JsonContentType, publication.ContentType);
        Assert.Equal(runtime.Fqdn, publication.AppId);
        Assert.False(publication.Persistent);
        Assert.False(publication.Mandatory);
        Assert.True(StorageServerAdvertisementWireProtocol.TryParseV1(
            publication.Body.Span,
            out var advertisement,
            out _));
        Assert.NotNull(advertisement);
        Assert.Equal(runtime.ServerId, advertisement.ServerId);
        Assert.Equal(runtime.Fqdn, advertisement.Fqdn);
        Assert.Equal(10_000, advertisement.TotalBytes);
        Assert.Equal(4_000, advertisement.UsedBytes);
        Assert.Equal(6_000, advertisement.AvailableBytes);
        Assert.Equal(runtime.BindPortTls, advertisement.VatpPort);

        time.Advance(CacheFleetTopology.AdvertisementInterval);
        await WaitForAsync(() => publisher.PublishedCount >= 2, TimeSpan.FromSeconds(2));

        await publisher.StopAsync(CancellationToken.None);
        Assert.Equal(1, channel.DisposeCount);
    }

    [Fact]
    public async Task StopAsync_EndsPublishLoop_WithoutOrphanedExecution()
    {
        var factory = new RecordingStorageServerRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var time = new FakeTimeProvider();
        var publisher = new StorageServerAdvertisementPublisherService(
            rabbit,
            StorageServerRuntimeOptionsFactory.Create(StorageServerTestOptions.CreateValid()),
            new FixedStorageCapacityReader(1, 0, 1),
            NullLogger<StorageServerAdvertisementPublisherService>.Instance,
            time);

        await rabbit.StartAsync(CancellationToken.None);
        await publisher.StartAsync(CancellationToken.None);
        var loop = publisher.Execution;
        Assert.NotNull(loop);

        await publisher.StopAsync(CancellationToken.None);
        Assert.True(loop.IsCompleted);
        Assert.Null(publisher.Execution);
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
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTP.StorageServer:cache01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met before timeout.");
            }

            await Task.Delay(10);
        }
    }
}

internal sealed class FixedStorageCapacityReader : IStorageCapacityReader
{
    private readonly StorageCapacitySnapshot _snapshot;

    public FixedStorageCapacityReader(long total, long used, long available) =>
        _snapshot = new StorageCapacitySnapshot(total, used, available);

    public StorageCapacitySnapshot Read() => _snapshot;
}
