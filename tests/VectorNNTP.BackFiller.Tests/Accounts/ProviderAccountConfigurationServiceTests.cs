using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.Nntp;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Accounts;

public sealed class ProviderAccountConfigurationServiceTests
{
    [Fact]
    public async Task Start_loads_the_initial_snapshot_and_maps_rows()
    {
        var source = new FakeProviderAccountSource
        {
            Rows = [ProviderAccountTestRows.Create()],
        };
        await using var harness = CreateHarness(source);

        await harness.Service.StartAsync(CancellationToken.None);

        var provider = Assert.Single(harness.Service.PublishedProviders);
        Assert.Equal("Giganews", provider.Backbone);
        Assert.Equal("news.example.test", provider.Host);
        Assert.Equal(1, source.QueryCount);
        Assert.True(harness.Catalog.TryGetProvider("giganews", out var published));
        Assert.Equal(provider, published);
    }

    [Fact]
    public async Task Start_accepts_a_valid_empty_provider_result()
    {
        await using var harness = CreateHarness(new FakeProviderAccountSource());

        await harness.Service.StartAsync(CancellationToken.None);

        Assert.Empty(harness.Service.PublishedProviders);
        Assert.Empty(harness.Catalog.Providers);
    }

    [Fact]
    public async Task Start_fails_when_the_initial_query_fails()
    {
        var source = new FakeProviderAccountSource
        {
            QueryException = new InvalidOperationException("Provider account query failed against GrabberDB."),
        };
        await using var harness = CreateHarness(source);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.StartAsync(CancellationToken.None));
        Assert.Equal("Provider account query failed against GrabberDB.", ex.Message);
        Assert.DoesNotContain("Password", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Service.PublishedProviders);
    }

    [Fact]
    public async Task Unchanged_refresh_does_not_churn_the_provider_pool()
    {
        var source = new FakeProviderAccountSource
        {
            Rows = [ProviderAccountTestRows.Create()],
        };
        await using var harness = CreateHarness(source);
        NntpSessionPoolTests.EnqueueReadyServers(harness.Transport, count: 2);
        await harness.Service.StartAsync(CancellationToken.None);
        Assert.True(harness.Registry.TryGetPool("Giganews", out var original));
        await using (var lease = await original.AcquireAsync(CancellationToken.None))
        {
            Assert.Equal(NntpSessionState.Ready, lease.Session.State);
        }

        var connects = harness.Transport.ConnectAttempts.Count;
        Assert.True(await harness.Service.RefreshOnceAsync(CancellationToken.None));

        Assert.True(harness.Registry.TryGetPool("Giganews", out var again));
        Assert.Same(original, again);
        Assert.Equal(connects, harness.Transport.ConnectAttempts.Count);
        Assert.Contains(
            harness.Logger.Messages,
            static message => message.Contains("snapshot unchanged", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("other.example.test", 563, "y", "nntp-user", ProviderAccountTestRows.SecretPassword, 4)]
    [InlineData("news.example.test", 119, "y", "nntp-user", ProviderAccountTestRows.SecretPassword, 4)]
    [InlineData("news.example.test", 563, "n", "nntp-user", ProviderAccountTestRows.SecretPassword, 4)]
    [InlineData("news.example.test", 563, "y", "other-user", ProviderAccountTestRows.SecretPassword, 4)]
    [InlineData("news.example.test", 563, "y", "nntp-user", "replacement-secret", 4)]
    [InlineData("news.example.test", 563, "y", "nntp-user", ProviderAccountTestRows.SecretPassword, 8)]
    public async Task Meaningful_row_change_replaces_the_provider_definition(
        string hostname,
        int port,
        string useSsl,
        string username,
        string password,
        int maxConnections)
    {
        var source = new FakeProviderAccountSource
        {
            Rows = [ProviderAccountTestRows.Create()],
        };
        await using var harness = CreateHarness(source);
        await harness.Service.StartAsync(CancellationToken.None);
        var previous = Assert.Single(harness.Service.PublishedProviders);

        source.Rows =
        [
            ProviderAccountTestRows.Create(
                hostname: hostname,
                port: port,
                useSsl: useSsl,
                username: username,
                password: password,
                maxConnections: maxConnections),
        ];
        Assert.True(await harness.Service.RefreshOnceAsync(CancellationToken.None));

        var next = Assert.Single(harness.Service.PublishedProviders);
        Assert.NotEqual(previous, next);
        Assert.Equal(hostname, next.Host);
        Assert.Equal(port, next.Port);
        Assert.Equal(useSsl == "y", next.UseTls);
        Assert.Equal(username, next.Username);
        Assert.Equal(password, next.Password);
        Assert.Equal(maxConnections, next.MaxSessions);
        Assert.All(
            harness.Logger.Messages,
            message =>
            {
                Assert.DoesNotContain(ProviderAccountTestRows.SecretPassword, message, StringComparison.Ordinal);
                Assert.DoesNotContain("replacement-secret", message, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task Provider_addition_removal_and_reappearance_update_the_catalog()
    {
        var source = new FakeProviderAccountSource
        {
            Rows = [ProviderAccountTestRows.Create()],
        };
        await using var harness = CreateHarness(source);
        await harness.Service.StartAsync(CancellationToken.None);

        source.Rows =
        [
            ProviderAccountTestRows.Create(),
            ProviderAccountTestRows.Create(backbone: "Eweka", hostname: "eweka.example.test"),
        ];
        Assert.True(await harness.Service.RefreshOnceAsync(CancellationToken.None));
        Assert.Equal(2, harness.Service.PublishedProviders.Count);
        Assert.True(harness.Catalog.TryGetProvider("Eweka", out _));

        source.Rows = [ProviderAccountTestRows.Create(backbone: "Eweka", hostname: "eweka.example.test")];
        Assert.True(await harness.Service.RefreshOnceAsync(CancellationToken.None));
        Assert.False(harness.Catalog.TryGetProvider("Giganews", out _));
        Assert.True(harness.Catalog.TryGetProvider("Eweka", out _));

        source.Rows =
        [
            ProviderAccountTestRows.Create(),
            ProviderAccountTestRows.Create(backbone: "Eweka", hostname: "eweka.example.test"),
        ];
        Assert.True(await harness.Service.RefreshOnceAsync(CancellationToken.None));
        Assert.True(harness.Catalog.TryGetProvider("Giganews", out var restored));
        Assert.Equal("news.example.test", restored.Host);
    }

    [Fact]
    public async Task Refresh_failure_retains_last_known_good_and_recovers()
    {
        var source = new FakeProviderAccountSource
        {
            Rows = [ProviderAccountTestRows.Create()],
        };
        await using var harness = CreateHarness(source);
        await harness.Service.StartAsync(CancellationToken.None);
        var knownGood = Assert.Single(harness.Service.PublishedProviders);

        source.QueryException = new InvalidOperationException("Provider account query failed against GrabberDB.");
        Assert.False(await harness.Service.RefreshOnceAsync(CancellationToken.None));
        Assert.Same(knownGood, Assert.Single(harness.Service.PublishedProviders));
        Assert.True(harness.Catalog.TryGetProvider("Giganews", out var retained));
        Assert.Equal(knownGood, retained);
        Assert.Contains(
            harness.Logger.Messages,
            static message => message.Contains("retaining last known-good snapshot", StringComparison.Ordinal));

        source.QueryException = null;
        source.Rows = [ProviderAccountTestRows.Create(hostname: "recovered.example.test")];
        Assert.True(await harness.Service.RefreshOnceAsync(CancellationToken.None));
        Assert.Equal("recovered.example.test", Assert.Single(harness.Service.PublishedProviders).Host);
        Assert.Contains(
            harness.Logger.Messages,
            static message => message.Contains("refresh recovered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Invalid_and_unknown_rows_are_rejected_without_publishing()
    {
        var source = new FakeProviderAccountSource
        {
            Rows =
            [
                ProviderAccountTestRows.Create(backbone: "NotABackbone"),
                ProviderAccountTestRows.Create(hostname: " "),
                ProviderAccountTestRows.Create(backbone: "Eweka", hostname: "eweka.example.test"),
            ],
        };
        await using var harness = CreateHarness(source);
        await harness.Service.StartAsync(CancellationToken.None);

        Assert.Equal("Eweka", Assert.Single(harness.Service.PublishedProviders).Backbone);
        Assert.Contains(
            harness.Logger.Messages,
            static message => message.Contains("Rejected provider account row", StringComparison.Ordinal)
                              && message.Contains("NotABackbone", StringComparison.Ordinal));
        Assert.All(
            harness.Logger.Messages,
            static message => Assert.DoesNotContain(
                ProviderAccountTestRows.SecretPassword,
                message,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_during_query_is_observed()
    {
        var source = new FakeProviderAccountSource
        {
            Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var harness = CreateHarness(source);
        using var cts = new CancellationTokenSource();
        var refresh = harness.Service.RefreshOnceAsync(cts.Token);
        await WaitUntilAsync(() => source.QueryCount == 1);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Empty(harness.Service.PublishedProviders);
    }

    [Fact]
    public async Task Cancellation_during_refresh_is_observed()
    {
        var source = new FakeProviderAccountSource
        {
            Rows = [ProviderAccountTestRows.Create()],
        };
        await using var harness = CreateHarness(source);
        NntpSessionPoolTests.EnqueueReadyServers(harness.Transport, count: 1);
        await harness.Service.StartAsync(CancellationToken.None);
        Assert.True(harness.Registry.TryGetPool("Giganews", out var pool));
        var lease = await pool.AcquireAsync(CancellationToken.None);

        source.Rows = [ProviderAccountTestRows.Create(hostname: "replaced.example.test")];
        using var cts = new CancellationTokenSource();
        var refresh = harness.Service.RefreshOnceAsync(cts.Token);
        await WaitUntilAsync(() =>
            harness.Catalog.TryGetProvider("Giganews", out var published)
            && published.Host == "replaced.example.test");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Shutdown_cancels_the_owned_poll_loop()
    {
        var source = new FakeProviderAccountSource
        {
            Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var harness = CreateHarness(
            source,
            refreshInterval: TimeSpan.FromHours(1));
        try
        {
            using var startCts = new CancellationTokenSource();
            var starting = harness.Service.StartAsync(startCts.Token);
            await WaitUntilAsync(() => source.QueryCount == 1);
            await startCts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        }
        finally
        {
            source.Block.TrySetResult();
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Shutdown_while_polling_completes_without_a_second_refresh()
    {
        var source = new FakeProviderAccountSource();
        await using var harness = CreateHarness(source, refreshInterval: TimeSpan.FromHours(1));
        await harness.Service.StartAsync(CancellationToken.None);
        Assert.Equal(1, source.QueryCount);

        await harness.Service.StopAsync(CancellationToken.None);
        Assert.Equal(1, source.QueryCount);
    }

    [Fact]
    public async Task Concurrent_refresh_is_skipped()
    {
        var source = new FakeProviderAccountSource
        {
            Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var harness = CreateHarness(source);
        var first = harness.Service.RefreshOnceAsync(CancellationToken.None);
        await WaitUntilAsync(() => source.QueryCount == 1 && harness.Service.RefreshInProgress);

        Assert.False(await harness.Service.RefreshOnceAsync(CancellationToken.None));
        source.Block.TrySetResult();
        Assert.True(await first);
        Assert.Equal(1, source.QueryCount);
    }

    [Fact]
    public async Task Poll_does_not_start_a_second_refresh_while_one_is_running()
    {
        var source = new FakeProviderAccountSource
        {
            Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockAfterQueryCount = 2,
        };
        await using var harness = CreateHarness(source, refreshInterval: TimeSpan.FromMilliseconds(20));
        await harness.Service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => source.QueryCount >= 2);

        Assert.False(await harness.Service.RefreshOnceAsync(CancellationToken.None));
        source.Block.TrySetResult();
        await WaitUntilAsync(() => !harness.Service.RefreshInProgress);
    }

    [Fact]
    public void Article_work_request_path_does_not_take_a_mysql_dependency()
    {
        var types = new[]
        {
            typeof(VectorNNTP.BackFiller.ArticleWork.ProviderArticleWorkHandler),
            typeof(NntpArticleRetriever),
            typeof(VectorNNTP.BackFiller.ArticleWork.ArticleWorkDeliveryPipeline),
            typeof(VectorNNTP.BackFiller.ArticleWork.ArticleWorkConsumerService),
        };

        foreach (var type in types)
        {
            foreach (var ctor in type.GetConstructors())
            {
                Assert.DoesNotContain(
                    ctor.GetParameters(),
                    static parameter => parameter.ParameterType == typeof(IProviderAccountSource)
                                        || parameter.ParameterType.FullName?.Contains("MySql", StringComparison.Ordinal) == true);
            }
        }
    }

    private static ServiceHarness CreateHarness(
        FakeProviderAccountSource source,
        TimeSpan? refreshInterval = null)
    {
        var options = BackFillerTestOptions.CreateValid();
        options.Accounts.RefreshIntervalSeconds = 3600;
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            options,
            BackFillerTestOptions.CreateValidConnectionStrings());
        if (refreshInterval is { } interval)
        {
            runtime = runtime with
            {
                Accounts = new BackFillerAccountsRuntimeOptions(interval, runtime.Accounts.CommandTimeout),
            };
        }

        var catalog = new ProviderConfigurationCatalog();
        var transport = new ScriptedNntpTransportFactory();
        var logger = new CollectingLogger<ProviderAccountConfigurationService>();
        var registry = new NntpProviderRegistry(
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
        var service = new ProviderAccountConfigurationService(
            source,
            catalog,
            registry,
            runtime,
            logger);
        return new ServiceHarness(service, catalog, registry, transport, logger);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class ServiceHarness : IAsyncDisposable
    {
        public ServiceHarness(
            ProviderAccountConfigurationService service,
            ProviderConfigurationCatalog catalog,
            NntpProviderRegistry registry,
            ScriptedNntpTransportFactory transport,
            CollectingLogger<ProviderAccountConfigurationService> logger)
        {
            Service = service;
            Catalog = catalog;
            Registry = registry;
            Transport = transport;
            Logger = logger;
        }

        public ProviderAccountConfigurationService Service { get; }

        public ProviderConfigurationCatalog Catalog { get; }

        public NntpProviderRegistry Registry { get; }

        public ScriptedNntpTransportFactory Transport { get; }

        public CollectingLogger<ProviderAccountConfigurationService> Logger { get; }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync().ConfigureAwait(false);
            await Registry.DisposeAsync().ConfigureAwait(false);
        }
    }
}
