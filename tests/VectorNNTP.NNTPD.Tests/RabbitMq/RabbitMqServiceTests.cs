using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.RabbitMq;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.RabbitMq;

public sealed class RabbitMqServiceTests
{
    [Fact]
    public async Task StartAsync_ConnectsOnce_AndIsReady()
    {
        var connector = new FakeRabbitMqBrokerConnector();
        var replaced = new List<RabbitMqConnectionReplacedEventArgs>();
        var service = CreateService(connector);
        service.ConnectionReplaced += (_, args) => replaced.Add(args);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, connector.ConnectCount);
        Assert.Equal(1, service.ConnectionCount);
        Assert.Equal(1, service.ConnectionGeneration);
        Assert.True(service.IsReady);
        Assert.Equal(RabbitMqInfrastructureState.Connected, service.State);
        Assert.NotNull(service.Execution);
        var generation = Assert.Single(replaced);
        Assert.Equal(1, generation.ConnectionGeneration);
        Assert.False(generation.IsReplacement);
        Assert.Equal("VectorNNTP.NNTPD:nntpd01.usenet.ninja", connector.LastConnection!.ClientProvidedName);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_Fails_WhenConnectThrows()
    {
        var connector = new FakeRabbitMqBrokerConnector
        {
            ConnectException = new InvalidOperationException("broker down"),
        };
        var service = CreateService(connector);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Equal("broker down", ex.Message);
        Assert.Equal(0, service.ConnectionCount);
        Assert.False(service.IsReady);
        Assert.Equal(RabbitMqInfrastructureState.Failed, service.State);
        Assert.Null(service.Execution);
    }

    [Fact]
    public async Task StartAsync_Fails_WhenConnectionIsNotUsable()
    {
        var connector = new FakeRabbitMqBrokerConnector { ReturnUnusableConnection = true };
        var service = CreateService(connector);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Equal(0, service.ConnectionCount);
        Assert.False(service.IsReady);
        Assert.Equal(1, connector.LastConnection!.DisposeCount);
    }

    [Fact]
    public async Task StartAsync_Cancelled_DoesNotLeaveConnection()
    {
        var connector = new FakeRabbitMqBrokerConnector
        {
            BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var service = CreateService(connector);
        using var cts = new CancellationTokenSource();

        var start = service.StartAsync(cts.Token);
        cts.Cancel();
        connector.BlockConnect.TrySetCanceled(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(0, service.ConnectionCount);
        Assert.False(service.IsReady);
    }

    [Fact]
    public async Task ConnectionShutdown_ReplacesConnection_AndIncrementsGeneration()
    {
        var connector = new FakeRabbitMqBrokerConnector();
        var replaced = new List<RabbitMqConnectionReplacedEventArgs>();
        var service = CreateService(connector, poolReconnectBaseDelayMs: 50);
        service.ConnectionReplaced += (_, args) => replaced.Add(args);

        await service.StartAsync(CancellationToken.None);
        var first = connector.LastConnection!;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.Connected = secondConnected;

        first.SimulateShutdown();

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var count = await secondConnected.Task.WaitAsync(safety.Token);

        Assert.Equal(2, count);
        Assert.Equal(2, service.ConnectionCount);
        Assert.Equal(2, service.ConnectionGeneration);
        Assert.True(service.IsReady);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(2, replaced.Count);
        Assert.True(replaced[1].IsReplacement);
        Assert.Equal(2, replaced[1].ConnectionGeneration);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_DisposesConnectionOnce()
    {
        var connector = new FakeRabbitMqBrokerConnector();
        var service = CreateService(connector);
        await service.StartAsync(CancellationToken.None);
        var connection = connector.LastConnection!;

        await service.StopAsync(CancellationToken.None);
        await service.DisposeAsync();

        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(RabbitMqInfrastructureState.Stopped, service.State);
        Assert.False(service.IsReady);
    }

    [Fact]
    public async Task DisposeAsync_IsSafe_WhenNeverStarted()
    {
        var service = CreateService(new FakeRabbitMqBrokerConnector());
        await service.DisposeAsync();
        await service.DisposeAsync();
        Assert.Equal(RabbitMqInfrastructureState.Stopped, service.State);
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        var connector = new FakeRabbitMqBrokerConnector();
        var service = CreateService(connector);
        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, connector.ConnectCount);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Logs_DoNotContainPassword()
    {
        const string secret = "unit-test-rabbitmq-password-not-real";
        var logger = new CollectingLogger<RabbitMqService>();
        var options = RabbitMqOptionsTests.CreateValid();
        options.Username = "nntparticles";
        options.Password = secret;
        options.PoolReconnectBaseDelayMs = 50;

        var service = new RabbitMqService(
            new FakeRabbitMqBrokerConnector(),
            Options.Create(options),
            Options.Create(TestHostFactory.CreateValidOptions()),
            logger);

        await service.StartAsync(CancellationToken.None);
        await service.DisposeAsync();

        Assert.NotEmpty(logger.Messages);
        foreach (var message in logger.Messages)
        {
            Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SanitizedSnapshot_DoesNotIncludePassword()
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.Username = "nntparticles";
        options.Password = "unit-test-rabbitmq-password-not-real";
        var runtime = options.ToRuntimeOptions();

        var snapshot = RabbitMqConnectionFactoryBuilder.BuildSanitizedSnapshot(
            runtime,
            "VectorNNTP.NNTPD:nntpd01.usenet.ninja");

        Assert.True(snapshot.UsesUsernameAuthentication);
        Assert.True(snapshot.HasPassword);
        Assert.DoesNotContain(runtime.Password!, snapshot.ToString(), StringComparison.Ordinal);
        Assert.False(snapshot.AutomaticRecoveryEnabled);
        Assert.False(snapshot.TopologyRecoveryEnabled);
    }

    [Fact]
    public void BuildConnectionFactory_DisablesClientAutomaticRecovery()
    {
        var runtime = RabbitMqOptionsTests.CreateValid().ToRuntimeOptions();
        var factory = RabbitMqConnectionFactoryBuilder.BuildConnectionFactory(
            runtime,
            "VectorNNTP.NNTPD:nntpd01.usenet.ninja");

        Assert.False(factory.AutomaticRecoveryEnabled);
        Assert.False(factory.TopologyRecoveryEnabled);
        Assert.Equal(runtime.Port, factory.Port);
        Assert.Equal(runtime.VirtualHost, factory.VirtualHost);
        Assert.Equal((ushort)runtime.RequestedChannelMax, factory.RequestedChannelMax);
    }

    private static RabbitMqService CreateService(
        FakeRabbitMqBrokerConnector connector,
        int poolReconnectBaseDelayMs = 250)
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = poolReconnectBaseDelayMs;
        return new RabbitMqService(
            connector,
            Options.Create(options),
            Options.Create(TestHostFactory.CreateValidOptions()),
            NullLogger<RabbitMqService>.Instance);
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
