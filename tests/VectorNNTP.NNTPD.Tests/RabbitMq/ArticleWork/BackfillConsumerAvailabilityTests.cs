using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.NNTPD.RabbitMq;
using VectorNNTP.NNTPD.RabbitMq.ArticleWork;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.RabbitMq.ArticleWork;

/// <summary>
/// Proves the lazy-on-demand consumer-count cache: refresh only when a lookup
/// asks for eligibility and the ~5s validity window has elapsed.
/// </summary>
public sealed class BackfillConsumerAvailabilityTests
{
    [Fact]
    public async Task Cache_is_lazy_no_probe_without_GetEligibleBackbones()
    {
        var time = new FakeTimeProvider();
        var factory = new FakeRabbitMqConnectionFactory();
        SeedConsumers(factory, ("Giganews", 10u));
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);
        await using var availability = new BackfillConsumerAvailabilityService(
            rabbit,
            NullLogger.Instance,
            time);

        Assert.Empty(factory.LastConnection!.TopologyChannels);

        await Task.Delay(20);
        Assert.Empty(factory.LastConnection.TopologyChannels);
    }

    [Fact]
    public async Task First_GetEligible_probes_once_and_reuses_within_refresh_interval()
    {
        var time = new FakeTimeProvider();
        var factory = new FakeRabbitMqConnectionFactory();
        SeedConsumers(factory, ("Giganews", 100u), ("Eweka", 50u), ("Highwinds", 0u));
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);
        await using var availability = new BackfillConsumerAvailabilityService(
            rabbit,
            NullLogger.Instance,
            time);

        var first = availability.GetEligibleBackbones();
        Assert.Equal(2, first.Count);
        Assert.Contains(first, static b => b.Backbone == "Giganews" && b.ConsumerCount == 100);
        Assert.Contains(first, static b => b.Backbone == "Eweka" && b.ConsumerCount == 50);
        Assert.DoesNotContain(first, static b => b.Backbone.Equals("Highwinds", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(first, static b => b.QueueName.Contains("storage", StringComparison.Ordinal));

        var probesAfterFirst = CountPassiveDeclares(factory.LastConnection!);
        Assert.Equal(BackfillArticleRetrievalTopology.Definitions.Count, probesAfterFirst);
        Assert.Single(factory.LastConnection!.TopologyChannels);

        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            _ = availability.GetEligibleBackbones();
        }

        Assert.Equal(probesAfterFirst, CountPassiveDeclares(factory.LastConnection));
        Assert.Single(factory.LastConnection.TopologyChannels);
    }

    [Fact]
    public async Task After_refresh_interval_next_GetEligible_requeries_and_picks_up_changes()
    {
        var time = new FakeTimeProvider();
        var factory = new FakeRabbitMqConnectionFactory();
        SeedConsumers(factory, ("Giganews", 10u));
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);
        await using var availability = new BackfillConsumerAvailabilityService(
            rabbit,
            NullLogger.Instance,
            time);

        var initial = availability.GetEligibleBackbones();
        Assert.Single(initial);
        Assert.Equal("Giganews", initial[0].Backbone);

        factory.LastConnection!.PassiveConsumerCounts["backfiller.eweka"] = 25;
        factory.LastConnection.PassiveConsumerCounts["backfiller.giganews"] = 0;

        time.Advance(BackfillConsumerAvailabilityService.RefreshInterval - TimeSpan.FromMilliseconds(1));
        var stillCached = availability.GetEligibleBackbones();
        Assert.Single(stillCached);
        Assert.Equal("Giganews", stillCached[0].Backbone);
        Assert.Single(factory.LastConnection.TopologyChannels);

        time.Advance(TimeSpan.FromMilliseconds(1));
        var refreshed = availability.GetEligibleBackbones();
        Assert.Single(refreshed);
        Assert.Equal("Eweka", refreshed[0].Backbone);
        Assert.Equal(25, refreshed[0].ConsumerCount);
        Assert.Equal(2, factory.LastConnection.TopologyChannels.Count);
        Assert.Equal(
            BackfillArticleRetrievalTopology.Definitions.Count * 2,
            CountPassiveDeclares(factory.LastConnection));
    }

    [Fact]
    public async Task Storage_queue_is_never_probed_or_selected()
    {
        var time = new FakeTimeProvider();
        var factory = new FakeRabbitMqConnectionFactory();
        SeedConsumers(factory, ("Giganews", 5u));
        factory.PassiveConsumerCounts[StorageArticleRetrievalTopology.EntityName] = 99;
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);
        await using var availability = new BackfillConsumerAvailabilityService(
            rabbit,
            NullLogger.Instance,
            time);

        var eligible = availability.GetEligibleBackbones();
        Assert.DoesNotContain(
            eligible,
            static b => b.QueueName.Equals(StorageArticleRetrievalTopology.EntityName, StringComparison.Ordinal));
        var channel = Assert.Single(factory.LastConnection!.TopologyChannels);
        Assert.DoesNotContain(
            channel.PassiveDeclareQueues,
            static q => q.Equals(StorageArticleRetrievalTopology.EntityName, StringComparison.Ordinal));
    }

    private static void SeedConsumers(FakeRabbitMqConnectionFactory factory, params (string Backbone, uint Count)[] counts)
    {
        foreach (var (backbone, count) in counts)
        {
            factory.PassiveConsumerCounts[BackfillArticleRetrievalTopology.BuildProviderEntityName(backbone)] = count;
        }
    }

    private static int CountPassiveDeclares(FakeRabbitMqConnection connection) =>
        connection.TopologyChannels.Sum(static channel => channel.PassiveDeclareCount);

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
