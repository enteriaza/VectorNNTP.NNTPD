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
    private const string SuccessUri = "vatp://backfiller01.usenet.ninja:119/dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14";
    private const string SuccessArticleIdHex = "dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14";

    [Fact]
    public async Task No_active_consumers_publishes_nothing()
    {
        await using var harness = CreateHarness(eligible: []);
        var result = await harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
        Assert.Contains("No BackFiller", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Publisher.Publications);
    }

    [Fact]
    public async Task One_active_backbone_receives_exactly_one_request()
    {
        await using var harness = CreateHarness(Eligible("Giganews", 10));
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var publication = await harness.Publisher.WaitForPublicationAsync("backfiller.giganews");
        AssertAmqpContract(publication);
        Assert.Equal("Giganews", ReadBackbone(publication));
        harness.DeliverSuccess(publication, "Giganews");
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Single(harness.Publisher.Publications);
    }

    [Fact]
    public async Task Two_active_backbones_first_attempt_goes_to_exactly_one()
    {
        await using var harness = CreateHarness(
            [Eligible("Giganews", 100), Eligible("Eweka", 50)],
            nextInt: static _ => 0);
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForPublicationCountAsync(1);
        Assert.Single(harness.Publisher.Publications);
        Assert.Equal("backfiller.giganews", harness.Publisher.Publications[0].Exchange);
        harness.DeliverSuccess(harness.Publisher.Publications[0], "Giganews");
        Assert.Equal(ArticleWorkOutcome.Success, (await lookup).Outcome);
    }

    [Fact]
    public async Task First_not_found_publishes_new_request_to_another_backbone()
    {
        await using var harness = CreateHarness(
            [Eligible("Giganews", 100), Eligible("Eweka", 50)],
            CreateSequentialRolls(0, 0));
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var first = await harness.Publisher.WaitForPublicationAsync("backfiller.giganews");
        harness.DeliverOutcome(first, ArticleWorkOutcome.ArticleNotFound, "missing");
        var second = await harness.Publisher.WaitForPublicationAsync("backfiller.eweka");
        Assert.Equal(2, harness.Publisher.Publications.Count);
        Assert.NotEqual(first.CorrelationId, second.CorrelationId);
        Assert.Equal(Guid.Parse(first.RequestId), Guid.Parse(second.RequestId));
        harness.DeliverSuccess(second, "Eweka");
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal("Eweka", result.Backbone);
        Assert.Equal(2, harness.Publisher.Publications.Count);
    }

    [Fact]
    public async Task First_not_found_never_retries_same_backbone()
    {
        await using var harness = CreateHarness(
            [Eligible("Giganews", 100), Eligible("Eweka", 1)],
            nextInt: static _ => 0);
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var first = await harness.Publisher.WaitForPublicationAsync("backfiller.giganews");
        harness.DeliverOutcome(first, ArticleWorkOutcome.ArticleNotFound, "missing");
        await harness.Publisher.WaitForPublicationCountAsync(2);
        Assert.Equal("backfiller.eweka", harness.Publisher.Publications[1].Exchange);
        harness.DeliverOutcome(harness.Publisher.Publications[1], ArticleWorkOutcome.ArticleNotFound, "missing");
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
        Assert.Equal(2, harness.Publisher.Publications.Count);
        Assert.DoesNotContain(
            harness.Publisher.Publications.Skip(1),
            static p => p.Exchange == "backfiller.giganews");
    }

    [Fact]
    public async Task All_eligible_not_found_returns_not_found_without_repeats()
    {
        await using var harness = CreateHarness(
            [Eligible("Giganews", 2), Eligible("Eweka", 2)],
            CreateSequentialRolls(0, 0));
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForPublicationCountAsync(1);
        harness.DeliverOutcome(harness.Publisher.Publications[0], ArticleWorkOutcome.ArticleNotFound, "a");
        await harness.Publisher.WaitForPublicationCountAsync(2);
        harness.DeliverOutcome(harness.Publisher.Publications[1], ArticleWorkOutcome.ArticleNotFound, "b");
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
        Assert.Equal(2, harness.Publisher.Publications.Count);
        Assert.Equal(
            new[] { "backfiller.eweka", "backfiller.giganews" },
            harness.Publisher.Publications.Select(static p => p.Exchange).OrderBy(static x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Zero_consumer_backbone_is_never_selected()
    {
        await using var harness = CreateHarness(Eligible("Eweka", 5));
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var publication = await harness.Publisher.WaitForPublicationAsync("backfiller.eweka");
        Assert.DoesNotContain(harness.Publisher.Publications, static p => p.Exchange.Contains("giganews", StringComparison.Ordinal));
        harness.DeliverSuccess(publication, "Eweka");
        Assert.Equal(ArticleWorkOutcome.Success, (await lookup).Outcome);
    }

    [Fact]
    public async Task Success_stops_immediately_without_further_publishes()
    {
        await using var harness = CreateHarness(
            [Eligible("Giganews", 100), Eligible("Eweka", 50)],
            nextInt: static _ => 0);
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var first = await harness.Publisher.WaitForPublicationAsync("backfiller.giganews");
        harness.DeliverSuccess(first, "Giganews");
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Single(harness.Publisher.Publications);
    }

    [Fact]
    public async Task Cancellation_stops_without_launching_another_attempt()
    {
        await using var harness = CreateHarness(Eligible("Giganews", 10));
        using var cts = new CancellationTokenSource();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, cts.Token);
        await harness.Publisher.WaitForPublicationAsync("backfiller.giganews");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await lookup);
        Assert.Single(harness.Publisher.Publications);
    }

    [Fact]
    public async Task Attempt_timeout_without_response_does_not_start_another_backbone()
    {
        await using var harness = CreateHarness(
            [Eligible("Giganews", 100), Eligible("Eweka", 50)],
            nextInt: static _ => 0);
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForPublicationAsync("backfiller.giganews");
        harness.Time.Advance(ArticleWorkRpcTiming.LookupDeadline);
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
        Assert.True(
            result.Error is not null
            && (result.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || result.Error.Contains("deadline elapsed", StringComparison.OrdinalIgnoreCase)),
            result.Error);
        Assert.Single(harness.Publisher.Publications);
        Assert.Equal(TimeSpan.FromSeconds(5), ArticleWorkRpcTiming.LookupDeadline);
    }

    [Fact]
    public async Task Lookup_completes_by_five_second_scheduler_deadline()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ArticleWorkRpcTiming.LookupDeadline);
        await using var harness = CreateHarness(
            [Eligible("Giganews", 10), Eligible("Eweka", 10)],
            nextInt: static _ => 0);
        var started = harness.Time.GetUtcNow();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        await harness.Publisher.WaitForPublicationAsync("backfiller.giganews");
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
        Assert.True(harness.Time.GetUtcNow() - started <= ArticleWorkRpcTiming.LookupDeadline);
        Assert.Single(harness.Publisher.Publications);
        Assert.DoesNotContain("absolute", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("60", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Definitive_not_found_before_deadline_advances_to_next_backbone()
    {
        await using var harness = CreateHarness(
            [Eligible("Giganews", 100), Eligible("Eweka", 50)],
            CreateSequentialRolls(0, 0));
        var started = harness.Time.GetUtcNow();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var first = await harness.Publisher.WaitForPublicationAsync("backfiller.giganews");
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        harness.DeliverOutcome(first, ArticleWorkOutcome.ArticleNotFound, "missing");
        var second = await harness.Publisher.WaitForPublicationAsync("backfiller.eweka");
        harness.DeliverSuccess(second, "Eweka");
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal(2, harness.Publisher.Publications.Count);
        Assert.True(harness.Time.GetUtcNow() - started < ArticleWorkRpcTiming.LookupDeadline);
    }

    [Fact]
    public async Task Success_before_deadline_terminates_immediately()
    {
        await using var harness = CreateHarness(
            [Eligible("Giganews", 100), Eligible("Eweka", 50)],
            nextInt: static _ => 0);
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var first = await harness.Publisher.WaitForPublicationAsync("backfiller.giganews");
        harness.Time.Advance(TimeSpan.FromMilliseconds(250));
        harness.DeliverSuccess(first, "Giganews");
        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Single(harness.Publisher.Publications);
    }

    [Fact]
    public void Lookup_deadline_is_five_seconds_with_no_separate_sixty_second_ceiling()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ArticleWorkRpcTiming.LookupDeadline);
        Assert.Null(typeof(ArticleWorkRpcTiming).GetField(
            "AbsoluteLifetime",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public void Weighted_selection_is_approximately_two_to_one_for_100_vs_50()
    {
        var roll = 0;
        var selector = new WeightedBackboneSelector(exclusiveMax =>
        {
            var value = roll % exclusiveMax;
            roll++;
            return value;
        });
        var candidates = new[]
        {
            Eligible("Giganews", 100),
            Eligible("Eweka", 50),
        };
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        const int iterations = 15_000;
        for (var i = 0; i < iterations; i++)
        {
            var pick = selector.Select(candidates)!.Value.Backbone;
            counts[pick] = counts.GetValueOrDefault(pick) + 1;
        }

        var ratio = counts["Giganews"] / (double)counts["Eweka"];
        Assert.InRange(ratio, 1.7, 2.3);
    }

    [Fact]
    public async Task Invalid_article_is_attempt_terminal_and_tries_next()
    {
        await using var harness = CreateHarness(
            [Eligible("Giganews", 10), Eligible("Eweka", 10)],
            CreateSequentialRolls(0, 0));
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
        var first = await harness.Publisher.WaitForPublicationAsync("backfiller.giganews");
        harness.DeliverOutcome(first, ArticleWorkOutcome.InvalidArticle, "bad yenc");
        var second = await harness.Publisher.WaitForPublicationAsync("backfiller.eweka");
        harness.DeliverSuccess(second, "Eweka");
        Assert.Equal(ArticleWorkOutcome.Success, (await lookup).Outcome);
    }

    [Fact]
    public async Task Repeated_lookups_with_single_active_backbone_never_touch_others()
    {
        await using var harness = CreateHarness(Eligible("Giganews", 25));
        for (var i = 0; i < 8; i++)
        {
            var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
            var publication = await harness.Publisher.WaitForPublicationAsync("backfiller.giganews", skip: i);
            Assert.Equal("backfiller.giganews", publication.Exchange);
            harness.DeliverSuccess(publication, "Giganews");
            Assert.Equal(ArticleWorkOutcome.Success, (await lookup).Outcome);
        }

        Assert.Equal(8, harness.Publisher.Publications.Count);
        Assert.All(
            harness.Publisher.Publications,
            static p => Assert.Equal("backfiller.giganews", p.Exchange));
        Assert.DoesNotContain(
            harness.Publisher.Publications,
            static p =>
                p.Exchange.Contains("eweka", StringComparison.Ordinal)
                || p.Exchange.Contains("highwinds", StringComparison.Ordinal)
                || p.Exchange.Contains("storage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Zero_consumer_and_storage_never_receive_attempts_even_when_listed()
    {
        await using var harness = CreateHarness(
            [
                Eligible("Giganews", 100),
                Eligible("Eweka", 50),
                Eligible("Highwinds", 0),
                new BackfillEligibleBackbone(
                    "Storage",
                    StorageArticleRetrievalTopology.EntityName,
                    StorageArticleRetrievalTopology.EntityName,
                    StorageArticleRetrievalTopology.EntityName,
                    0),
            ],
            CreateSequentialRolls(0, 0));

        for (var i = 0; i < 6; i++)
        {
            var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);
            await harness.Publisher.WaitForPublicationCountAsync(i + 1);
            var publication = harness.Publisher.Publications[i];
            Assert.Contains(
                publication.Exchange,
                new[] { "backfiller.giganews", "backfiller.eweka" },
                StringComparer.Ordinal);
            harness.DeliverSuccess(publication, ReadBackbone(publication));
            Assert.Equal(ArticleWorkOutcome.Success, (await lookup).Outcome);
        }

        Assert.DoesNotContain(
            harness.Publisher.Publications,
            static p =>
                p.Exchange.Equals("backfiller.highwinds", StringComparison.Ordinal)
                || p.Exchange.Equals(StorageArticleRetrievalTopology.EntityName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Three_backbone_not_found_chain_never_reuses_attempted_backbone()
    {
        await using var harness = CreateHarness(
            [Eligible("Giganews", 10), Eligible("Eweka", 10), Eligible("Highwinds", 10)],
            CreateSequentialRolls(0, 0, 0));
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);

        var first = await harness.Publisher.WaitForPublicationAsync("backfiller.giganews");
        harness.DeliverOutcome(first, ArticleWorkOutcome.ArticleNotFound, "missing-g");
        var second = await harness.Publisher.WaitForPublicationAsync("backfiller.eweka");
        harness.DeliverOutcome(second, ArticleWorkOutcome.ArticleNotFound, "missing-e");
        var third = await harness.Publisher.WaitForPublicationAsync("backfiller.highwinds");
        harness.DeliverSuccess(third, "Highwinds");

        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
        Assert.Equal("Highwinds", result.Backbone);
        Assert.Equal(3, harness.Publisher.Publications.Count);
        Assert.Equal(
            new[] { "backfiller.giganews", "backfiller.eweka", "backfiller.highwinds" },
            harness.Publisher.Publications.Select(static p => p.Exchange).ToArray());
        Assert.Equal(
            Guid.Parse(first.RequestId),
            Guid.Parse(second.RequestId));
        Assert.Equal(
            Guid.Parse(first.RequestId),
            Guid.Parse(third.RequestId));
        Assert.NotEqual(first.CorrelationId, second.CorrelationId);
        Assert.NotEqual(second.CorrelationId, third.CorrelationId);
        Assert.Equal(1, harness.Publisher.Publications.Count(static p => p.Exchange == "backfiller.giganews"));
    }

    [Fact]
    public async Task Global_deadline_bounds_total_lookup_not_per_attempt()
    {
        await using var harness = CreateHarness(
            [Eligible("Giganews", 10), Eligible("Eweka", 10), Eligible("Highwinds", 10)],
            CreateSequentialRolls(0, 0, 0));
        var started = harness.Time.GetUtcNow();
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);

        var first = await harness.Publisher.WaitForPublicationAsync("backfiller.giganews");
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        harness.DeliverOutcome(first, ArticleWorkOutcome.ArticleNotFound, "missing");

        await harness.Publisher.WaitForPublicationAsync("backfiller.eweka");
        harness.Time.Advance(TimeSpan.FromSeconds(4));
        var result = await lookup;

        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
        Assert.Equal(2, harness.Publisher.Publications.Count);
        Assert.DoesNotContain(
            harness.Publisher.Publications,
            static p => p.Exchange == "backfiller.highwinds");
        Assert.True(harness.Time.GetUtcNow() - started <= ArticleWorkRpcTiming.LookupDeadline);
    }

    [Fact]
    public async Task All_three_backbones_not_found_attempts_each_once_only()
    {
        await using var harness = CreateHarness(
            [Eligible("Giganews", 5), Eligible("Eweka", 5), Eligible("Highwinds", 5)],
            CreateSequentialRolls(0, 0, 0));
        var lookup = harness.Client.LookupByMessageIdAsync(MessageIdBytes, CancellationToken.None);

        await harness.Publisher.WaitForPublicationCountAsync(1);
        harness.DeliverOutcome(harness.Publisher.Publications[0], ArticleWorkOutcome.ArticleNotFound, "a");
        await harness.Publisher.WaitForPublicationCountAsync(2);
        harness.DeliverOutcome(harness.Publisher.Publications[1], ArticleWorkOutcome.ArticleNotFound, "b");
        await harness.Publisher.WaitForPublicationCountAsync(3);
        harness.DeliverOutcome(harness.Publisher.Publications[2], ArticleWorkOutcome.ArticleNotFound, "c");

        var result = await lookup;
        Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
        Assert.Equal(3, harness.Publisher.Publications.Count);
        Assert.Equal(
            3,
            harness.Publisher.Publications.Select(static p => p.Exchange).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            Guid.Parse(harness.Publisher.Publications[0].RequestId),
            Guid.Parse(harness.Publisher.Publications[2].RequestId));
        Assert.Equal(
            3,
            harness.Publisher.Publications.Select(static p => p.CorrelationId).Distinct(StringComparer.Ordinal).Count());
    }

    private static Func<int, int> CreateSequentialRolls(params int[] rolls)
    {
        var index = 0;
        return exclusiveMax =>
        {
            _ = exclusiveMax;
            var value = rolls[Math.Min(index, rolls.Length - 1)];
            index++;
            return value;
        };
    }

    private static BackfillEligibleBackbone Eligible(string backbone, int consumers)
    {
        var entity = BackfillArticleRetrievalTopology.BuildProviderEntityName(backbone);
        return new BackfillEligibleBackbone(backbone, entity, entity, entity, consumers);
    }

    private static Harness CreateHarness(params BackfillEligibleBackbone[] eligible) =>
        CreateHarness(eligible, nextInt: static _ => 0);

    private static Harness CreateHarness(BackfillEligibleBackbone[] eligible, Func<int, int> nextInt) =>
        CreateHarness(eligible, nextInt, currentGeneration: null);

    private static Harness CreateHarness(
        BackfillEligibleBackbone[] eligible,
        Func<int, int>? nextInt,
        Func<long>? currentGeneration)
    {
        var time = new FakeTimeProvider();
        var publisher = new RecordingArticleWorkRpcPublisher();
        var router = new ArticleWorkRpcResponseRouter(NullLogger.Instance);
        var availability = new StaticBackfillConsumerAvailability(eligible);
        var selector = new WeightedBackboneSelector(nextInt ?? (static _ => 0));
        var client = new ArticleWorkRpcClient(
            publisher,
            router,
            availability,
            selector,
            time,
            NullLogger.Instance,
            currentGeneration);
        return new Harness(time, publisher, router, client, availability);
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

    private static string ReadBackbone(FakeRabbitMqRpcPublication publication) =>
        JsonDocument.Parse(publication.Body).RootElement.GetProperty("backbone").GetString()!;

    private static byte[] SuccessBody(Guid requestId, string backbone)
    {
        return Encoding.UTF8.GetBytes(
            $$"""{"version":1,"requestId":"{{requestId}}","messageId":"{{MessageId}}","backbone":"{{backbone}}","outcome":"Success","uri":"{{SuccessUri}}","articleId":"{{SuccessArticleIdHex}}"}""");
    }

    private static byte[] FailureBody(Guid requestId, ArticleWorkOutcome outcome, string error, string backbone)
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
            ArticleWorkRpcClient client,
            StaticBackfillConsumerAvailability availability)
        {
            Time = time;
            Publisher = publisher;
            Router = router;
            Client = client;
            Availability = availability;
        }

        internal FakeTimeProvider Time { get; }

        internal RecordingArticleWorkRpcPublisher Publisher { get; }

        internal ArticleWorkRpcResponseRouter Router { get; }

        internal ArticleWorkRpcClient Client { get; }

        internal StaticBackfillConsumerAvailability Availability { get; }

        internal void DeliverSuccess(FakeRabbitMqRpcPublication publication, string backbone, Guid? requestId = null)
        {
            var id = requestId ?? Guid.Parse(publication.RequestId);
            Router.Dispatch(publication.CorrelationId, SuccessBody(id, backbone), id.ToString("D"), 0);
        }

        internal void DeliverOutcome(FakeRabbitMqRpcPublication publication, ArticleWorkOutcome outcome, string error)
        {
            var requestId = Guid.Parse(publication.RequestId);
            var backbone = ReadBackbone(publication);
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

        public string ReplyTo => "nntpd.01.test.rpc";

        public List<FakeRabbitMqRpcPublication> Publications { get; } = [];

        public Task PublishAsync(
            string exchange,
            string routingKey,
            Guid requestId,
            string correlationId,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
        {
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
            return Task.CompletedTask;
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
    }
}
