using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Configuration;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.NntpDb;

namespace VectorNNTP.NNTPD.Tests.NntpDb;

public sealed class NntpSharedConfigurationServiceTests
{
    [Fact]
    public async Task Start_publishes_one_shared_configuration_row()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            SharedConfigurationRows =
            [
                new NntpSharedConfigurationCandidate(
                    4096,
                    "news.example",
                    "http://prom.example",
                    "https://acme-v02.api.letsencrypt.org/directory",
                    14,
                    "0123456789abcdef0123456789abcdef",
                    "usenet.ninja"),
            ],
        };
        await using var database = await StartDatabaseAsync(factory);
        var service = new NntpSharedConfigurationService(
            database,
            Options.Create(new NntpdOptions { ServerId = 1, BindPortTls = 0 }),
            NullLogger<NntpSharedConfigurationService>.Instance,
            TimeProvider.System,
            TimeSpan.FromHours(1));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(4096, service.Current.MaxArticleBytes);
        Assert.Equal("news.example", service.Current.SiteName);
        Assert.Equal("http://prom.example", service.Current.PrometheusUrl);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Start_fails_when_the_row_is_invalid_and_does_not_publish()
    {
        var factory = new FakeNntpDbConnectionFactory
        {
            SharedConfigurationRows = [],
        };
        await using var database = await StartDatabaseAsync(factory);
        var service = new NntpSharedConfigurationService(
            database,
            Options.Create(new NntpdOptions { ServerId = 1, BindPortTls = 0 }),
            NullLogger<NntpSharedConfigurationService>.Instance,
            TimeProvider.System,
            TimeSpan.FromHours(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.False(service.HasSnapshot);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Open_before_startup_is_refused()
    {
        var database = new NntpDbService(
            new FakeNntpDbConnectionFactory(),
            Options.Create(new NntpDbOptions { ConnectionString = TestHostFactory.TestNntpDbConnectionString }),
            NullLogger<NntpDbService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.OpenAsync(CancellationToken.None).AsTask());
        await database.DisposeAsync();
    }

    private static async Task<NntpDbService> StartDatabaseAsync(FakeNntpDbConnectionFactory factory)
    {
        var database = new NntpDbService(
            factory,
            Options.Create(new NntpDbOptions { ConnectionString = TestHostFactory.TestNntpDbConnectionString }),
            NullLogger<NntpDbService>.Instance);
        await database.StartAsync(CancellationToken.None);
        return database;
    }
}
