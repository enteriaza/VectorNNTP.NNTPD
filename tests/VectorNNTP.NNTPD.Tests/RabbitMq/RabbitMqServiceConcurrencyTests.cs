using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.RabbitMq;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.RabbitMq;

public sealed class RabbitMqServiceConcurrencyTests
{
    [Fact]
    public async Task SimultaneousLossSignals_StartOneReconnect()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;

        Parallel.For(0, 8, _ => first.SimulateLost());

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await secondConnected.Task.WaitAsync(safety.Token);

        var extra = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = extra;
        var extraConnect = extra.Task.WaitAsync(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<TimeoutException>(() => extraConnect);

        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(2, service.ConnectionGeneration);
        Assert.True(service.IsReady);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task StaleConnectionLost_DoesNotAffectCurrentGeneration()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;
        first.SimulateLost();

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await secondConnected.Task.WaitAsync(safety.Token);
        var current = factory.LastConnection!;

        var extra = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = extra;
        first.SimulateLost();

        var extraConnect = extra.Task.WaitAsync(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<TimeoutException>(() => extraConnect);

        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(2, service.ConnectionGeneration);
        Assert.Same(current, service.GetRequiredConnection());
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, current.DisposeCount);

        await service.DisposeAsync();
        Assert.Equal(1, current.DisposeCount);
    }

    [Fact]
    public async Task StaleConnectionDispose_DoesNotDisposeCurrent()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;
        first.SimulateLost();

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await secondConnected.Task.WaitAsync(safety.Token);
        var current = factory.LastConnection!;

        await first.DisposeAsync();

        Assert.Equal(2, first.DisposeCount);
        Assert.Equal(0, current.DisposeCount);
        Assert.True(service.IsReady);
        Assert.Same(current, service.GetRequiredConnection());

        await service.DisposeAsync();
        Assert.Equal(1, current.DisposeCount);
    }

    [Fact]
    public async Task ShutdownDuringReconnect_DoesNotInstallAReplacement()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;
        var connectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.ConnectStarted = connectStarted;
        factory.BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        first.SimulateLost();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await connectStarted.Task.WaitAsync(safety.Token);

        var stop = service.StopAsync(CancellationToken.None);
        factory.BlockConnect.TrySetCanceled();
        await stop;

        Assert.Equal(1, service.ConnectionCount);
        Assert.Equal(1, service.ConnectionGeneration);
        Assert.False(service.IsReady);
        Assert.Throws<InvalidOperationException>(() => service.GetRequiredConnection());
        foreach (var connection in factory.Connections)
        {
            Assert.Equal(1, connection.DisposeCount);
        }
    }

    [Fact]
    public async Task ConcurrentStopAndLoss_DisposesCurrentOnce()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;

        var stop = service.StopAsync(CancellationToken.None);
        first.SimulateLost();
        await service.DisposeAsync();
        await stop;

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, factory.ConnectCount);
        Assert.False(service.IsReady);
    }

    [Fact]
    public async Task RepeatedLifecycleCalls_AreIdempotent()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        var connection = factory.LastConnection!;

        await Task.WhenAll(
            service.StopAsync(CancellationToken.None),
            service.StopAsync(CancellationToken.None),
            service.DisposeAsync().AsTask(),
            service.DisposeAsync().AsTask());

        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, connection.DisposeCount);
        Assert.False(service.IsReady);
    }

    private static RabbitMqService CreateService(FakeRabbitMqConnectionFactory factory)
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
