using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Messaging.Cache;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Storage;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.RabbitMq;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.Storage;

public sealed class StorageArticleLookupServiceTests
{
    private static readonly ArticleId ArticleX = ArticleId.FromMessageId("<article-x@example.com>"u8);

    [Fact]
    public async Task MultipleOwners_FirstPositiveCompletes_AndDuplicateIsIgnored()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var time = new FakeTimeProvider();
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            time);

        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var lookupTask = service.LookupAsync(ArticleX, CancellationToken.None);
        var publication = await WaitForPublicationAsync(factory);
        Assert.Equal(CacheFleetTopology.RequestsExchangeName, publication.Exchange);
        Assert.Equal(string.Empty, publication.RoutingKey);
        Assert.Equal(CacheFleetTopology.LookupExpirationMilliseconds, publication.Expiration);
        Assert.NotNull(service.CurrentReplyTo);
        Assert.Equal(service.CurrentReplyTo, publication.ReplyTo);

        Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(publication.Body, out var request, out _));
        Assert.NotNull(request);

        var first = CreateResponse(request!, 2, "cache02.usenet.ninja");
        var second = CreateResponse(request!, 3, "cache03.usenet.ninja");
        var consume = factory.LastConnection!.RpcChannels.First(static c => c.ConsumedQueue is not null);
        await consume.DeliverAsync(publication.CorrelationId, StorageArticleLookupWireProtocol.SerializeResponseV1(first));
        await consume.DeliverAsync(publication.CorrelationId, StorageArticleLookupWireProtocol.SerializeResponseV1(second));
        await consume.DeliverAsync(publication.CorrelationId, StorageArticleLookupWireProtocol.SerializeResponseV1(first));

        var result = await lookupTask;
        Assert.Equal(StorageArticleLookupOutcome.Found, result.Outcome);
        Assert.Equal("cache02.usenet.ninja", result.Fqdn);
        Assert.Equal(2, result.ServerId);
        Assert.Equal(0, service.OutstandingCorrelations);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NobodyOwns_TimesOutCleanly()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var time = new FakeTimeProvider();
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            time);

        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var lookupTask = service.LookupAsync(ArticleX, CancellationToken.None);
        _ = await WaitForPublicationAsync(factory);
        time.Advance(CacheFleetTopology.LookupTimeout);

        var result = await lookupTask;
        Assert.Equal(StorageArticleLookupOutcome.NotFound, result.Outcome);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LateResponse_AfterCompletion_IsIgnored()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var lookupTask = service.LookupAsync(ArticleX, CancellationToken.None);
        var publication = await WaitForPublicationAsync(factory);
        Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(publication.Body, out var request, out _));
        var consume = factory.LastConnection!.RpcChannels.First(static c => c.ConsumedQueue is not null);
        await consume.DeliverAsync(
            publication.CorrelationId,
            StorageArticleLookupWireProtocol.SerializeResponseV1(CreateResponse(request!, 1, "cache01.usenet.ninja")));
        var result = await lookupTask;
        Assert.Equal(StorageArticleLookupOutcome.Found, result.Outcome);

        Assert.False(service.TryDispatchResponse(
            publication.CorrelationId,
            CreateResponse(request!, 2, "cache02.usenet.ninja")));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentLookups_CorrelateIndependently()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMq(factory);
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateOptions()),
            NullLogger<StorageArticleLookupService>.Instance);
        var articleA = ArticleId.FromMessageId("<a@example.com>"u8);
        var articleB = ArticleId.FromMessageId("<b@example.com>"u8);

        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var lookupA = service.LookupAsync(articleA, CancellationToken.None);
        var lookupB = service.LookupAsync(articleB, CancellationToken.None);
        var publications = await WaitForPublicationCountAsync(factory, 2);
        var requestA = publications.Single(p =>
            StorageArticleLookupWireProtocol.TryParseRequestV1(p.Body, out var r, out _)
            && r!.ArticleId == articleA);
        var requestB = publications.Single(p =>
            StorageArticleLookupWireProtocol.TryParseRequestV1(p.Body, out var r, out _)
            && r!.ArticleId == articleB);
        Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(requestA.Body, out var parsedA, out _));
        Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(requestB.Body, out var parsedB, out _));
        var consume = factory.LastConnection!.RpcChannels.First(static c => c.ConsumedQueue is not null);

        await consume.DeliverAsync(
            requestB.CorrelationId,
            StorageArticleLookupWireProtocol.SerializeResponseV1(
                CreateResponse(parsedB!, 3, "cache03.usenet.ninja")));
        await consume.DeliverAsync(
            requestA.CorrelationId,
            StorageArticleLookupWireProtocol.SerializeResponseV1(
                CreateResponse(parsedA!, 1, "cache01.usenet.ninja")));

        var resultA = await lookupA;
        var resultB = await lookupB;
        Assert.Equal("cache01.usenet.ninja", resultA.Fqdn);
        Assert.Equal("cache03.usenet.ninja", resultB.Fqdn);

        await service.StopAsync(CancellationToken.None);
    }

    private static StorageArticleLookupResponse CreateResponse(
        StorageArticleLookupRequest request,
        int serverId,
        string fqdn) =>
        new(
            1,
            request.RequestId,
            serverId,
            fqdn,
            request.ArticleId,
            StorageArticleLookupWireProtocol.BuildCacheUri(fqdn, 1191, request.ArticleId));

    private static RabbitMqService CreateRabbitMq(FakeRabbitMqConnectionFactory factory)
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = 50;
        options.PoolReconnectMaxDelayMs = 50;
        return new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTP.NNTPD:nntpd01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance);
    }

    private static NntpdOptions CreateOptions()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = 1;
        return options;
    }

    private static async Task<FakeRabbitMqRpcPublication> WaitForPublicationAsync(FakeRabbitMqConnectionFactory factory)
    {
        var publications = await WaitForPublicationCountAsync(factory, 1);
        return publications[0];
    }

    private static async Task<List<FakeRabbitMqRpcPublication>> WaitForPublicationCountAsync(
        FakeRabbitMqConnectionFactory factory,
        int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var connection = factory.LastConnection;
            if (connection is not null)
            {
                var publications = connection.RpcChannels
                    .SelectMany(static channel => channel.Publications)
                    .Where(static p => p.Exchange == CacheFleetTopology.RequestsExchangeName)
                    .ToList();
                if (publications.Count >= count)
                {
                    return publications;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Expected {count} cache.requests publications.");
    }
}
