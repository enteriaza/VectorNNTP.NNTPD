using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.NNTPD.RabbitMq;
using VectorNNTP.NNTPD.RabbitMq.ArticleWork;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.RabbitMq.ArticleWork;

public sealed class ArticleWorkRpcClientTests
{
    private static readonly byte[] MessageIdBytes = "<12345@example.invalid>"u8.ToArray();
    private const string MessageId = "<12345@example.invalid>";
    private const string SuccessUri = "cache://backfiller01.usenet.ninja:119/30edc94157aa16fe644a45a1f1ffe160";

    [Fact]
    public async Task StorageSuccess_At50ms_CompletesImmediately_DoesNotFanOut()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var storage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(TimeSpan.FromMilliseconds(50));
        harness.DeliverSuccess(storage);
        var result = await lookup;

        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal(SuccessUri, result.Uri);
        Assert.Equal("backfiller.storage", result.SourceExchange);
        Assert.Equal(50, harness.Time.GetUtcNow().UtcDateTime.TimeOfDay.TotalMilliseconds, precision: 0);
        Assert.Single(harness.Publisher.Publications);
        Assert.Equal(0, harness.Router.OutstandingCount);
        AssertNoProviderFanOut(harness);
    }

    [Fact]
    public async Task StorageSuccess_At499ms_CompletesImmediately_DoesNotFanOut()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var storage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(TimeSpan.FromMilliseconds(499));
        harness.DeliverSuccess(storage);
        var result = await lookup;

        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Single(harness.Publisher.Publications);
        AssertNoProviderFanOut(harness);
    }

    [Fact]
    public async Task StorageTimeout_At500ms_PublishesAllTwelveProvidersConcurrently()
    {
        await using var harness = CreateHarness();
        harness.Publisher.HoldPublications = true;
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForStartedAsync(1);
        harness.Publisher.ReleaseOne();
        var storage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        Assert.Equal("backfiller.storage", storage.RoutingKey);
        AssertAmqpContract(storage);

        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForStartedAsync(13);
        Assert.Equal(13, harness.Publisher.StartedCount);
        Assert.Single(harness.Publisher.Publications);
        harness.Publisher.ReleaseAll();
        await harness.Publisher.WaitForPublicationCountAsync(13);

        var providerExchanges = BackfillArticleRetrievalTopology.Definitions
            .Select(static definition => definition.ExchangeName)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            providerExchanges,
            harness.Publisher.Publications.Skip(1).Select(static publication => publication.Exchange).ToHashSet(StringComparer.Ordinal));
        Assert.All(harness.Publisher.Publications, AssertAmqpContract);

        harness.Time.Advance(ArticleWorkRpcTiming.LookupDeadline);
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
    }

    [Fact]
    public async Task StorageArticleNotFound_At100ms_IsProcessedImmediately_ThenFansOutAt500ms()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var storage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(TimeSpan.FromMilliseconds(100));
        harness.DeliverOutcome(storage, ArticleWorkOutcome.ArticleNotFound, "missing");
        Assert.False(lookup.IsCompleted);
        Assert.Single(harness.Publisher.Publications);

        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.Publisher.WaitForPublicationCountAsync(13);
        Assert.False(lookup.IsCompleted);

        harness.Time.Advance(TimeSpan.FromSeconds(5));
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
        Assert.Contains("deadline", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StorageInvalidArticle_IsSourceLocal_DoesNotWaitForGrace_AndDoesNotCompleteLookup()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var storage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(TimeSpan.FromMilliseconds(80));
        harness.DeliverOutcome(storage, ArticleWorkOutcome.InvalidArticle, "bad article");
        Assert.False(lookup.IsCompleted);
        Assert.Single(harness.Publisher.Publications);

        harness.Time.Advance(TimeSpan.FromMilliseconds(420));
        await harness.Publisher.WaitForPublicationCountAsync(13);
        var eweka = harness.Publisher.Publications.Single(static publication => publication.Exchange == "backfiller.eweka");
        harness.DeliverSuccess(eweka, "Eweka");
        var result = await lookup;

        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal("backfiller.eweka", result.SourceExchange);
        Assert.Equal(SuccessUri, result.Uri);
    }

    [Fact]
    public async Task StorageInvalidRequest_DoesNotSuppressLaterProviderSuccess()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var storage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.DeliverOutcome(storage, ArticleWorkOutcome.InvalidRequest, "bad request");
        Assert.False(lookup.IsCompleted);

        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForPublicationCountAsync(13);
        var giganews = harness.Publisher.Publications.Single(static publication => publication.Exchange == "backfiller.giganews");
        harness.DeliverSuccess(giganews, "Giganews");
        var result = await lookup;

        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal("backfiller.giganews", result.SourceExchange);
    }

    [Fact]
    public async Task StorageSuccess_AfterFanOut_BeforeFiveSeconds_Wins()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var storage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForPublicationCountAsync(13);
        harness.Time.Advance(TimeSpan.FromMilliseconds(100));
        harness.DeliverSuccess(storage);
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal("backfiller.storage", result.SourceExchange);
    }

    [Fact]
    public async Task ProviderSuccess_ImmediatelyAfterFanOut_CompletesWithoutWaitingForFiveSeconds()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForPublicationCountAsync(13);
        var eweka = harness.Publisher.Publications.Single(static publication => publication.Exchange == "backfiller.eweka");
        harness.DeliverSuccess(eweka, "Eweka");
        var result = await lookup;

        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal("backfiller.eweka", result.SourceExchange);
        Assert.Equal("Eweka", result.Backbone);
        Assert.Equal(500, harness.Time.GetUtcNow().UtcDateTime.TimeOfDay.TotalMilliseconds, precision: 0);
    }

    [Fact]
    public async Task ProviderSuccess_At4_9s_CompletesImmediately_DoesNotWaitForFiveSeconds()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForPublicationCountAsync(13);
        harness.Time.Advance(TimeSpan.FromMilliseconds(4400));
        var eweka = harness.Publisher.Publications.Single(static publication => publication.Exchange == "backfiller.eweka");
        harness.DeliverSuccess(eweka, "Eweka");
        var result = await lookup;

        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal(4900, harness.Time.GetUtcNow().UtcDateTime.TimeOfDay.TotalMilliseconds, precision: 0);
        Assert.False(lookup.IsFaulted);
    }

    [Fact]
    public async Task ProviderArticleNotFound_ContinuesWaiting()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForPublicationCountAsync(13);
        foreach (var publication in harness.Publisher.Publications.Where(static publication => publication.Exchange != "backfiller.storage"))
        {
            harness.DeliverOutcome(publication, ArticleWorkOutcome.ArticleNotFound, "missing");
        }

        Assert.False(lookup.IsCompleted);
        harness.Time.Advance(ArticleWorkRpcTiming.LookupDeadline);
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
    }

    [Fact]
    public async Task NoResponses_ResolvesAtFiveSecondAggregateDeadline()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForPublicationCountAsync(13);
        Assert.False(lookup.IsCompleted);
        harness.Time.Advance(TimeSpan.FromMilliseconds(4500));
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
        Assert.Contains("deadline", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5000, harness.Time.GetUtcNow().UtcDateTime.TimeOfDay.TotalMilliseconds, precision: 0);
        Assert.Equal(0, harness.Router.OutstandingCount);
    }

    [Fact]
    public async Task LateResponse_AfterLookupCompletion_IsIgnored()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var storage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForPublicationCountAsync(13);
        harness.Time.Advance(ArticleWorkRpcTiming.LookupDeadline);
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);

        harness.DeliverSuccess(storage);
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
        Assert.Equal(0, harness.Router.OutstandingCount);
    }

    [Fact]
    public async Task UnknownCorrelationId_IsIgnored()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var storage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Router.Dispatch(
            Guid.NewGuid().ToString("D"),
            SuccessBody(Guid.NewGuid()),
            storage.RequestId,
            0);
        Assert.False(lookup.IsCompleted);
        harness.Time.Advance(ArticleWorkRpcTiming.LookupDeadline);
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
    }

    [Fact]
    public async Task Publications_ShareRequestId_AndUseDistinctCorrelationIds()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForPublicationCountAsync(13);

        var requestIds = harness.Publisher.Publications
            .Select(static publication => publication.RequestId)
            .ToArray();
        Assert.Equal(13, requestIds.Length);
        var requestId = Assert.Single(requestIds.Distinct(StringComparer.Ordinal));
        Assert.True(Guid.TryParse(requestId, out var parsedRequestId));
        Assert.NotEqual(Guid.Empty, parsedRequestId);
        Assert.Equal(
            13,
            harness.Publisher.Publications.Select(static publication => publication.CorrelationId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            harness.Publisher.Publications,
            publication =>
            {
                AssertAmqpContract(publication);
                Assert.Equal(requestId, publication.RequestId, StringComparer.Ordinal);
                Assert.NotEqual(publication.RequestId, publication.CorrelationId, StringComparer.Ordinal);
                using var document = JsonDocument.Parse(publication.Body);
                var root = document.RootElement;
                Assert.Equal(1, root.GetProperty("version").GetInt32());
                Assert.Equal(parsedRequestId, root.GetProperty("requestId").GetGuid());
                Assert.Equal(MessageId, root.GetProperty("messageId").GetString());
                Assert.False(root.TryGetProperty("CorrelationId", out _));
                Assert.False(root.TryGetProperty("ReplyTo", out _));
            });
        var storagePublication = harness.Publisher.Publications.Single(static publication => publication.Exchange == "backfiller.storage");
        Assert.Equal("Storage", JsonDocument.Parse(storagePublication.Body).RootElement.GetProperty("backbone").GetString());
        Assert.Equal(
            BackfillArticleRetrievalTopology.Definitions.ToDictionary(static definition => definition.ExchangeName, static definition => definition.Provider, StringComparer.Ordinal),
            harness.Publisher.Publications
                .Where(static publication => publication.Exchange != "backfiller.storage")
                .ToDictionary(
                    static publication => publication.Exchange,
                    static publication => JsonDocument.Parse(publication.Body).RootElement.GetProperty("backbone").GetString()!,
                    StringComparer.Ordinal));

        harness.Time.Advance(ArticleWorkRpcTiming.LookupDeadline);
        await lookup;
    }

    [Fact]
    public async Task TwoIndependentLookups_CannotCrossComplete()
    {
        await using var harness = CreateHarness();
        var first = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var firstStorage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        var firstRequestId = firstStorage.RequestId;
        harness.Time.Advance(ArticleWorkRpcTiming.LookupDeadline);
        await first;

        var second = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var secondStorage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage", skip: 1);
        Assert.NotEqual(firstRequestId, secondStorage.RequestId, StringComparer.Ordinal);
        harness.DeliverSuccess(firstStorage, requestId: Guid.Parse(firstRequestId));
        Assert.False(second.IsCompleted);
        harness.DeliverSuccess(secondStorage);
        var result = await second;
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal(secondStorage.Exchange, result.SourceExchange);
        Assert.Equal(secondStorage.RequestId, result.RequestId.ToString("D"), StringComparer.Ordinal);
    }

    [Fact]
    public async Task RequestWithoutConsumer_UsesOneSecondExpiration_AndDoesNotCleanQueues()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var storage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        AssertAmqpContract(storage);
        Assert.Equal(ArticleWorkRpcAmqp.ExpirationMilliseconds, storage.Expiration);

        var publisherMethods = typeof(IArticleWorkRpcPublisher).GetMethods().Select(static method => method.Name);
        Assert.DoesNotContain("QueueDeleteAsync", publisherMethods);
        Assert.DoesNotContain("QueuePurgeAsync", publisherMethods);
        var channelMethods = typeof(IRabbitMqRpcChannel).GetMethods().Select(static method => method.Name);
        Assert.DoesNotContain("QueueDeleteAsync", channelMethods);
        Assert.DoesNotContain("QueuePurgeAsync", channelMethods);

        harness.Time.Advance(ArticleWorkRpcTiming.LookupDeadline);
        await lookup;
    }

    [Fact]
    public async Task FirstSuccessWins_ConcurrentResponses_CompleteExactlyOnce()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForPublicationCountAsync(13);

        var first = harness.Publisher.Publications.Single(static publication => publication.Exchange == "backfiller.eweka");
        var second = harness.Publisher.Publications.Single(static publication => publication.Exchange == "backfiller.giganews");
        await Task.WhenAll(
            Task.Run(() => harness.DeliverSuccess(first, "Eweka")),
            Task.Run(() => harness.DeliverSuccess(second, "Giganews")));

        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.True(
            result.SourceExchange is "backfiller.eweka" or "backfiller.giganews",
            result.SourceExchange);
        Assert.Equal(0, harness.Router.OutstandingCount);
    }

    [Fact]
    public async Task AbsoluteLifetime_CompletesWhenPublishBlocksForSixtySeconds()
    {
        await using var harness = CreateHarness();
        harness.Publisher.HoldPublications = true;
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForStartedAsync(1);
        harness.Time.Advance(ArticleWorkRpcTiming.AbsoluteLifetime);
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
        Assert.Contains("absolute", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellation_CancelsPendingLookup()
    {
        await using var harness = CreateHarness();
        using var cts = new CancellationTokenSource();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, cts.Token);
        await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.Equal(0, harness.Router.OutstandingCount);
    }

    [Fact]
    public async Task Success_DoesNotWaitForOutstandingProviderPublications()
    {
        await using var harness = CreateHarness();
        harness.Publisher.HoldPublications = true;
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForStartedAsync(1);
        harness.Publisher.ReleaseOne();
        var storage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");

        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForStartedAsync(13);
        harness.DeliverSuccess(storage);
        var result = await lookup;

        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal(0, harness.Router.OutstandingCount);
        Assert.Single(harness.Publisher.Publications);
        harness.Publisher.ReleaseAll();
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal(0, harness.Router.OutstandingCount);
    }

    [Fact]
    public async Task OutstandingPublishCompletion_AfterSuccess_CannotMutateLookup()
    {
        await using var harness = CreateHarness();
        harness.Publisher.HoldPublications = true;
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForStartedAsync(1);
        harness.Publisher.ReleaseOne();
        var storage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForStartedAsync(13);

        harness.DeliverSuccess(storage);
        var result = await lookup;
        Assert.Equal("backfiller.storage", result.SourceExchange);

        harness.Publisher.ReleaseAll();
        await harness.Publisher.WaitForPublicationCountAsync(13);
        var eweka = harness.Publisher.Publications.Single(static publication => publication.Exchange == "backfiller.eweka");
        harness.DeliverSuccess(eweka, "Eweka");

        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal("backfiller.storage", result.SourceExchange);
        Assert.Equal(0, harness.Router.OutstandingCount);
    }

    [Fact]
    public async Task OutstandingPublishFailure_AfterSuccess_IsObservedWithoutMutatingLookup()
    {
        await using var harness = CreateHarness();
        harness.Publisher.HoldPublications = true;
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForStartedAsync(1);
        harness.Publisher.ReleaseOne();
        var storage = await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForStartedAsync(13);

        harness.DeliverSuccess(storage);
        var result = await lookup;
        harness.Publisher.FailAll(new InvalidOperationException("provider publish failed after success"));

        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal("backfiller.storage", result.SourceExchange);
        Assert.Equal(0, harness.Router.OutstandingCount);
    }

    [Fact]
    public async Task Success_AfterRequestTtlElapsed_StillCompletesInsideFiveSecondLookup()
    {
        await using var harness = CreateHarness();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForPublicationAsync("backfiller.storage");
        harness.Time.Advance(ArticleWorkRpcTiming.StorageGrace);
        await harness.Publisher.WaitForPublicationCountAsync(13);
        harness.Time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(lookup.IsCompleted);

        var eweka = harness.Publisher.Publications.Single(static publication => publication.Exchange == "backfiller.eweka");
        harness.DeliverSuccess(eweka, "Eweka");
        var result = await lookup;

        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal(1100, harness.Time.GetUtcNow().UtcDateTime.TimeOfDay.TotalMilliseconds, precision: 0);
        Assert.True(harness.Time.GetUtcNow().UtcDateTime.TimeOfDay.TotalMilliseconds > 1000);
        Assert.Equal(0, harness.Router.OutstandingCount);
    }

    private static Harness CreateHarness(Func<long>? currentGeneration = null)
    {
        var time = new FakeTimeProvider();
        var publisher = new RecordingArticleWorkRpcPublisher();
        var router = new ArticleWorkRpcResponseRouter(NullLogger.Instance);
        var client = new ArticleWorkRpcClient(publisher, router, time, NullLogger.Instance, currentGeneration);
        return new Harness(time, publisher, router, client);
    }

    private static void AssertNoProviderFanOut(Harness harness)
    {
        Assert.DoesNotContain(
            harness.Publisher.Publications,
            static publication => publication.Exchange.StartsWith("backfiller.", StringComparison.Ordinal)
                && publication.Exchange != "backfiller.storage");
    }

    private static void AssertAmqpContract(FakeRabbitMqRpcPublication publication)
    {
        Assert.False(string.IsNullOrWhiteSpace(publication.ReplyTo));
        Assert.Equal(ArticleWorkWireProtocol.JsonContentType, publication.ContentType);
        Assert.Equal(ArticleWorkRpcAmqp.ExpirationMilliseconds, publication.Expiration);
        Assert.True(Guid.TryParse(publication.RequestId, out var requestId));
        Assert.NotEqual(Guid.Empty, requestId);
        Assert.True(Guid.TryParse(publication.CorrelationId, out var correlationId));
        Assert.NotEqual(Guid.Empty, correlationId);
        Assert.Equal(publication.Exchange, publication.RoutingKey);
        using var document = JsonDocument.Parse(publication.Body);
        Assert.Equal(requestId, document.RootElement.GetProperty("requestId").GetGuid());
    }

    private static byte[] SuccessBody(Guid requestId, string backbone = "Storage")
    {
        return Encoding.UTF8.GetBytes(
            $$"""{"version":1,"requestId":"{{requestId}}","messageId":"{{MessageId}}","backbone":"{{backbone}}","outcome":"Success","uri":"{{SuccessUri}}"}""");
    }

    private static byte[] FailureBody(Guid requestId, ArticleWorkOutcome outcome, string error, string backbone = "Storage")
    {
        return Encoding.UTF8.GetBytes(
            $$"""{"version":1,"requestId":"{{requestId}}","messageId":"{{MessageId}}","backbone":"{{backbone}}","outcome":"{{outcome}}","error":"{{error}}"}""");
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal Harness(
            FakeTimeProvider time,
            RecordingArticleWorkRpcPublisher publisher,
            ArticleWorkRpcResponseRouter router,
            ArticleWorkRpcClient client)
        {
            Time = time;
            Publisher = publisher;
            Router = router;
            Client = client;
        }

        internal FakeTimeProvider Time { get; }

        internal RecordingArticleWorkRpcPublisher Publisher { get; }

        internal ArticleWorkRpcResponseRouter Router { get; }

        internal ArticleWorkRpcClient Client { get; }

        internal void DeliverSuccess(FakeRabbitMqRpcPublication publication, string backbone = "Storage", Guid? requestId = null)
        {
            var id = requestId ?? Guid.Parse(publication.RequestId);
            Router.Dispatch(publication.CorrelationId, SuccessBody(id, backbone), id.ToString("D"), 0);
        }

        internal void DeliverOutcome(FakeRabbitMqRpcPublication publication, ArticleWorkOutcome outcome, string error)
        {
            var requestId = Guid.Parse(publication.RequestId);
            var backbone = JsonDocument.Parse(publication.Body).RootElement.GetProperty("backbone").GetString() ?? "Storage";
            Router.Dispatch(
                publication.CorrelationId,
                FailureBody(requestId, outcome, error, backbone),
                requestId.ToString("D"),
                0);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingArticleWorkRpcPublisher : IArticleWorkRpcPublisher
    {
        private readonly SemaphoreSlim _publishedPulse = new(0, int.MaxValue);
        private readonly SemaphoreSlim _startedPulse = new(0, int.MaxValue);
        private readonly ConcurrentQueue<TaskCompletionSource> _holds = new();
        private int _started;

        public string ReplyTo => "nntpd.01.test.rpc";

        public bool HoldPublications { get; set; }

        public int StartedCount => Volatile.Read(ref _started);

        public List<FakeRabbitMqRpcPublication> Publications { get; } = [];

        public async Task PublishAsync(
            string exchange,
            string routingKey,
            Guid requestId,
            string correlationId,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _started);
            _startedPulse.Release();
            if (HoldPublications)
            {
                var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _holds.Enqueue(hold);
                await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (Publications)
            {
                Publications.Add(new FakeRabbitMqRpcPublication(
                    exchange,
                    routingKey,
                    correlationId,
                    requestId.ToString("D"),
                    ReplyTo,
                    ArticleWorkWireProtocol.JsonContentType,
                    ArticleWorkRpcAmqp.ExpirationMilliseconds,
                    body.ToArray()));
            }

            _publishedPulse.Release();
        }

        public void ReleaseOne()
        {
            if (_holds.TryDequeue(out var hold))
            {
                hold.TrySetResult();
            }
        }

        public void ReleaseAll()
        {
            while (_holds.TryDequeue(out var hold))
            {
                hold.TrySetResult();
            }
        }

        public void FailAll(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            while (_holds.TryDequeue(out var hold))
            {
                hold.TrySetException(exception);
            }
        }

        public async Task<FakeRabbitMqRpcPublication> WaitForPublicationAsync(string exchange, int skip = 0)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                FakeRabbitMqRpcPublication? match;
                lock (Publications)
                {
                    match = Publications.Where(publication => publication.Exchange == exchange).Skip(skip).FirstOrDefault();
                }

                if (match is not null)
                {
                    return match;
                }

                await _publishedPulse.WaitAsync(cts.Token).ConfigureAwait(false);
            }
        }

        public async Task WaitForPublicationCountAsync(int count)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                lock (Publications)
                {
                    if (Publications.Count >= count)
                    {
                        return;
                    }
                }

                await _publishedPulse.WaitAsync(cts.Token).ConfigureAwait(false);
            }
        }

        public async Task WaitForStartedAsync(int count)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (StartedCount < count)
            {
                await _startedPulse.WaitAsync(cts.Token).ConfigureAwait(false);
            }
        }
    }
}
