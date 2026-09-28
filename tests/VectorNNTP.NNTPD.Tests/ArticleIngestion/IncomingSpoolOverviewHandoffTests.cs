using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Diagnostics;
using VectorNNTP.NNTPD.Hosting;
using VectorNNTP.NNTPD.Logging;
using VectorNNTP.NNTPD.Newsgroups;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Transit;
using VectorNNTP.NNTPD.RabbitMq;
using VectorNNTP.NNTPD.RabbitMq.ArticleWork;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.RabbitMq;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.ArticleIngestion;

/// <summary>Ingestion-worker OverviewDB handoff success, requeue, and topology contracts.</summary>
[Collection(SerilogCollection.Name)]
public sealed class IncomingSpoolOverviewHandoffTests
{
    [Fact]
    public async Task Topology_DeclaresOverviewDbClassicQueue_WithoutExchangeOrBinding()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateRabbitMqService(factory);
        var topology = new RabbitMqTopologyService(rabbit, NullLogger<RabbitMqTopologyService>.Instance);

        await rabbit.StartAsync(CancellationToken.None);
        await topology.StartAsync(CancellationToken.None);

        var connection = factory.LastConnection ?? throw new InvalidOperationException("Expected a connected fake.");
        var overview = Assert.Single(
            connection.QueueDeclarations,
            static queue => queue.Name == OverviewDbTopology.QueueName);
        Assert.Equal("overviewdb.queue", overview.Name);
        Assert.True(overview.Durable);
        Assert.False(overview.Exclusive);
        Assert.False(overview.AutoDelete);
        var arguments = overview.Arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        Assert.False(arguments.ContainsKey("x-queue-type"));
        Assert.False(arguments.ContainsKey(RabbitMqArticleRetrievalEndpoints.QueueTypeArgumentName));
        Assert.False(arguments.ContainsKey("x-message-ttl"));
        Assert.False(arguments.ContainsKey("x-expires"));
        Assert.Empty(arguments);
        Assert.DoesNotContain(connection.ExchangeDeclarations, static e => e.Name == OverviewDbTopology.QueueName);
        Assert.DoesNotContain(connection.BindingDeclarations, static b => b.Queue == OverviewDbTopology.QueueName);
    }

    [Fact]
    public async Task PublishSuccess_CompletesWorkerAndPersistsArticle()
    {
        var overview = new RecordingOverviewDbHandoffPublisher();
        var captured = new List<InboundArticle>();
        var persister = new CapturingPersister(captured);
        var inbound = CreateArticle();
        await RunWorkerAsync(inbound, persister, overview);

        Assert.Equal(1, overview.AttemptCount);
        var payload = Assert.Single(overview.Payloads);
        var decoded = OverviewArticleV1Codec.Decode(payload);
        Assert.Equal(inbound.MessageId, decoded.MessageId);
        Assert.Equal(["alt.test", "rec.test"], decoded.Newsgroups);
        Assert.Equal((uint)inbound.Record.ArtSize, decoded.Bytes);
        Assert.Equal((uint)inbound.Record.ArtLines, decoded.Lines);
        Assert.Same(inbound, Assert.Single(captured));
    }

    [Fact]
    public async Task Worker_RecordsOneItemOnPipelineMetrics()
    {
        var overview = new RecordingOverviewDbHandoffPublisher();
        var captured = new List<InboundArticle>();
        var persister = new CapturingPersister(captured);
        var pipeline = new IngestionPipelineMetrics();
        var inbound = CreateArticle();
        await RunWorkerAsync(inbound, persister, overview, pipeline);

        var snapshot = pipeline.CaptureInterval();
        Assert.Equal(1, snapshot.WorkerItems);
        Assert.Equal(1, snapshot.ToPublishStart.Count);
        Assert.Equal(1, snapshot.Encode.Count);
        Assert.Equal(1, snapshot.News.Count);
        Assert.Equal(1, snapshot.Persist.Count);
        Assert.True(snapshot.BusyTicks > 0);
    }

    [Fact]
    public async Task PublishFailure_RequeuesUntilConfirm_ThenCompletes()
    {
        var overview = new RecordingOverviewDbHandoffPublisher { RemainingFailures = 1 };
        var captured = new List<InboundArticle>();
        var persister = new CapturingPersister(captured);
        var inbound = CreateArticle();
        await RunWorkerAsync(inbound, persister, overview);

        Assert.Equal(2, overview.AttemptCount);
        Assert.Single(overview.Payloads);
        Assert.Same(inbound, Assert.Single(captured));
    }

    [Fact]
    public async Task UnroutableReturn_RequeuesSameInboundArticle_UntilConfirm()
    {
        var overview = new RecordingOverviewDbHandoffPublisher
        {
            RemainingFailures = 1,
            TransientPublishException = new InvalidOperationException(
                "RabbitMQ returned the OverviewDB handoff as unroutable."),
        };
        var captured = new List<InboundArticle>();
        var persister = new CapturingPersister(captured);
        var inbound = CreateArticle();
        await RunWorkerAsync(inbound, persister, overview);

        Assert.Equal(2, overview.AttemptCount);
        Assert.Single(overview.Payloads);
        Assert.Same(inbound, Assert.Single(captured));
    }

    [Fact]
    public async Task PublishFailure_DoesNotPersist_AndRequeuesUntilStop()
    {
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overview = new RecordingOverviewDbHandoffPublisher
        {
            PublishException = new InvalidOperationException("RabbitMQ negatively acknowledged the OverviewDB handoff."),
            BlockOnFailure = block,
        };
        var captured = new List<InboundArticle>();
        var persister = new CapturingPersister(captured);
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = CreateWriter(queue, persister, overview);
        await writer.StartAsync(CancellationToken.None);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(CreateArticle(), CancellationToken.None));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await overview.FirstAttempt.WaitAsync(cts.Token);

        Assert.Equal(1, overview.AttemptCount);
        Assert.Empty(overview.Payloads);
        Assert.Empty(captured);

        queue.Complete();
        block.TrySetResult();
        await writer.StopAsync(CancellationToken.None);
        Assert.Empty(captured);
        Assert.Empty(overview.Payloads);
    }

    [Fact]
    public async Task PersistFailure_AfterRabbitMqConfirm_DoesNotRequeueOrRepublish()
    {
        var overview = new RecordingOverviewDbHandoffPublisher();
        var diagnostics = new SignalingFeedDiagnostics();
        var persister = new ThrowingPersister();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = CreateWriter(queue, persister, overview, diagnostics);
        await writer.StartAsync(CancellationToken.None);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(CreateArticle(), CancellationToken.None));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var end = await diagnostics.Ended.Task.WaitAsync(cts.Token);

        Assert.False(end.Persisted);
        Assert.Equal(1, overview.AttemptCount);
        Assert.Single(overview.Payloads);
        Assert.Equal(0, queue.Count);

        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
        Assert.Equal(1, overview.AttemptCount);
        Assert.Single(overview.Payloads);
    }

    [Fact]
    public void Worker_HasNoOverviewDbRpcOrDatabaseDependency()
    {
        var ctor = typeof(IncomingSpoolWriterService).GetConstructors().Single();
        var types = ctor.GetParameters().Select(static p => p.ParameterType).ToArray();
        Assert.Contains(typeof(IOverviewDbHandoffPublisher), types);
        Assert.DoesNotContain(types, static t => t == typeof(IRabbitMqRpcChannel));
        Assert.DoesNotContain(types, static t => t.Name.Contains("NntpDb", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, static t => t.Name.Contains("MySql", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, static t => t.Name.Contains("Http", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(types, static t => t.Name.Contains("Grpc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Host_RegistersOverviewDbHandoffPublisher_NotAnRpcClient()
    {
        var builder = Host.CreateApplicationBuilder();
        TestHostFactory.ConfigureNntpdTestHost(builder);
        builder.ConfigureNntpdLogging(static lc => lc.MinimumLevel.Fatal());
        builder.Services.AddNntpdHosting(includePlaceholderService: false);

        using var host = builder.Build();
        var publisher = host.Services.GetRequiredService<IOverviewDbHandoffPublisher>();
        Assert.IsType<OverviewDbHandoffPublisher>(publisher);
        Assert.Same(IngestionPipelineMetrics.Shared, host.Services.GetRequiredService<IngestionPipelineMetrics>());
        Assert.IsNotType<ArticleWorkRpcService>(publisher);
        var services = host.Services.GetServices<IApplicationService>().Select(static s => s.GetType()).ToArray();
        Assert.DoesNotContain(services, static t => t.Name.Contains("OverviewDbClient", StringComparison.Ordinal));
        Assert.DoesNotContain(services, static t => t.Name.Contains("OverviewDbRpc", StringComparison.Ordinal));
    }

    private static async Task RunWorkerAsync(
        InboundArticle inbound,
        CapturingPersister persister,
        IOverviewDbHandoffPublisher overview,
        IngestionPipelineMetrics? pipelineMetrics = null)
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = CreateWriter(queue, persister, overview, pipelineMetrics: pipelineMetrics);
        await writer.StartAsync(CancellationToken.None);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(inbound, CancellationToken.None));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await persister.Completed.Task.WaitAsync(cts.Token);
        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
    }

    private static IncomingSpoolWriterService CreateWriter(
        IArticleIngestionQueue queue,
        IIncomingArticlePersister persister,
        IOverviewDbHandoffPublisher overview,
        IFeedDiagnostics? feedDiagnostics = null,
        IngestionPipelineMetrics? pipelineMetrics = null) =>
        new(
            queue,
            persister,
            Options.Create(new NntpdOptions
            {
                ArticleIngestion = new ArticleIngestionOptions(),
                Transit = new TransitOptions { WantTrash = true, LogTrash = true },
            }),
            NullLogger<IncomingSpoolWriterService>.Instance,
            feedDiagnostics: feedDiagnostics,
            catalogue: new StaticNewsgroupCatalogue(
                NewsgroupSnapshot.Create(
                    [new NewsgroupDefinition("alt.test", string.Empty, 2, 1, NewsgroupPostingStatus.Allowed),
                     new NewsgroupDefinition("rec.test", string.Empty, 2, 1, NewsgroupPostingStatus.Allowed)])),
            overviewHandoff: overview,
            pipelineMetrics: pipelineMetrics);

    private static InboundArticle CreateArticle() =>
        CanonicalArticleText.CreateQueued(
            "<overview@example.test>",
            InboundArticleProducer.TakeThis,
            body: "overview-body\r\n",
            newsgroups: "alt.test,rec.test",
            subject: "overview-subject",
            from: "poster@example.test",
            references: "<prev@example.test>");

    private static RabbitMqService CreateRabbitMqService(FakeRabbitMqConnectionFactory factory)
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

    private sealed class CapturingPersister(List<InboundArticle> captured) : IIncomingArticlePersister
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PersistAsync(InboundArticle article, CancellationToken cancellationToken)
        {
            captured.Add(article);
            Completed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>Persister that fails after OverviewDB confirm to prove the worker does not requeue.</summary>
    private sealed class ThrowingPersister : IIncomingArticlePersister
    {
        /// <inheritdoc />
        public Task PersistAsync(InboundArticle article, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Incoming spool persist failed after OverviewDB confirm.");
    }

    /// <summary>Signals <see cref="IncomingSpoolWriterService"/> spool-work completion for tests.</summary>
    private sealed class SignalingFeedDiagnostics : IFeedDiagnostics
    {
        /// <summary>Completes when the worker finishes one spool-work attempt.</summary>
        public TaskCompletionSource<(int Bytes, bool Persisted)> Ended { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public bool IsEnabled => false;

        /// <inheritdoc />
        public void OnRejected(string peerName, string remote)
        {
        }

        /// <inheritdoc />
        public FeedSessionProbe? OnAccepted(NntpSession session) => null;

        /// <inheritdoc />
        public void OnReleased(FeedSessionProbe? probe)
        {
        }

        /// <inheritdoc />
        public void RecordTcpBytes(int bytes)
        {
        }

        /// <inheritdoc />
        public void BeginSpoolWork()
        {
        }

        /// <inheritdoc />
        public void EndSpoolWork(int bytes, bool persisted) => Ended.TrySetResult((bytes, persisted));

        /// <inheritdoc />
        public FeedDiagnosticsSnapshot CaptureSnapshot(
            IArticleIngestionQueue queue,
            TransitConfigurationStore store) =>
            FeedDiagnosticsSnapshot.Empty;
    }
}
