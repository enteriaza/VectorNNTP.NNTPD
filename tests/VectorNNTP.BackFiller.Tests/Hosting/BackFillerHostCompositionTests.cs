using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Logging;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Hosting;

public sealed class BackFillerHostCompositionTests
{
    [Fact]
    public void AddBackFillerHosting_registers_rabbitmq_as_the_sole_backfiller_hosted_service()
    {
        using var host = CreateHost();

        Assert.Same(TimeProvider.System, host.Services.GetRequiredService<TimeProvider>());
        var hosted = host.Services.GetServices<IHostedService>()
            .Where(static service => service.GetType().Assembly == typeof(BackFillerServiceCollectionExtensions).Assembly)
            .ToArray();
        var rabbit = Assert.Single(hosted);
        Assert.Same(host.Services.GetRequiredService<IBackFillerRabbitMqService>(), rabbit);
        Assert.Same(host.Services.GetRequiredService<BackFillerRabbitMqService>(), rabbit);
    }

    [Fact]
    public void AddBackFillerHosting_registers_a_single_runtime_options_snapshot()
    {
        using var host = CreateHost();

        var first = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
        var second = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
        Assert.Same(first, second);
        Assert.Equal("backfiller01.usenet.ninja", first.Fqdn);
        Assert.Equal("127.0.0.1", first.GrabberDb.Server);
        Assert.Equal(TimeSpan.FromSeconds(45), first.Shutdown.GracePeriod);
        Assert.Equal(
            first.Shutdown.GracePeriod,
            host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    [Fact]
    public async Task Host_starts_rabbitmq_and_stops_without_a_placeholder_background_service()
    {
        using var host = CreateHost();

        await host.StartAsync();
        try
        {
            Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
            var rabbit = host.Services.GetRequiredService<IBackFillerRabbitMqService>();
            Assert.True(rabbit.IsReady);
            Assert.Equal(1, rabbit.ConnectionGeneration);
            Assert.DoesNotContain(
                host.Services.GetServices<IHostedService>(),
                static service => service.GetType().Name.Contains("BackgroundService", StringComparison.Ordinal)
                                  && service.GetType().Assembly == typeof(BackFillerServiceCollectionExtensions).Assembly);
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.False(host.Services.GetRequiredService<IBackFillerRabbitMqService>().IsReady);
    }

    [Fact]
    public void BackFiller_assembly_does_not_reference_NNTPD()
    {
        var referenced = typeof(BackFillerServiceCollectionExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(static name => name.Name);

        Assert.DoesNotContain("VectorNNTP.NNTPD", referenced);
    }

    private static IHost CreateHost()
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Configuration.AddInMemoryCollection(BackFillerTestOptions.CreateValidConfigurationPairs());
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<IPhysicalMemoryProvider>(new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
        builder.Services.AddSingleton<IBackFillerRabbitMqConnectionFactory>(new FakeBackFillerRabbitMqConnectionFactory());
        builder.ConfigureBackFillerLogging();
        builder.ConfigureBackFillerPlatformHosting();
        builder.AddBackFillerHosting();
        return builder.Build();
    }
}
