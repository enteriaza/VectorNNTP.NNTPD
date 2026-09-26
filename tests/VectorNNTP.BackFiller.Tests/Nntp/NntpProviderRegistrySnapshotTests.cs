using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Nntp;

public sealed class NntpProviderRegistrySnapshotTests
{
    [Fact]
    public async Task Active_leased_session_survives_configuration_replacement()
    {
        var catalog = new ProviderConfigurationCatalog();
        var transport = new ScriptedNntpTransportFactory();
        NntpSessionPoolTests.EnqueueReadyServers(transport, count: 2);
        await using var registry = CreateRegistry(catalog, transport);
        var original = CreateDefinition();
        await registry.ApplySnapshotAsync([original], CancellationToken.None);
        Assert.True(registry.TryGetPool("Giganews", out var oldPool));
        var lease = await oldPool.AcquireAsync(CancellationToken.None);

        var replacement = original with { Host = "replaced.example.test" };
        var applying = registry.ApplySnapshotAsync([replacement], CancellationToken.None);
        Assert.Equal(NntpSessionState.Ready, lease.Session.State);
        Assert.True(registry.TryGetPool("Giganews", out var newPool));
        Assert.NotSame(oldPool, newPool);
        Assert.Equal("replaced.example.test", newPool.Provider.Host);

        await lease.DisposeAsync();
        await applying;
        Assert.True(registry.TryGetPool("Giganews", out var after));
        Assert.Same(newPool, after);
    }

    [Fact]
    public async Task Removed_provider_stops_accepting_new_leases()
    {
        var catalog = new ProviderConfigurationCatalog();
        var transport = new ScriptedNntpTransportFactory();
        NntpSessionPoolTests.EnqueueReadyServers(transport, count: 1);
        await using var registry = CreateRegistry(catalog, transport);
        await registry.ApplySnapshotAsync([CreateDefinition()], CancellationToken.None);
        Assert.True(registry.TryGetPool("Giganews", out var pool));
        var lease = await pool.AcquireAsync(CancellationToken.None);

        var removing = registry.ApplySnapshotAsync([], CancellationToken.None);
        Assert.False(registry.TryGetPool("Giganews", out _));
        Assert.Equal(NntpSessionState.Ready, lease.Session.State);
        await lease.DisposeAsync();
        await removing;
        Assert.False(registry.TryGetPool("Giganews", out _));
    }

    [Fact]
    public async Task Unrelated_provider_pool_is_retained_when_another_provider_changes()
    {
        var catalog = new ProviderConfigurationCatalog();
        var transport = new ScriptedNntpTransportFactory();
        NntpSessionPoolTests.EnqueueReadyServers(transport, count: 3);
        await using var registry = CreateRegistry(catalog, transport);
        var giganews = CreateDefinition();
        var eweka = CreateDefinition("Eweka", "eweka.example.test");
        await registry.ApplySnapshotAsync([giganews, eweka], CancellationToken.None);
        Assert.True(registry.TryGetPool("Giganews", out var giganewsPool));
        await using (var lease = await giganewsPool.AcquireAsync(CancellationToken.None))
        {
            Assert.Equal(NntpSessionState.Ready, lease.Session.State);
        }

        var giganewsConnects = transport.ConnectAttempts.Count(static attempt => attempt.Backbone == "Giganews");
        await registry.ApplySnapshotAsync(
            [giganews, eweka with { Host = "eweka-new.example.test" }],
            CancellationToken.None);

        Assert.True(registry.TryGetPool("Giganews", out var stillGiganews));
        Assert.Same(giganewsPool, stillGiganews);
        Assert.True(registry.TryGetPool("Eweka", out var newEweka));
        Assert.Equal("eweka-new.example.test", newEweka.Provider.Host);
        await using var reused = await stillGiganews.AcquireAsync(CancellationToken.None);
        Assert.Equal(
            giganewsConnects,
            transport.ConnectAttempts.Count(static attempt => attempt.Backbone == "Giganews"));
        await reused.DisposeAsync();
    }

    [Fact]
    public async Task Min_and_max_session_changes_replace_only_that_pool()
    {
        var catalog = new ProviderConfigurationCatalog();
        var transport = new ScriptedNntpTransportFactory();
        await using var registry = CreateRegistry(catalog, transport);
        var giganews = CreateDefinition();
        var eweka = CreateDefinition("Eweka", "eweka.example.test");
        await registry.ApplySnapshotAsync([giganews, eweka], CancellationToken.None);
        Assert.True(registry.TryGetPool("Giganews", out var giganewsPool));
        Assert.True(registry.TryGetPool("Eweka", out var ewekaPool));

        NntpSessionPoolTests.EnqueueReadyServers(transport, count: 1);
        await registry.ApplySnapshotAsync(
            [giganews with { MinSessions = 1, MaxSessions = 8 }, eweka],
            CancellationToken.None);

        Assert.True(registry.TryGetPool("Giganews", out var replaced));
        Assert.True(registry.TryGetPool("Eweka", out var retained));
        Assert.NotSame(giganewsPool, replaced);
        Assert.Same(ewekaPool, retained);
        Assert.Equal(1, replaced.Provider.MinSessions);
        Assert.Equal(8, replaced.Provider.MaxSessions);
    }

    [Fact]
    public async Task Stop_uses_one_shared_grace_token_across_pools()
    {
        var catalog = new ProviderConfigurationCatalog();
        var transport = new ScriptedNntpTransportFactory();
        NntpSessionPoolTests.EnqueueReadyServers(transport, count: 2);
        await using var registry = new NntpProviderRegistry(
            catalog,
            transport,
            NntpSessionOptions.Default with
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                CommandTimeout = TimeSpan.FromSeconds(2),
                ReceiveTimeout = TimeSpan.FromSeconds(2),
            },
            TimeSpan.FromSeconds(30),
            NullLogger<NntpProviderRegistry>.Instance);
        await registry.ApplySnapshotAsync(
            [CreateDefinition(), CreateDefinition("Eweka", "eweka.example.test")],
            CancellationToken.None);
        Assert.True(registry.TryGetPool("Giganews", out var giganews));
        Assert.True(registry.TryGetPool("Eweka", out var eweka));
        var first = await giganews.AcquireAsync(CancellationToken.None);
        var second = await eweka.AcquireAsync(CancellationToken.None);

        using var expired = new CancellationTokenSource();
        await expired.CancelAsync();
        await registry.StopAsync(expired.Token).WaitAsync(TimeSpan.FromSeconds(2));

        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.False(registry.TryGetPool("Giganews", out _));
        Assert.False(registry.TryGetPool("Eweka", out _));

        var connects = transport.ConnectAttempts.Count;
        await registry.ApplySnapshotAsync([CreateDefinition()], CancellationToken.None);
        Assert.False(registry.TryGetPool("Giganews", out _));
        Assert.Equal(connects, transport.ConnectAttempts.Count);
    }

    private static NntpProviderRegistry CreateRegistry(
        ProviderConfigurationCatalog catalog,
        ScriptedNntpTransportFactory transport)
    {
        return new NntpProviderRegistry(
            catalog,
            transport,
            NntpSessionOptions.Default with
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                CommandTimeout = TimeSpan.FromSeconds(2),
                ReceiveTimeout = TimeSpan.FromSeconds(2),
            },
            TimeSpan.FromSeconds(2),
            NullLogger<NntpProviderRegistry>.Instance);
    }

    private static BackFillerProviderDefinition CreateDefinition(
        string backbone = "Giganews",
        string host = "news.example.test") =>
        new(backbone, host, 563, true, "nntp-user", "p", 0, 4);
}
