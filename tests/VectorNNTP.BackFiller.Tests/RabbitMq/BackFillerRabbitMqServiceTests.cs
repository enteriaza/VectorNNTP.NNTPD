using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;

using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.RabbitMq;

public sealed class RabbitMqServiceTests
{
    [Fact]
    public void ConnectionHandle_does_not_own_the_connection()
    {
        Assert.DoesNotContain(typeof(IDisposable), typeof(RabbitMqConnectionHandle).GetInterfaces());
        Assert.DoesNotContain(typeof(IAsyncDisposable), typeof(RabbitMqConnectionHandle).GetInterfaces());
    }

    [Fact]
    public async Task StartAsync_connects_once_and_is_ready()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var replaced = new List<RabbitMqConnectionReplacedEventArgs>();
        var service = CreateService(factory);
        service.ConnectionReplaced += (_, args) => replaced.Add(args);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, factory.ConnectCount);
        Assert.Equal(1, service.ConnectionCount);
        Assert.Equal(1, service.ConnectionGeneration);
        Assert.True(service.IsReady);
        Assert.NotNull(service.Execution);
        Assert.True(service.TryGetCurrent(out var handle));
        Assert.True(handle.IsCurrent);
        Assert.Equal(1, handle.Generation);
        Assert.Same(factory.LastConnection, handle.Connection);
        var generation = Assert.Single(replaced);
        Assert.Equal(1, generation.ConnectionGeneration);
        Assert.False(generation.IsReplacement);
        Assert.Equal("VectorNNTP.BackFiller:backfiller01.usenet.ninja", factory.LastConnection!.ClientProvidedName);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_fails_when_connect_throws()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory
        {
            ConnectException = new InvalidOperationException("broker down"),
        };
        var service = CreateService(factory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Equal("broker down", ex.Message);
        Assert.Equal(0, service.ConnectionCount);
        Assert.Equal(0, service.ConnectionGeneration);
        Assert.False(service.IsReady);
        Assert.Null(service.Execution);
        Assert.False(service.TryGetCurrent(out _));
    }

    [Fact]
    public async Task StartAsync_fails_when_connection_is_not_usable()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory { ReturnUnusableConnection = true };
        var service = CreateService(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Equal(0, service.ConnectionCount);
        Assert.False(service.IsReady);
        Assert.Equal(1, factory.LastConnection!.DisposeCount);
    }

    [Fact]
    public async Task StartAsync_cancelled_does_not_leave_a_connection()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory
        {
            BlockConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var service = CreateService(factory);
        using var cts = new CancellationTokenSource();

        var start = service.StartAsync(cts.Token);
        cts.Cancel();
        factory.BlockConnect.TrySetCanceled(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(0, service.ConnectionCount);
        Assert.False(service.IsReady);
        Assert.Null(service.Execution);
    }

    [Fact]
    public async Task StartAsync_can_retry_after_failed_connect()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory
        {
            ConnectException = new InvalidOperationException("broker down"),
        };
        var service = CreateService(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        factory.ConnectException = null;

        await service.StartAsync(CancellationToken.None);
        Assert.True(service.IsReady);
        Assert.Equal(1, service.ConnectionGeneration);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_is_idempotent()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, factory.ConnectCount);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Shutdown_after_successful_startup_disposes_the_current_generation()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        var connection = factory.LastConnection!;

        await service.StopAsync(CancellationToken.None);

        Assert.False(service.IsReady);
        Assert.False(service.TryGetCurrent(out _));
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ConnectionLost_replaces_connection_and_increments_generation()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var replaced = new List<RabbitMqConnectionReplacedEventArgs>();
        var service = CreateService(factory);
        service.ConnectionReplaced += (_, args) => replaced.Add(args);

        await service.StartAsync(CancellationToken.None);
        var first = factory.LastConnection!;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;

        first.SimulateLost();

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
        Assert.True(service.TryGetCurrent(out var current));
        Assert.Same(factory.LastConnection, current.Connection);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task Repeated_connection_loss_recovers_indefinitely()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);

        for (var expected = 2; expected <= 4; expected++)
        {
            var previous = factory.LastConnection!;
            var connected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Connected = connected;
            previous.SimulateLost();

            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await connected.Task.WaitAsync(safety.Token);
            Assert.Equal(expected, service.ConnectionGeneration);
            Assert.True(service.IsReady);
            Assert.Equal(1, previous.DisposeCount);
        }

        await service.DisposeAsync();
    }

    [Fact]
    public async Task Stale_generation_cannot_replace_newer_generation()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
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
        Assert.True(service.TryGetCurrent(out var handle));
        Assert.Same(current, handle.Connection);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, current.DisposeCount);

        await service.DisposeAsync();
        Assert.Equal(1, current.DisposeCount);
    }

    [Fact]
    public async Task Stale_generation_cannot_dispose_newer_generation()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
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
        Assert.True(service.TryGetCurrent(out var handle));
        Assert.Same(current, handle.Connection);

        await service.DisposeAsync();
        Assert.Equal(1, current.DisposeCount);
    }

    [Fact]
    public async Task Current_generation_can_be_shut_down_cleanly()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        Assert.True(service.TryGetCurrent(out var handle));
        var connection = factory.LastConnection!;

        await service.DisposeAsync();

        Assert.False(handle.IsCurrent);
        Assert.False(service.IsReady);
        Assert.Equal(1, connection.DisposeCount);
        Assert.False(service.Execution!.IsFaulted);
    }

    [Fact]
    public async Task Cancellation_during_recovery_does_not_install_a_replacement()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
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
        Assert.False(service.TryGetCurrent(out _));
        foreach (var connection in factory.Connections)
        {
            Assert.Equal(1, connection.DisposeCount);
        }
    }

    [Fact]
    public async Task CreateChannel_uses_the_current_generation_and_does_not_dispose_the_connection()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        Assert.True(service.TryGetCurrent(out var handle));

        var channel = await handle.Connection.CreateManualAckChannelAsync(handle.Generation, CancellationToken.None);
        Assert.Equal(1, channel.Generation);
        Assert.True(channel.IsOpen);
        Assert.Single(factory.LastConnection!.Channels);

        await channel.DisposeAsync();
        Assert.Equal(1, factory.LastConnection.Channels[0].DisposeCount);
        Assert.Equal(0, factory.LastConnection.DisposeCount);
        Assert.True(service.IsReady);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task CreateChannel_on_a_stale_handle_fails()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        var service = CreateService(factory);
        await service.StartAsync(CancellationToken.None);
        Assert.True(service.TryGetCurrent(out var stale));
        var first = factory.LastConnection!;
        var secondConnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Connected = secondConnected;
        first.SimulateLost();

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await secondConnected.Task.WaitAsync(safety.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => stale.Connection.CreateManualAckChannelAsync(stale.Generation, CancellationToken.None));
        Assert.Empty(factory.LastConnection!.Channels);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task Logs_do_not_include_configured_password()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory
        {
            ConnectException = new InvalidOperationException("broker down"),
        };
        var logger = new CollectingLogger<RabbitMqService>();
        var service = CreateService(factory, logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));

        Assert.All(
            logger.Messages,
            static message => Assert.DoesNotContain(BackFillerTestOptions.SecretPassword, message, StringComparison.Ordinal));
    }

    [Fact]
    public void Production_rabbitmq_code_does_not_block_synchronously()
    {
        var directory = FindRabbitMqSourceDirectory();
        Assert.True(Directory.Exists(directory), directory);
        foreach (var file in Directory.GetFiles(directory, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BackFiller_assembly_does_not_contain_BackFillerRabbitMqService()
    {
        var assembly = typeof(VectorNNTP.BackFiller.Hosting.BackFillerServiceCollectionExtensions).Assembly;
        Assert.Null(assembly.GetType("VectorNNTP.BackFiller.RabbitMq.BackFillerRabbitMqService"));
        Assert.DoesNotContain(
            assembly.GetTypes(),
            static type => type.Name == "BackFillerRabbitMqService");
    }

    [Fact]
    public void Common_assembly_does_not_contain_backfiller_storage_or_article_work_types()
    {
        var common = typeof(RabbitMqService).Assembly;
        Assert.Null(common.GetType("VectorNNTP.BackFiller.ArticleWork.ArticleWorkConsumerService"));
        Assert.Null(common.GetType("VectorNNTP.Common.Messaging.RabbitMq.BackFillerArticleWorkTopology"));
        Assert.DoesNotContain(
            common.GetManifestResourceNames(),
            static name => name.Contains("backfiller.storage", StringComparison.OrdinalIgnoreCase));

        var topologyNames = common.GetTypes()
            .SelectMany(static type => type.GetFields(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Instance))
            .Where(static field => field.FieldType == typeof(string) && field.IsLiteral)
            .Select(static field => field.GetRawConstantValue() as string)
            .Where(static value => !string.IsNullOrEmpty(value));
        Assert.DoesNotContain(topologyNames, static value => value == "backfiller.storage");
    }

    internal static RabbitMqService CreateService(
        FakeBackFillerRabbitMqConnectionFactory factory,
        ILogger<RabbitMqService>? logger = null)
    {
        var options = BackFillerTestOptions.CreateValidRabbitMq();
        options.PoolReconnectBaseDelayMs = 1;
        options.PoolReconnectMaxDelayMs = 1;
        return new RabbitMqService(
            factory,
            Microsoft.Extensions.Options.Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTP.BackFiller:backfiller01.usenet.ninja"),
            logger ?? NullLogger<RabbitMqService>.Instance);
    }

    internal static BackFillerRuntimeOptions CreateFastRuntime()
    {
        var rabbit = BackFillerTestOptions.CreateValidRabbitMq();
        rabbit.PoolReconnectBaseDelayMs = 1;
        rabbit.PoolReconnectMaxDelayMs = 1;
        return BackFillerRuntimeOptionsFactory.Create(
            BackFillerTestOptions.CreateValid(),
            BackFillerTestOptions.CreateValidNntpDb(),
            BackFillerTestOptions.CreateValidAcme(),
            rabbit);
    }

    private static string FindRabbitMqSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "VectorNNTP.BackFiller", "RabbitMq");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/VectorNNTP.BackFiller/RabbitMq.");
    }
}
