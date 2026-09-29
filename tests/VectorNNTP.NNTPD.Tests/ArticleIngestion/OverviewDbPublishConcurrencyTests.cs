using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Diagnostics;
using VectorNNTP.NNTPD.RabbitMq;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.RabbitMq;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.ArticleIngestion;

/// <summary>Bounded OverviewDB publish-channel pool concurrency contracts.</summary>
public sealed class OverviewDbPublishConcurrencyTests
{
    [Fact]
    public async Task ConcurrentPublishes_CreateSeparateChannels_AndRespectConcurrencyBound()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        await rabbit.StartAsync(CancellationToken.None);

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var peak = 0;
        var connection = factory.LastConnection!;
        connection.ConfigurePublishChannel = channel =>
        {
            channel.BeforeConfirmAsync = async () =>
            {
                var now = Interlocked.Increment(ref entered);
                while (true)
                {
                    var observed = Volatile.Read(ref peak);
                    if (now <= observed || Interlocked.CompareExchange(ref peak, now, observed) == observed)
                    {
                        break;
                    }
                }

                await hold.Task.ConfigureAwait(false);
                Interlocked.Decrement(ref entered);
            };
        };

        var nntpd = TestHostFactory.CreateValidOptions();
        nntpd.ArticleIngestion = new ArticleIngestionOptions { MaxPublishConcurrency = 2 };
        await using var publisher = new OverviewDbHandoffPublisher(
            rabbit,
            Options.Create(nntpd),
            Options.Create(RabbitMqOptionsTests.CreateValid()));

        var p1 = publisher.PublishConfirmedAsync("a"u8.ToArray(), CancellationToken.None);
        var p2 = publisher.PublishConfirmedAsync("b"u8.ToArray(), CancellationToken.None);
        var p3 = publisher.PublishConfirmedAsync("c"u8.ToArray(), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref peak) < 2)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }

        Assert.Equal(2, Volatile.Read(ref peak));
        Assert.Equal(2, publisher.InFlightPublishes);
        Assert.Equal(2, connection.PublishChannels.Count);

        hold.TrySetResult();
        await Task.WhenAll(p1, p2, p3).WaitAsync(cts.Token);
        Assert.Equal(0, publisher.InFlightPublishes);
        Assert.Equal(2, connection.PublishChannels.Count);
        Assert.Equal(3, connection.PublishChannels.Sum(static c => c.Publications.Count));
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
