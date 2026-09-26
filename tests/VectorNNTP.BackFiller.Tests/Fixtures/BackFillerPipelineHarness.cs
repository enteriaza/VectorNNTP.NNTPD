using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Nntp;
using VectorNNTP.BackFiller.Tests.RabbitMq;
using VectorNNTP.BackFiller.Tests.Retention;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Fixtures;

/// <summary>
/// Composes the real production Article Work path with fakes only at external boundaries.
/// </summary>
internal sealed class BackFillerPipelineHarness : IAsyncDisposable
{
    private bool _accountsStarted;

    private BackFillerPipelineHarness(
        FakeProviderAccountSource accounts,
        ProviderConfigurationCatalog catalog,
        ProviderAccountConfigurationService accountService,
        NntpProviderRegistry registry,
        ScriptedNntpTransportFactory nntp,
        ArticleRetentionAuthority retention,
        ManualTimeProvider time,
        ProviderArticleWorkHandler handler,
        FakeBackFillerRabbitMqConnectionFactory rabbitFactory,
        BackFillerRabbitMqService connections,
        ArticleWorkResponsePublisher publisher,
        ArticleWorkDeliveryPipeline pipeline)
    {
        Accounts = accounts;
        Catalog = catalog;
        AccountService = accountService;
        Registry = registry;
        Nntp = nntp;
        Retention = retention;
        Time = time;
        Handler = handler;
        RabbitFactory = rabbitFactory;
        Connections = connections;
        Publisher = publisher;
        Pipeline = pipeline;
    }

    public FakeProviderAccountSource Accounts { get; }

    public ProviderConfigurationCatalog Catalog { get; }

    public ProviderAccountConfigurationService AccountService { get; }

    public NntpProviderRegistry Registry { get; }

    public ScriptedNntpTransportFactory Nntp { get; }

    public ArticleRetentionAuthority Retention { get; }

    public ManualTimeProvider Time { get; }

    public ProviderArticleWorkHandler Handler { get; }

    public FakeBackFillerRabbitMqConnectionFactory RabbitFactory { get; }

    public BackFillerRabbitMqService Connections { get; }

    public ArticleWorkResponsePublisher Publisher { get; }

    public ArticleWorkDeliveryPipeline Pipeline { get; }

    public FakeBackFillerRabbitMqPublishChannel PublishChannel =>
        Assert.IsType<FakeBackFillerRabbitMqPublishChannel>(Publisher.Channel);

    public static async Task<BackFillerPipelineHarness> StartAsync(
        FakePublishConfirmBehavior confirm = FakePublishConfirmBehavior.Confirm,
        TimeSpan? retentionTtl = null)
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        var retention = ArticleRetentionAuthorityTests.Create(
            time,
            maxBytes: 1024 * 1024,
            ttl: retentionTtl ?? TimeSpan.FromSeconds(60));
        var accounts = new FakeProviderAccountSource();
        var catalog = new ProviderConfigurationCatalog();
        var nntp = new ScriptedNntpTransportFactory();
        var options = BackFillerTestOptions.CreateValid();
        options.Accounts.RefreshIntervalSeconds = 3600;
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            options,
            BackFillerTestOptions.CreateValidConnectionStrings()) with
        {
            RabbitMq = BackFillerRabbitMqServiceTests.CreateFastRuntime().RabbitMq,
        };

        var registry = new NntpProviderRegistry(
            catalog,
            nntp,
            NntpSessionOptions.Default with
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                CommandTimeout = TimeSpan.FromSeconds(2),
                ReceiveTimeout = TimeSpan.FromSeconds(2),
            },
            TimeSpan.FromSeconds(2),
            NullLogger<NntpProviderRegistry>.Instance);
        var accountService = new ProviderAccountConfigurationService(
            accounts,
            catalog,
            registry,
            runtime,
            NullLogger<ProviderAccountConfigurationService>.Instance);
        var handler = new ProviderArticleWorkHandler(
            new NntpArticleRetriever(registry, NullLogger<NntpArticleRetriever>.Instance),
            retention);

        var rabbitFactory = new FakeBackFillerRabbitMqConnectionFactory
        {
            DefaultPublishConfirmBehavior = confirm,
        };
        var connections = BackFillerRabbitMqServiceTests.CreateService(rabbitFactory);
        await connections.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var publisher = new ArticleWorkResponsePublisher(
            connections,
            runtime,
            NullLogger<ArticleWorkResponsePublisher>.Instance);
        await publisher.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, 1024);

        var harness = new BackFillerPipelineHarness(
            accounts,
            catalog,
            accountService,
            registry,
            nntp,
            retention,
            time,
            handler,
            rabbitFactory,
            connections,
            publisher,
            pipeline);
        await harness.LoadProviderAsync().ConfigureAwait(false);
        return harness;
    }

    public async Task LoadProviderAsync(
        string backbone = "Giganews",
        string hostname = "news.example.test",
        int port = 563,
        string useSsl = "y",
        string username = "nntp-user",
        string password = ProviderAccountTestRows.SecretPassword,
        int maxConnections = 4)
    {
        Accounts.Rows =
        [
            ProviderAccountTestRows.Create(
                backbone: backbone,
                hostname: hostname,
                port: port,
                useSsl: useSsl,
                username: username,
                password: password,
                maxConnections: maxConnections),
        ];
        if (!_accountsStarted)
        {
            await AccountService.StartAsync(CancellationToken.None).ConfigureAwait(false);
            _accountsStarted = true;
            return;
        }

        Assert.True(await AccountService.RefreshOnceAsync(CancellationToken.None).ConfigureAwait(false));
    }

    public ScriptedNntpServer EnqueueArticle(byte[] payload, TaskCompletionSource? blockArticle = null)
    {
        var server = CreateArticleServer(payload, blockArticle);
        Nntp.Enqueue(server);
        return server;
    }

    public static ScriptedNntpServer CreateArticleServer(byte[] payload, TaskCompletionSource? blockArticle = null)
    {
        var server = new ScriptedNntpServer
        {
            BlockArticle = blockArticle,
            ArticleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var wire = "220 follows\r\n" + Encoding.ASCII.GetString(payload) + "\r\n.\r\n";
        server.Respond(command =>
            command.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase)
                ? "281 authentication accepted\r\n"
                : wire);
        return server;
    }

    public Task<ArticleWorkOutcome> ProcessAsync(
        BackFillerRabbitMqConsumedDelivery delivery,
        FakeBackFillerRabbitMqChannel channel,
        Func<bool>? channelStillCurrent = null,
        CancellationToken cancellationToken = default) =>
        Pipeline.ProcessAsync(
            delivery,
            "Giganews",
            channel,
            channelStillCurrent ?? (static () => true),
            cancellationToken);

    public Task<ArticleWorkOutcome> ProcessCanonicalAsync(
        FakeBackFillerRabbitMqChannel channel,
        ulong deliveryTag = 7,
        long generation = 1,
        Func<bool>? channelStillCurrent = null,
        CancellationToken cancellationToken = default) =>
        ProcessAsync(
            ArticleWorkTestDeliveries.Canonical(deliveryTag, generation),
            channel,
            channelStillCurrent,
            cancellationToken);

    public static CacheListenerSession CreateListenerSession(
        ScriptedCacheListenerTransport transport,
        CacheListenerRetentionHandler handler,
        BackFillerListenerRuntimeOptions? listener = null) =>
        new(
            transport,
            handler,
            listener ?? new BackFillerListenerRuntimeOptions(
                65536,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                1024 * 1024,
                8));

    public async ValueTask DisposeAsync()
    {
        await Publisher.DisposeAsync().ConfigureAwait(false);
        await Connections.DisposeAsync().ConfigureAwait(false);
        await AccountService.DisposeAsync().ConfigureAwait(false);
        await Registry.DisposeAsync().ConfigureAwait(false);
        await Retention.DisposeAsync().ConfigureAwait(false);
    }
}
