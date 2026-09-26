using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Logging;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Hosting;

public sealed class BackFillerHostCompositionTests
{
    [Fact]
    public void AddBackFillerHosting_registers_rabbitmq_then_article_work_consumers()
    {
        using var host = CreateHost();

        Assert.Same(TimeProvider.System, host.Services.GetRequiredService<TimeProvider>());
        var hosted = host.Services.GetServices<IHostedService>()
            .Where(static service => service.GetType().Assembly == typeof(BackFillerServiceCollectionExtensions).Assembly)
            .ToArray();
        Assert.Equal(4, hosted.Length);
        Assert.Same(host.Services.GetRequiredService<BackFillerRabbitMqService>(), hosted[0]);
        Assert.Same(host.Services.GetRequiredService<NntpProviderRegistry>(), hosted[1]);
        Assert.Same(host.Services.GetRequiredService<ArticleRetentionSweepService>(), hosted[2]);
        Assert.Same(host.Services.GetRequiredService<ArticleWorkConsumerService>(), hosted[3]);
        Assert.Same(
            host.Services.GetRequiredService<ArticleRetentionAuthority>(),
            host.Services.GetRequiredService<IArticleRetentionAuthority>());
        Assert.IsType<ProviderArticleWorkHandler>(host.Services.GetRequiredService<IArticleWorkHandler>());
        Assert.IsType<RecordingArticleWorkResponsePublisher>(
            host.Services.GetRequiredService<IArticleWorkResponsePublisher>());
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
    public async Task Host_starts_rabbitmq_and_article_work_consumers_without_a_placeholder_background_service()
    {
        var factory = new FakeBackFillerRabbitMqConnectionFactory();
        using var host = CreateHost(factory);

        await host.StartAsync();
        try
        {
            Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
            var rabbit = host.Services.GetRequiredService<IBackFillerRabbitMqService>();
            Assert.True(rabbit.IsReady);
            Assert.Equal(1, rabbit.ConnectionGeneration);
            var consumer = host.Services.GetRequiredService<ArticleWorkConsumerService>();
            Assert.Equal(BackFillerRabbitMqTopology.ProviderBackbones.Count, consumer.Sessions.Count);
            Assert.Equal(BackFillerRabbitMqTopology.ProviderBackbones.Count, factory.LastConnection!.Channels.Count);
            Assert.Equal(1, factory.ConnectCount);
            Assert.Contains(
                host.Services.GetServices<IHostedService>(),
                static service => service is ArticleRetentionSweepService);
            Assert.DoesNotContain(
                host.Services.GetServices<IHostedService>(),
                static service => service.GetType().Name == "PlaceholderBackgroundService");
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.False(host.Services.GetRequiredService<IBackFillerRabbitMqService>().IsReady);
        Assert.All(factory.LastConnection!.Channels, static channel => Assert.Equal(1, channel.DisposeCount));
    }

    [Fact]
    public void BackFiller_assembly_does_not_reference_NNTPD()
    {
        var referenced = typeof(BackFillerServiceCollectionExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(static name => name.Name);

        Assert.DoesNotContain("VectorNNTP.NNTPD", referenced);
    }

    private static IHost CreateHost(FakeBackFillerRabbitMqConnectionFactory? factory = null)
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Configuration.AddInMemoryCollection(BackFillerTestOptions.CreateValidConfigurationPairs());
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<IPhysicalMemoryProvider>(new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
        builder.Services.AddSingleton<IBackFillerRabbitMqConnectionFactory>(
            factory ?? new FakeBackFillerRabbitMqConnectionFactory());
        builder.ConfigureBackFillerLogging();
        builder.ConfigureBackFillerPlatformHosting();
        builder.AddBackFillerHosting();
        return builder.Build();
    }
}
