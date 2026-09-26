using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Hosting;
using VectorNNTP.NNTPD.Logging;
using VectorNNTP.NNTPD.RabbitMq;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.RabbitMq;

[Collection(SerilogCollection.Name)]
public sealed class RabbitMqTopologyServiceTests
{
    [Fact]
    public async Task StartAsync_DeclaresThirteenEndpoints_IncludingStorageRequests()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await topology.StartAsync(CancellationToken.None);

        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        var channel = Assert.Single(connection.TopologyChannels);
        Assert.Equal(1, channel.DisposeCount);
        AssertDeclaredTopology(connection, expectedPasses: 1);

        await topology.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeclareRequiredTopologyAsync_IsIdempotent_AndDoesNotDeleteOrPurge()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await topology.StartAsync(CancellationToken.None);
        await topology.DeclareRequiredTopologyAsync(CancellationToken.None);

        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        Assert.Equal(2, connection.TopologyChannels.Count);
        Assert.All(connection.TopologyChannels, static channel => Assert.Equal(1, channel.DisposeCount));
        AssertDeclaredTopology(connection, expectedPasses: 2);
        var channelMethods = typeof(IRabbitMqTopologyChannel).GetMethods().Select(static m => m.Name).ToHashSet();
        Assert.Contains(nameof(IRabbitMqTopologyChannel.ExchangeDeclareAsync), channelMethods);
        Assert.Contains(nameof(IRabbitMqTopologyChannel.QueueDeclareAsync), channelMethods);
        Assert.Contains(nameof(IRabbitMqTopologyChannel.QueueBindAsync), channelMethods);
        Assert.DoesNotContain("QueueDeleteAsync", channelMethods);
        Assert.DoesNotContain("QueuePurgeAsync", channelMethods);
        Assert.DoesNotContain("ExchangeDeleteAsync", channelMethods);
        Assert.DoesNotContain("BasicPublishAsync", channelMethods);
        Assert.DoesNotContain("BasicConsumeAsync", channelMethods);
    }

    [Fact]
    public async Task StartAsync_SecondCall_DoesNotRedeclare()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await topology.StartAsync(CancellationToken.None);
        await topology.StartAsync(CancellationToken.None);

        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        Assert.Single(connection.TopologyChannels);
    }

    [Fact]
    public async Task StartAsync_Fails_WhenRabbitMqHasNotStarted()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => topology.StartAsync(CancellationToken.None));

        Assert.Contains("not ready", ex.Message, StringComparison.Ordinal);
        Assert.Null(factory.LastConnection);
    }

    [Fact]
    public async Task StartAsync_Fails_WhenQueueDeclareIsRejected()
    {
        var factory = new FakeRabbitMqConnectionFactory
        {
            QueueDeclareException = new InvalidOperationException("PRECONDITION_FAILED - inequivalent arg 'x-queue-type' for queue 'grabbers.abavia'"),
        };
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => topology.StartAsync(CancellationToken.None));

        Assert.Contains("x-queue-type", ex.Message, StringComparison.Ordinal);
        Assert.Single(factory.LastConnection!.TopologyChannels);
        Assert.Equal(1, factory.LastConnection.TopologyChannels[0].DisposeCount);
    }

    [Fact]
    public async Task ApplicationServiceManager_TopologyFailure_PreventsStartup_AndRollsBackRabbitMq()
    {
        var factory = new FakeRabbitMqConnectionFactory
        {
            QueueDeclareException = new InvalidOperationException("classic queue cannot become quorum"),
        };
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);
        var manager = TestHostFactory.CreateServiceManager([rabbit, topology]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StartAsync(CancellationToken.None));

        Assert.Contains("quorum", ex.Message, StringComparison.Ordinal);
        Assert.Empty(manager.StartedServices);
        Assert.False(rabbit.IsReady);
        Assert.False(rabbit.TryGetCurrent(out _));
    }

    [Fact]
    public async Task Host_Start_DeclaresArticleRetrievalTopology_BeforeRunning()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpdHosting(includePlaceholderService: false);

        using var host = builder.Build();
        await host.StartAsync();

        Assert.Equal(ApplicationState.Running, host.Services.GetRequiredService<ApplicationLifecycle>().State);
        var factory = Assert.IsType<FakeRabbitMqConnectionFactory>(
            host.Services.GetRequiredService<IRabbitMqConnectionFactory>());
        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        AssertDeclaredTopology(connection, expectedPasses: 1);

        await host.StopAsync();
    }

    [Fact]
    public void Host_RegistersTopologyServiceOnce_ImmediatelyAfterRabbitMqConnection()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpdHosting(includePlaceholderService: false);

        using var host = builder.Build();
        var services = host.Services.GetServices<IApplicationService>().ToArray();

        Assert.Equal(typeof(RabbitMqService), services[2].GetType());
        Assert.Equal(typeof(RabbitMqTopologyService), services[3].GetType());
        Assert.Equal(1, services.Count(static s => s is RabbitMqTopologyService));
        Assert.Same(
            host.Services.GetRequiredService<RabbitMqTopologyService>(),
            services[3]);
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

    private static void AssertDeclaredTopology(FakeRabbitMqConnection connection, int expectedPasses)
    {
        var definitions = ArticleRetrievalTopology.Required;
        Assert.Equal(13, definitions.Count);
        Assert.Equal(13 * expectedPasses, connection.ExchangeDeclarations.Count);
        Assert.Equal(13 * expectedPasses, connection.QueueDeclarations.Count);
        Assert.Equal(13 * expectedPasses, connection.BindingDeclarations.Count);
        Assert.Equal("storage.requests", definitions[^1].ExchangeName);
        Assert.Equal("storage.requests", definitions[^1].QueueName);
        Assert.Equal("storage.requests", definitions[^1].RoutingKey);

        for (var pass = 0; pass < expectedPasses; pass++)
        {
            var offset = pass * definitions.Count;
            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                var exchange = connection.ExchangeDeclarations[offset + i];
                Assert.Equal(definition.ExchangeName, exchange.Name);
                Assert.Equal(definition.ExchangeType, exchange.Type);
                Assert.Equal("fanout", exchange.Type);
                Assert.True(exchange.Durable);
                Assert.False(exchange.AutoDelete);
                Assert.Null(exchange.Arguments);

                var queue = connection.QueueDeclarations[offset + i];
                Assert.Equal(definition.QueueName, queue.Name);
                Assert.True(queue.Durable);
                Assert.False(queue.Exclusive);
                Assert.False(queue.AutoDelete);
                Assert.NotNull(queue.Arguments);
                Assert.Single(queue.Arguments);
                Assert.True(queue.Arguments.TryGetValue(
                    RabbitMqArticleRetrievalEndpoints.QueueTypeArgumentName,
                    out var queueType));
                Assert.Equal(RabbitMqArticleRetrievalEndpoints.QuorumQueueType, queueType);

                var binding = connection.BindingDeclarations[offset + i];
                Assert.Equal(definition.QueueName, binding.Queue);
                Assert.Equal(definition.ExchangeName, binding.Exchange);
                Assert.Equal(definition.RoutingKey, binding.RoutingKey);
                Assert.Null(binding.Arguments);
            }
        }
    }
}
