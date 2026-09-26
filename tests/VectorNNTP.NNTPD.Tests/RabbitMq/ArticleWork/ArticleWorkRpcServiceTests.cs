using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Hosting;
using VectorNNTP.NNTPD.Logging;
using VectorNNTP.NNTPD.RabbitMq;
using VectorNNTP.NNTPD.RabbitMq.ArticleWork;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.RabbitMq.ArticleWork;

[Collection(SerilogCollection.Name)]
public sealed class ArticleWorkRpcServiceTests
{
    [Fact]
    public async Task StartAsync_DeclaresExclusiveReplyQueue_AndConsumesOnce()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);
        var rpc = new ArticleWorkRpcService(rabbit, Options.Create(TestHostFactory.CreateValidOptions()), NullLogger<ArticleWorkRpcService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await topology.StartAsync(CancellationToken.None);
        await rpc.StartAsync(CancellationToken.None);

        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        Assert.Equal(2, connection.RpcChannels.Count);
        var consume = connection.RpcChannels[1];
        var queue = Assert.Single(consume.Queues);
        Assert.Equal(rpc.CurrentReplyTo, queue.Name);
        Assert.False(queue.Durable);
        Assert.True(queue.Exclusive);
        Assert.True(queue.AutoDelete);
        Assert.Equal(queue.Name, consume.ConsumedQueue);
        Assert.StartsWith("nntpd.", queue.Name, StringComparison.Ordinal);

        await rpc.StopAsync(CancellationToken.None);
        Assert.Null(rpc.CurrentReplyTo);
        Assert.Equal(1, connection.RpcChannels[0].DisposeCount);
        Assert.Equal(1, consume.DisposeCount);
    }

    [Fact]
    public async Task ConnectionReplacement_DisposesStaleSession_AndDoesNotCorruptNewGeneration()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        factory.Connected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rabbit = CreateRabbitMqService(factory);
        var rpc = new ArticleWorkRpcService(rabbit, Options.Create(TestHostFactory.CreateValidOptions()), NullLogger<ArticleWorkRpcService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await rpc.StartAsync(CancellationToken.None);
        var first = factory.Connections[0];
        var firstReplyTo = rpc.CurrentReplyTo;
        Assert.Equal(1, rpc.CurrentSessionGeneration);
        var firstPublish = first.RpcChannels[0];

        factory.Connected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.SimulateLost();
        await factory.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => rpc.CurrentSessionGeneration == 2);

        Assert.Equal(2, rpc.CurrentSessionGeneration);
        Assert.Equal(1, firstPublish.DisposeCount);
        Assert.True(first.RpcChannels.TrueForAll(static channel => channel.DisposeCount == 1));
        var second = factory.LastConnection ?? throw new InvalidOperationException("Expected replacement connection.");
        Assert.Equal(2, second.RpcChannels.Count);
        Assert.NotNull(firstReplyTo);
        Assert.False(string.IsNullOrWhiteSpace(rpc.CurrentReplyTo));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => firstPublish.PublishAsync(
                "backfiller.storage",
                "backfiller.storage",
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"),
                firstReplyTo!,
                ArticleWorkWireProtocol.JsonContentType,
                ArticleWorkRpcAmqp.ExpirationMilliseconds,
                ReadOnlyMemory<byte>.Empty,
                CancellationToken.None));
        Assert.Empty(second.RpcChannels[0].Publications);

        await rpc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_CancelsPendingLookup_AndClearsCorrelations()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        var rpc = new ArticleWorkRpcService(rabbit, Options.Create(TestHostFactory.CreateValidOptions()), NullLogger<ArticleWorkRpcService>.Instance);
        await rabbit.StartAsync(CancellationToken.None);
        await rpc.StartAsync(CancellationToken.None);

        var lookup = rpc.LookupByMessageIdAsync("<12345@example.invalid>"u8.ToArray(), CancellationToken.None);
        await WaitForAsync(() => factory.LastConnection!.RpcChannels[0].Publications.Count == 1);
        var publication = Assert.Single(factory.LastConnection!.RpcChannels[0].Publications);
        Assert.Equal(ArticleWorkRpcAmqp.ExpirationMilliseconds, publication.Expiration);
        Assert.True(Guid.TryParse(publication.RequestId, out _));
        Assert.Equal(ArticleWorkWireProtocol.JsonContentType, publication.ContentType);
        Assert.Equal(rpc.CurrentReplyTo, publication.ReplyTo);
        Assert.True(rpc.OutstandingCorrelations >= 1);

        await rpc.StopAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.Equal(0, rpc.OutstandingCorrelations);
    }

    [Fact]
    public async Task WorkerResponse_CarriesOneSecondExpiration_AndCompletesLookup()
    {
        var time = new FakeTimeProvider();
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        var rpc = new ArticleWorkRpcService(
            rabbit,
            Options.Create(TestHostFactory.CreateValidOptions()),
            NullLogger<ArticleWorkRpcService>.Instance,
            time);

        await rabbit.StartAsync(CancellationToken.None);
        await rpc.StartAsync(CancellationToken.None);

        var lookup = rpc.LookupByMessageIdAsync("<12345@example.invalid>"u8.ToArray(), CancellationToken.None);
        await WaitForAsync(() => factory.LastConnection!.RpcChannels[0].Publications.Count == 1);
        var publication = Assert.Single(factory.LastConnection!.RpcChannels[0].Publications);
        Assert.Equal(ArticleWorkRpcAmqp.ExpirationMilliseconds, publication.Expiration);

        var consume = factory.LastConnection.RpcChannels[1];
        await consume.DeliverAsync(
            publication.CorrelationId,
            SuccessBody(publication.RequestId),
            requestId: publication.RequestId,
            expiration: ArticleWorkRpcAmqp.ExpirationMilliseconds);
        var result = await lookup;

        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal(ArticleWorkRpcAmqp.ExpirationMilliseconds, consume.LastDelivery?.Expiration);
        Assert.Equal(0, rpc.OutstandingCorrelations);
        await rpc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StaleGenerationDelivery_CannotCompleteNewerLookup()
    {
        var time = new FakeTimeProvider();
        var factory = new FakeRabbitMqConnectionFactory();
        factory.Connected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rabbit = CreateRabbitMqService(factory);
        var rpc = new ArticleWorkRpcService(
            rabbit,
            Options.Create(TestHostFactory.CreateValidOptions()),
            NullLogger<ArticleWorkRpcService>.Instance,
            time);

        await rabbit.StartAsync(CancellationToken.None);
        await rpc.StartAsync(CancellationToken.None);
        var first = factory.Connections[0];
        var firstLookup = rpc.LookupByMessageIdAsync("<12345@example.invalid>"u8.ToArray(), CancellationToken.None);
        await WaitForAsync(() => first.RpcChannels[0].Publications.Count == 1);
        var firstPublication = Assert.Single(first.RpcChannels[0].Publications);

        factory.Connected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.SimulateLost();
        await factory.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => rpc.CurrentSessionGeneration == 2);

        var secondLookup = rpc.LookupByMessageIdAsync("<12345@example.invalid>"u8.ToArray(), CancellationToken.None);
        var second = factory.LastConnection ?? throw new InvalidOperationException("Expected replacement connection.");
        await WaitForAsync(() => second.RpcChannels[0].Publications.Count == 1);
        var secondPublication = Assert.Single(second.RpcChannels[0].Publications);

        await second.RpcChannels[1].DeliverAsync(
            firstPublication.CorrelationId,
            SuccessBody(firstPublication.RequestId),
            requestId: firstPublication.RequestId);
        Assert.False(secondLookup.IsCompleted);

        await first.RpcChannels[1].DeliverAsync(
            secondPublication.CorrelationId,
            SuccessBody(secondPublication.RequestId),
            requestId: secondPublication.RequestId);
        Assert.False(secondLookup.IsCompleted);

        await second.RpcChannels[1].DeliverAsync(
            secondPublication.CorrelationId,
            SuccessBody(secondPublication.RequestId),
            requestId: secondPublication.RequestId);
        var result = await secondLookup;
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal(secondPublication.RequestId, result.RequestId.ToString("D"), StringComparer.Ordinal);

        time.Advance(ArticleWorkRpcTiming.LookupDeadline);
        var firstResult = await firstLookup;
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, firstResult.Outcome);
        await rpc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void RpcChannel_DoesNotExposeQueueCleanup()
    {
        var methods = typeof(IRabbitMqRpcChannel).GetMethods().Select(static method => method.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("QueueDeleteAsync", methods);
        Assert.DoesNotContain("QueuePurgeAsync", methods);
        Assert.DoesNotContain("ExchangeDeleteAsync", methods);
    }

    [Fact]
    public async Task Host_RegistersRpcService_ImmediatelyAfterTopology()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpdHosting(includePlaceholderService: false);

        using var host = builder.Build();
        var services = host.Services.GetServices<IApplicationService>().ToArray();
        Assert.Equal(typeof(RabbitMqTopologyService), services[3].GetType());
        Assert.Equal(typeof(ArticleWorkRpcService), services[4].GetType());
        Assert.Same(
            host.Services.GetRequiredService<IArticleWorkRpcClient>(),
            services[4]);
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

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, cts.Token).ConfigureAwait(false);
        }
    }

    private static byte[] SuccessBody(string requestId)
    {
        return Encoding.UTF8.GetBytes(
            $$"""{"version":1,"requestId":"{{requestId}}","messageId":"<12345@example.invalid>","backbone":"Storage","outcome":"Success","uri":"cache://backfiller01.usenet.ninja:119/30edc94157aa16fe644a45a1f1ffe160"}""");
    }
}
