using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.NntpDb;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.NntpDb;

public sealed class NntpDbServiceTests
{
    [Fact]
    public async Task StartAsync_OpensConnection_SelectsOne_AndDisposesLogicalConnection()
    {
        var factory = new FakeNntpDbConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.HasStarted);
        Assert.True(service.IsAccepting);
        Assert.Equal(1, factory.OpenCount);
        var connection = Assert.Single(factory.Connections);
        Assert.Equal(1, connection.SelectOneCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task StartAsync_Fails_WhenMySqlIsUnavailable()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            OpenException = new NntpDbUnavailableException("down"),
        };
        var service = CreateService(factory, startupTimeout: TimeSpan.FromMilliseconds(1));

        var ex = await Assert.ThrowsAsync<NntpDbUnavailableException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("startup", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(service.HasStarted);
        Assert.False(service.IsAccepting);
    }

    [Fact]
    public async Task StartAsync_FailsImmediately_OnAuthenticationFailure()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            OpenException = new NntpDbAuthenticationException("denied"),
        };
        var service = CreateService(factory, startupTimeout: TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<NntpDbAuthenticationException>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.HasStarted);
        Assert.False(service.IsAccepting);
    }

    [Fact]
    public async Task StartAsync_FailsImmediately_WhenSelectOneReturnsWrongValue()
    {
        var factory = new FakeNntpDbConnectionFactory { NextHealthCheckResult = 0 };
        var service = CreateService(factory);

        var ex = await Assert.ThrowsAsync<NntpDbUnavailableException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("SELECT 1", ex.Message, StringComparison.Ordinal);
        Assert.False(service.HasStarted);
        Assert.Equal(1, Assert.Single(factory.Connections).DisposeCount);
    }

    [Fact]
    public async Task StartAsync_FailsImmediately_WhenSelectOneThrows()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            NextHealthCheckException = new NntpDbUnavailableException("MySQL health query SELECT 1 failed."),
        };
        var service = CreateService(factory);

        await Assert.ThrowsAsync<NntpDbUnavailableException>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.HasStarted);
        Assert.Equal(1, Assert.Single(factory.Connections).DisposeCount);
    }

    [Fact]
    public async Task StartAsync_RetriesTransientOpenFailure_ThenSucceeds()
    {
        var factory = new FakeNntpDbConnectionFactory { RemainingOpenFailures = 1 };
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.HasStarted);
        Assert.Equal(0, factory.RemainingOpenFailures);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, Assert.Single(factory.Connections).SelectOneCount);
        Assert.Equal(1, factory.Connections[0].DisposeCount);
    }

    [Fact]
    public async Task StartAsync_CancellationDuringOpen_ResetsState_AndIsRetryable()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            BlockOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            OpenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var service = CreateService(factory);
        using var cts = new CancellationTokenSource();
        var starting = service.StartAsync(cts.Token);
        await factory.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        factory.BlockOpen.TrySetResult();
        factory.BlockOpen = null;
        factory.OpenStarted = null;

        Assert.False(service.HasStarted);
        Assert.False(service.IsAccepting);
        Assert.Empty(factory.Connections);

        await service.StartAsync(CancellationToken.None);
        Assert.True(service.HasStarted);
        Assert.True(service.IsAccepting);
        Assert.Equal(1, Assert.Single(factory.Connections).SelectOneCount);
    }

    [Fact]
    public async Task StartAsync_CancellationDuringSelectOne_DisposesConnection_AndRetriesFullStartup()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            BlockSelectOne = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            SelectOneStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var service = CreateService(factory);
        using var cts = new CancellationTokenSource();
        var starting = service.StartAsync(cts.Token);
        await factory.SelectOneStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        factory.BlockSelectOne.TrySetResult();
        factory.BlockSelectOne = null;
        factory.SelectOneStarted = null;

        var first = Assert.Single(factory.Connections);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, first.SelectOneCount);
        Assert.False(service.HasStarted);
        Assert.False(service.IsAccepting);

        await service.StartAsync(CancellationToken.None);
        Assert.True(service.HasStarted);
        Assert.True(service.IsAccepting);
        Assert.Equal(1, factory.Connections.Sum(static connection => connection.SelectOneCount));
        Assert.All(factory.Connections, static connection => Assert.Equal(1, connection.DisposeCount));
    }

    [Fact]
    public async Task OpenAsync_CreatesLogicalConnection_AndDoesNotRetainIt()
    {
        var factory = new FakeNntpDbConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(1, factory.OpenCount);

        await using (var connection = await service.OpenAsync())
        {
            Assert.Equal(2, factory.OpenCount);
            Assert.Equal(0, ((FakeNntpDbConnection)connection).DisposeCount);
        }

        Assert.Equal(2, factory.OpenCount);
        Assert.All(factory.Connections, static connection => Assert.Equal(1, connection.DisposeCount));
    }

    [Fact]
    public async Task StopAsync_RejectsNewOpens()
    {
        var factory = new FakeNntpDbConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenAsync().AsTask());
        Assert.False(service.IsAccepting);
    }

    [Fact]
    public async Task Lifecycle_StartsServiceBeforeRunning_AndStopsIt()
    {
        var factory = new FakeNntpDbConnectionFactory();
        var service = CreateService(factory);
        var lifecycle = TestHostFactory.CreateLifecycle([service]);

        await lifecycle.StartAsync(CancellationToken.None);
        Assert.Equal(ApplicationState.Running, lifecycle.State);
        Assert.True(service.HasStarted);
        Assert.Equal(1, factory.OpenCount);

        await lifecycle.StopAsync(CancellationToken.None);
        Assert.Equal(ApplicationState.Stopped, lifecycle.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenAsync().AsTask());
    }

    [Fact]
    public void Options_NeverExposeSecretPropertyNamesOnValidatorFailures()
    {
        var options = new NntpDbOptions
        {
            ConnectionString = "Server=db;Password=super-secret;User ID=nntpd;",
            StartupTimeout = TimeSpan.Zero,
        };
        var result = new NntpDbOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.DoesNotContain(result.Failures!, static failure => failure.Contains("super-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Failures!, static failure => failure.Contains("Password=", StringComparison.Ordinal));
    }

    private static NntpDbService CreateService(
        FakeNntpDbConnectionFactory factory,
        TimeSpan? startupTimeout = null,
        TimeProvider? clock = null)
    {
        var options = new NntpDbOptions
        {
            ConnectionString = TestHostFactory.TestNntpDbConnectionString,
            StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(15),
        };
        return new NntpDbService(
            factory,
            Options.Create(options),
            NullLogger<NntpDbService>.Instance,
            clock ?? TimeProvider.System);
    }
}
