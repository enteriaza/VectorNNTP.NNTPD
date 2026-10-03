using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.Nntp
{
    /// <summary>
    /// Owns one <see cref="NntpSessionPool"/> per configured provider with MaxSessions &gt; 0.
    /// Eagerly establishes MaxSessions NNTP slots and publishes usable capacity.
    /// </summary>
    /// <remarks>
    /// Pools are keyed by backbone with ordinal ignore-case comparison.
    /// <see cref="StartAsync"/> fills pools from the current catalog.
    /// <see cref="ApplySnapshotAsync"/> keeps a pool whose provider record is unchanged, rebinds a max-sessions shrink, and otherwise replaces the pool.
    /// </remarks>
    internal sealed class NntpProviderRegistry : IHostedService, IAsyncDisposable
    {
        /// <summary>Catalog consulted by <see cref="TryGetPool"/> and <see cref="PublishCapacity"/>.</summary>
        private readonly IBackFillerProviderCatalog _catalog;

        /// <summary>Transport passed to every pool this registry creates.</summary>
        private readonly INntpTransportFactory _transport;

        /// <summary>Session options passed to every pool this registry creates.</summary>
        private readonly NntpSessionOptions _options;

        /// <summary>Single drain budget shared by <see cref="StopAsync"/> and <see cref="DisposeAsync"/>.</summary>
        private readonly TimeSpan _shutdownGrace;

        /// <summary>Logger for capacity publication and consumer-reconcile failures.</summary>
        private readonly ILogger<NntpProviderRegistry> _logger;

        /// <summary>Snapshot replaced by <see cref="PublishCapacity"/> and cleared on dispose.</summary>
        private readonly BackboneUsableCapacityState _capacity;

        /// <summary>Test-supplied reconciler. When null, <see cref="Consumers"/> resolves one from <see cref="_services"/>.</summary>
        private readonly IArticleWorkConsumerReconciliation? _consumers;

        /// <summary>Host provider used to resolve consumers after construction. Null when tests pass <see cref="_consumers"/> directly.</summary>
        private readonly IServiceProvider? _services;

        /// <summary>Live pools keyed by backbone, ordinal ignore-case.</summary>
        private readonly ConcurrentDictionary<string, NntpSessionPool> _pools = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Serializes pool-map edits, catalog publication, and the provider list read for capacity.</summary>
        private readonly object _gate = new();

        /// <summary>Zero while the registry accepts work. Set to 1 by the first <see cref="DisposeCoreAsync"/>.</summary>
        private int _disposed;

        /// <summary>
        /// Initializes the registry with <see cref="NntpSessionOptions.Default"/> and <see cref="BackFillerShutdownRuntimeOptions.GracePeriod"/>.
        /// </summary>
        /// <param name="catalog">Current provider snapshot.</param>
        /// <param name="transport">NNTP transport factory.</param>
        /// <param name="runtime">Validated runtime snapshot.</param>
        /// <param name="logger">Registry logger.</param>
        /// <param name="capacity">Usable-capacity publisher.</param>
        /// <param name="services">
        /// Host service provider used to resolve consumers after construction.
        /// Avoids a constructor cycle with <see cref="IArticleWorkConsumerReconciliation"/>.
        /// </param>
        internal NntpProviderRegistry(
            IBackFillerProviderCatalog catalog,
            INntpTransportFactory transport,
            BackFillerRuntimeOptions runtime,
            ILogger<NntpProviderRegistry> logger,
            BackboneUsableCapacityState capacity,
            IServiceProvider? services = null)
            : this(catalog, transport, NntpSessionOptions.Default, runtime.Shutdown.GracePeriod, logger, capacity, consumers: null, services)
        {
        }

        /// <summary>Initializes the registry with explicit session options (tests).</summary>
        /// <param name="catalog">Current provider snapshot.</param>
        /// <param name="transport">NNTP transport factory.</param>
        /// <param name="options">Per-session NNTP options.</param>
        /// <param name="shutdownGrace">Shared drain budget for replaced or stopped pools.</param>
        /// <param name="logger">Registry logger.</param>
        /// <param name="capacity">Usable-capacity publisher. Created when omitted.</param>
        /// <param name="consumers">Optional consumer reconciler for tests.</param>
        /// <param name="services">Optional host service provider for deferred consumer resolve.</param>
        internal NntpProviderRegistry(
            IBackFillerProviderCatalog catalog,
            INntpTransportFactory transport,
            NntpSessionOptions options,
            TimeSpan shutdownGrace,
            ILogger<NntpProviderRegistry> logger,
            BackboneUsableCapacityState? capacity = null,
            IArticleWorkConsumerReconciliation? consumers = null,
            IServiceProvider? services = null)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            _catalog = catalog;
            _transport = transport;
            _options = options;
            _shutdownGrace = shutdownGrace;
            _logger = logger;
            _capacity = capacity ?? new BackboneUsableCapacityState();
            _consumers = consumers;
            _services = services;
        }

        /// <summary>
        /// Gets the reconciler passed at construction, or resolves one from <see cref="_services"/> when that argument was omitted.
        /// </summary>
        /// <remarks>The service lookup is not cached. The result is null when neither source has a reconciler.</remarks>
        private IArticleWorkConsumerReconciliation? Consumers =>
            _consumers ?? _services?.GetService<IArticleWorkConsumerReconciliation>();

        /// <summary>Gets the usable-capacity snapshot published by this registry.</summary>
        internal BackboneUsableCapacityState Capacity => _capacity;

        /// <summary>
        /// Returns the live pool for <paramref name="backbone"/> when it matches the catalog provider, creating that pool on first use.
        /// </summary>
        /// <param name="backbone">Backbone key, compared ordinal-ignore-case.</param>
        /// <param name="pool">The matching pool when this method returns <see langword="true"/>; otherwise null.</param>
        /// <returns>
        /// <see langword="false"/> when the registry is disposed, the catalog has no such provider,
        /// <see cref="BackFillerProviderDefinition.MaxSessions"/> is not positive, or a pool is already stored whose bound snapshot is not equal to the catalog provider.
        /// </returns>
        /// <exception cref="ArgumentException"><paramref name="backbone"/> is null or whitespace.</exception>
        /// <remarks>
        /// A mismatched pool is left in <see cref="_pools"/>. <see cref="ApplySnapshotAsync"/> is the path that replaces it.
        /// </remarks>
        internal bool TryGetPool(string backbone, out NntpSessionPool pool)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    pool = null!;
                    return false;
                }

                if (!_catalog.TryGetProvider(backbone, out var provider) || provider.MaxSessions <= 0)
                {
                    pool = null!;
                    return false;
                }

                if (_pools.TryGetValue(backbone, out pool!))
                {
                    if (pool.Provider == provider)
                    {
                        return true;
                    }

                    pool = null!;
                    return false;
                }

                pool = CreatePool(provider);
                _pools[provider.Backbone] = pool;
                return true;
            }
        }

        /// <summary>
        /// Publishes <paramref name="providers"/> and reconciles pools. Unchanged providers keep their pool.
        /// A MaxSessions-only shrink keeps the pool and retires consumers above the new limit first.
        /// Consumers for removed or replaced providers are retired before NNTP teardown.
        /// </summary>
        /// <param name="providers">
        /// Desired provider set. When <see cref="_catalog"/> is a <see cref="ProviderConfigurationCatalog"/>, it is published under <see cref="_gate"/> before the pool map changes.
        /// Other catalog implementations are not updated.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancels consumer retirement, pool shrink, pool drain, and warming connects.
        /// Cancellation after a replacement pool has been inserted leaves that pool in the map.
        /// </param>
        /// <remarks>
        /// A disposed registry returns without changing pools. After shrinks and removals are drained, new and replacement pools are connected, capacity is published, and a running consumer reconciler is asked to reconcile.
        /// Consumer retirement is skipped when <see cref="Consumers"/> is missing or not running.
        /// </remarks>
        internal async Task ApplySnapshotAsync(
            IReadOnlyList<BackFillerProviderDefinition> providers,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(providers);
            var retiring = new List<NntpSessionPool>();
            var shrinking = new List<NntpSessionPool>();
            var warming = new List<NntpSessionPool>();
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    return;
                }

                if (_catalog is ProviderConfigurationCatalog live)
                {
                    live.Publish(providers);
                }

                var desired = new Dictionary<string, BackFillerProviderDefinition>(StringComparer.OrdinalIgnoreCase);
                foreach (var provider in providers)
                {
                    desired[provider.Backbone] = provider;
                }

                foreach (var pair in _pools.ToArray())
                {
                    if (!desired.TryGetValue(pair.Key, out var next) || next.MaxSessions <= 0)
                    {
                        if (_pools.TryRemove(pair.Key, out var removed))
                        {
                            Unhook(removed);
                            retiring.Add(removed);
                        }

                        continue;
                    }

                    if (pair.Value.Provider == next)
                    {
                        continue;
                    }

                    if (next.IsMaxSessionsShrinkOf(pair.Value.Provider))
                    {
                        pair.Value.BindProvider(next);
                        shrinking.Add(pair.Value);
                        continue;
                    }

                    if (_pools.TryRemove(pair.Key, out var replaced))
                    {
                        Unhook(replaced);
                        retiring.Add(replaced);
                    }

                    if (next.MaxSessions > 0)
                    {
                        var created = CreatePool(next);
                        _pools[next.Backbone] = created;
                        warming.Add(created);
                    }
                }

                foreach (var provider in desired.Values)
                {
                    if (provider.MaxSessions <= 0 || _pools.ContainsKey(provider.Backbone))
                    {
                        continue;
                    }

                    var created = CreatePool(provider);
                    _pools[provider.Backbone] = created;
                    warming.Add(created);
                }
            }

            if (Consumers is { IsRunning: true } retiringConsumers)
            {
                foreach (var pool in shrinking)
                {
                    await retiringConsumers.RetireCapacityAsync(pool.Backbone, pool.Provider.MaxSessions, cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var pool in retiring)
                {
                    await retiringConsumers.RetireCapacityAsync(pool.Backbone, 0, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var pool in shrinking)
            {
                await pool.ShrinkToBoundAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var pool in retiring)
            {
                await pool.DrainAndDisposeAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            foreach (var pool in warming)
            {
                await pool.EnsureDesiredSessionsAsync(cancellationToken).ConfigureAwait(false);
            }

            PublishCapacity();
            if (Consumers is { IsRunning: true } reconcile)
            {
                await reconcile.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Opens a pool for every catalog provider with a positive <see cref="BackFillerProviderDefinition.MaxSessions"/> and connects that pool's desired sessions.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancels session establishment. Pools already created stay registered. Capacity is published only when the loop finishes.
        /// </param>
        /// <remarks>
        /// Does not reconcile article-work consumers.
        /// A catalog provider whose stored pool snapshot does not match is skipped because <see cref="TryGetPool"/> returns false.
        /// </remarks>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var provider in _catalog.Providers)
            {
                if (provider.MaxSessions <= 0)
                {
                    continue;
                }

                if (!TryGetPool(provider.Backbone, out var pool))
                {
                    continue;
                }

                await pool.EnsureDesiredSessionsAsync(cancellationToken).ConfigureAwait(false);
            }

            PublishCapacity();
        }

        /// <summary>Drains and disposes every pool under one shared grace budget.</summary>
        /// <param name="cancellationToken">Linked with <see cref="_shutdownGrace"/>. Either source ends the drain wait.</param>
        /// <remarks>
        /// One shared grace token covers every pool. Pools are not given a fresh
        /// <see cref="BackFillerShutdownRuntimeOptions.GracePeriod"/> budget each.
        /// When the wait ends, <see cref="DisposeCoreAsync"/> still retires remaining sessions.
        /// </remarks>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(_shutdownGrace);
            await DisposeCoreAsync(grace.Token).ConfigureAwait(false);
        }

        /// <summary>Drains and disposes every pool, using only <see cref="_shutdownGrace"/> as the wait limit.</summary>
        /// <remarks>
        /// The host cancellation token is not observed; <see cref="StopAsync"/> is the path that links caller cancellation.
        /// A second call returns immediately from <see cref="DisposeCoreAsync"/>.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            using var grace = new CancellationTokenSource(_shutdownGrace);
            await DisposeCoreAsync(grace.Token).ConfigureAwait(false);
        }

        /// <summary>Creates a pool for <paramref name="provider"/> and subscribes to its active-session changes.</summary>
        /// <param name="provider">Provider definition stored on the new pool.</param>
        /// <returns>The hooked pool. The caller inserts it into <see cref="_pools"/>.</returns>
        private NntpSessionPool CreatePool(BackFillerProviderDefinition provider)
        {
            var pool = new NntpSessionPool(provider, _options, _transport, _logger, _shutdownGrace);
            pool.ActiveSessionCountChanged += OnPoolActiveSessionCountChanged;
            return pool;
        }

        /// <summary>Removes the active-session handler from <paramref name="pool"/> before the pool is drained.</summary>
        /// <param name="pool">Pool that is leaving the registry.</param>
        private void Unhook(NntpSessionPool pool)
        {
            pool.ActiveSessionCountChanged -= OnPoolActiveSessionCountChanged;
        }

        /// <summary>
        /// Publishes capacity and, when consumers are running, starts a reconcile that is not tied to the caller's cancellation token.
        /// </summary>
        /// <remarks>
        /// Returns immediately when the registry is disposed or consumers are not running.
        /// The reconcile task is not observed here; its failures are logged by <see cref="ReconcileConsumersObservedAsync"/>.
        /// </remarks>
        private void OnPoolActiveSessionCountChanged()
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            PublishCapacity();
            if (Consumers is not { IsRunning: true })
            {
                return;
            }

            _ = ReconcileConsumersObservedAsync();
        }

        /// <summary>Runs <see cref="IArticleWorkConsumerReconciliation.ReconcileAsync"/> with <see cref="CancellationToken.None"/>.</summary>
        /// <remarks>
        /// Exceptions are logged through <see cref="NntpLogMessages.SessionReplenishFailed"/> with backbone text <c>consumers</c> and are not rethrown.
        /// </remarks>
        private async Task ReconcileConsumersObservedAsync()
        {
            try
            {
                await Consumers!.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                NntpLogMessages.SessionReplenishFailed(_logger, "consumers", ex.Message);
            }
        }

        /// <summary>
        /// Publishes one usable-session count per catalog provider and logs how many of those counts are positive.
        /// </summary>
        /// <remarks>
        /// Providers with <see cref="BackFillerProviderDefinition.MaxSessions"/> below 1, and providers with no pool, are published as zero.
        /// The count is <see cref="NntpSessionPool.ActiveSessionCount"/>, not the configured maximum.
        /// </remarks>
        private void PublishCapacity()
        {
            var snapshot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<BackFillerProviderDefinition> providers;
            lock (_gate)
            {
                providers = _catalog.Providers;
                foreach (var provider in providers)
                {
                    if (provider.MaxSessions <= 0)
                    {
                        snapshot[provider.Backbone] = 0;
                        continue;
                    }

                    snapshot[provider.Backbone] = _pools.TryGetValue(provider.Backbone, out var pool)
                        ? pool.ActiveSessionCount
                        : 0;
                }
            }

            _capacity.PublishSnapshot(snapshot);
            NntpLogMessages.UsableCapacityPublished(_logger, snapshot.Count(static pair => pair.Value > 0));
        }

        /// <summary>
        /// Marks the registry disposed, drains each pool with <paramref name="cancellationToken"/>, and publishes an empty capacity snapshot.
        /// </summary>
        /// <param name="cancellationToken">Passed to each <see cref="NntpSessionPool.DrainAndDisposeAsync"/>. A cancelled wait still retires that pool's remaining sessions.</param>
        /// <remarks>The first caller clears <see cref="_pools"/>. A concurrent or later call returns without draining again.</remarks>
        private async Task DisposeCoreAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            List<NntpSessionPool> pools;
            lock (_gate)
            {
                pools = [.. _pools.Values];
                _pools.Clear();
            }

            foreach (var pool in pools)
            {
                Unhook(pool);
                await pool.DrainAndDisposeAsync(cancellationToken).ConfigureAwait(false);
            }

            _capacity.PublishSnapshot(new Dictionary<string, int>());
        }
    }
}
