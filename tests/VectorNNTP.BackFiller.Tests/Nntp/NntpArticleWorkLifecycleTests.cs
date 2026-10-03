using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.RabbitMq;
using VectorNNTP.BackFiller.Tests.TestDoubles;

using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.Nntp
{
    public sealed class NntpArticleWorkLifecycleTests
    {
        [Fact]
        public async Task MaxSessions_zero_has_no_pool_no_capacity_and_no_consumer()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            var capacity = new BackboneUsableCapacityState();
            await using var registry = CreateRegistry(catalog, transport, capacity);
            await using var consumer = await StartConsumerAsync(catalog, capacity);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 0)], CancellationToken.None);

            Assert.False(registry.TryGetPool("Giganews", out _));
            Assert.False(capacity.HasUsableCapacityForBackbone("Giganews"));
            Assert.Empty(consumer.Service.Sessions);
            Assert.Empty(transport.ConnectAttempts);
        }

        [Fact]
        public async Task MaxSessions_n_eagerly_connects_without_rabbitmq_work()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(transport, count: 3);
            var capacity = new BackboneUsableCapacityState();
            await using var registry = CreateRegistry(catalog, transport, capacity);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 3)], CancellationToken.None);

            Assert.True(registry.TryGetPool("Giganews", out var pool));
            Assert.Equal(3, pool.ActiveSessionCount);
            Assert.Equal(3, transport.ConnectAttempts.Count);
            Assert.True(capacity.HasUsableCapacityForBackbone("Giganews"));
            Assert.Equal(3, capacity.GetUsableCapacityForBackbone("Giganews"));
        }

        [Fact]
        public async Task Topology_backbone_without_a_database_row_never_gets_a_consumer()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(transport, count: 1);
            var capacity = new BackboneUsableCapacityState();
            await using var registry = CreateRegistry(catalog, transport, capacity);
            await using var consumer = await StartConsumerAsync(catalog, capacity);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 1)], CancellationToken.None);
            await WaitUntilAsync(() => consumer.Service.Sessions.Count == 1);

            Assert.DoesNotContain(consumer.Service.Sessions, static session => session.Backbone == "Abavia");
            Assert.DoesNotContain(
                BackFillerRabbitMqTopology.ProviderBackbones.Where(static name => name != "Giganews"),
                name => consumer.Service.Sessions.Any(session => session.Backbone == name));
            Assert.Equal("Giganews", Assert.Single(consumer.Service.Sessions).Backbone);
        }

        [Fact]
        public async Task Configured_provider_with_zero_active_sessions_has_no_consumer()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory
            {
                ConnectException = new IOException("down"),
            };
            var capacity = new BackboneUsableCapacityState();
            await using var registry = CreateRegistry(catalog, transport, capacity);
            await using var consumer = await StartConsumerAsync(catalog, capacity);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 2)], CancellationToken.None);

            Assert.True(catalog.TryGetProvider("Giganews", out _));
            Assert.False(capacity.HasUsableCapacityForBackbone("Giganews"));
            Assert.Empty(consumer.Service.Sessions);
        }

        [Fact]
        public async Task Active_capacity_starts_consumers_matching_desired_slot_count()
        {
            var catalog = new StaticBackFillerProviderCatalog([CreateProvider("Giganews", maxSessions: 3)]);
            var capacity = new BackboneUsableCapacityState();
            await using var consumer = await StartConsumerAsync(catalog, capacity);
            Assert.Empty(consumer.Service.Sessions);

            capacity.PublishSnapshot(new Dictionary<string, int> { ["Giganews"] = 1 });
            await WaitUntilAsync(() => consumer.Service.Sessions.Count == 3);

            Assert.Equal(new[] { 1, 2, 3 }, consumer.Service.Sessions.Select(static s => s.ConnectionNumber).OrderBy(static n => n));
        }

        [Fact]
        public async Task Loss_of_all_active_sessions_retires_consumers_after_capacity_publish()
        {
            var catalog = new StaticBackFillerProviderCatalog([CreateProvider("Giganews", maxSessions: 2)]);
            var capacity = new BackboneUsableCapacityState();
            capacity.PublishSnapshot(new Dictionary<string, int> { ["Giganews"] = 2 });
            await using var consumer = await StartConsumerAsync(catalog, capacity);
            Assert.Equal(2, consumer.Service.Sessions.Count);

            capacity.PublishSnapshot(new Dictionary<string, int>());
            await WaitUntilAsync(() => consumer.Service.Sessions.Count == 0);
        }

        [Fact]
        public async Task Session_loss_replenishes_toward_max_sessions()
        {
            var factory = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 2);
            await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 1);
            await pool.EnsureDesiredSessionsAsync(CancellationToken.None);
            Assert.Equal(1, pool.ActiveSessionCount);

            NntpSessionPoolTests.EnqueueReadyServers(factory, count: 1);
            await using (var lease = await pool.AcquireAsync(CancellationToken.None))
            {
                lease.Retire();
            }

            await WaitUntilAsync(() => pool.ActiveSessionCount == 1 && factory.ConnectAttempts.Count >= 2);
            Assert.Equal(1, pool.ActiveSessionCount);
        }

        [Fact]
        public async Task Idle_connected_provider_stays_connected_without_article_work()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(transport, count: 2);
            var capacity = new BackboneUsableCapacityState();
            await using var registry = CreateRegistry(catalog, transport, capacity);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 2)], CancellationToken.None);
            Assert.True(registry.TryGetPool("Giganews", out var pool));
            Assert.Equal(2, pool.ActiveSessionCount);
            Assert.Equal(0, pool.ActiveLeaseCount);
            Assert.Equal(2, transport.ConnectAttempts.Count);
        }

        [Fact]
        public async Task Provider_removal_retires_pool_and_consumers()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(transport, count: 1);
            var capacity = new BackboneUsableCapacityState();
            await using var consumer = await StartConsumerAsync(catalog, capacity);
            await using var registry = CreateRegistry(catalog, transport, capacity, consumer.Service);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 1)], CancellationToken.None);
            await WaitUntilAsync(() => consumer.Service.Sessions.Count == 1);

            await registry.ApplySnapshotAsync([], CancellationToken.None);
            await WaitUntilAsync(() => consumer.Service.Sessions.Count == 0 && !registry.TryGetPool("Giganews", out _));
            Assert.False(capacity.HasUsableCapacityForBackbone("Giganews"));
        }

        [Fact]
        public async Task Provider_shrink_retains_consumers_up_to_new_max_and_keeps_the_pool()
        {
            var catalog = new ProviderConfigurationCatalog();
            var transport = new ScriptedNntpTransportFactory();
            NntpSessionPoolTests.EnqueueReadyServers(transport, count: 4);
            var capacity = new BackboneUsableCapacityState();
            await using var consumer = await StartConsumerAsync(catalog, capacity);
            await using var registry = CreateRegistry(catalog, transport, capacity, consumer.Service);

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 4)], CancellationToken.None);
            await WaitUntilAsync(() => consumer.Service.Sessions.Count == 4);
            Assert.True(registry.TryGetPool("Giganews", out var pool));
            var retained = consumer.Service.Sessions
                .Where(static session => session.ConnectionNumber <= 2)
                .OrderBy(static session => session.ConnectionNumber)
                .ToArray();
            Assert.Equal(new[] { 1, 2 }, retained.Select(static session => session.ConnectionNumber));
            Assert.Equal(4, capacity.GetUsableCapacityForBackbone("Giganews"));

            await registry.ApplySnapshotAsync([CreateProvider("Giganews", maxSessions: 2)], CancellationToken.None);

            Assert.True(registry.TryGetPool("Giganews", out var after));
            Assert.Same(pool, after);
            Assert.Equal(2, after.LiveSessionCount);
            Assert.Equal(2, after.ActiveSessionCount);
            Assert.True(capacity.HasUsableCapacityForBackbone("Giganews"));
            Assert.Equal(2, capacity.GetUsableCapacityForBackbone("Giganews"));
            Assert.Equal(2, consumer.Service.Sessions.Count);
            Assert.Equal(
                new[] { 1, 2 },
                consumer.Service.Sessions.Select(static session => session.ConnectionNumber).OrderBy(static n => n));
            Assert.Same(retained[0], consumer.Service.Sessions.Single(static session => session.ConnectionNumber == 1));
            Assert.Same(retained[1], consumer.Service.Sessions.Single(static session => session.ConnectionNumber == 2));
            Assert.All(consumer.Service.Sessions, static session => Assert.Equal(ArticleWorkConsumerState.Running, session.State));
            Assert.Equal(4, transport.ConnectAttempts.Count);
        }

        private static async Task<ConsumerHarness> StartConsumerAsync(
            IBackFillerProviderCatalog catalog,
            IBackboneUsableCapacityProvider capacity)
        {
            var factory = new FakeBackFillerRabbitMqConnectionFactory();
            var connections = RabbitMqServiceTests.CreateService(factory);
            await connections.StartAsync(CancellationToken.None);
            var service = new ArticleWorkConsumerService(
                connections,
                RabbitMqServiceTests.CreateFastRuntime(),
                new DeferredArticleWorkHandler(),
                new RecordingArticleWorkResponsePublisher(),
                NullLogger<ArticleWorkConsumerService>.Instance,
                catalog,
                capacity);
            await service.StartAsync(CancellationToken.None);
            return new ConsumerHarness(service, connections);
        }

        private sealed class ConsumerHarness(
            ArticleWorkConsumerService service,
            RabbitMqService connections) : IAsyncDisposable
        {
            public ArticleWorkConsumerService Service { get; } = service;

            public async ValueTask DisposeAsync()
            {
                await Service.DisposeAsync();
                await connections.DisposeAsync();
            }
        }

        private static NntpProviderRegistry CreateRegistry(
            ProviderConfigurationCatalog catalog,
            ScriptedNntpTransportFactory transport,
            BackboneUsableCapacityState capacity,
            IArticleWorkConsumerReconciliation? consumers = null)
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
                NullLogger<NntpProviderRegistry>.Instance,
                capacity,
                consumers);
        }

        private static BackFillerProviderDefinition CreateProvider(string backbone, int maxSessions) =>
            new(backbone, "news.example.test", 119, false, "nntp-user", "p", 0, maxSessions);

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(10, timeout.Token);
            }
        }
    }
}
