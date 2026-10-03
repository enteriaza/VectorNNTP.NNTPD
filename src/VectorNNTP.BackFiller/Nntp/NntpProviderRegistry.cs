using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Owns one <see cref="NntpSessionPool"/> per configured provider with MaxSessions &gt; 0.
/// Eagerly establishes MaxSessions NNTP slots and publishes usable capacity.
/// </summary>
internal sealed class NntpProviderRegistry : IHostedService, IAsyncDisposable
{
    private readonly IBackFillerProviderCatalog _catalog;
    private readonly INntpTransportFactory _transport;
    private readonly NntpSessionOptions _options;
    private readonly TimeSpan _shutdownGrace;
    private readonly ILogger<NntpProviderRegistry> _logger;
    private readonly BackboneUsableCapacityState _capacity;
    private readonly IArticleWorkConsumerReconciliation? _consumers;
    private readonly IServiceProvider? _services;
    private readonly ConcurrentDictionary<string, NntpSessionPool> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _disposed;

    /// <summary>Initializes the registry.</summary>
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

    private IArticleWorkConsumerReconciliation? Consumers =>
        _consumers ?? _services?.GetService<IArticleWorkConsumerReconciliation>();

    /// <summary>Gets the usable-capacity snapshot published by this registry.</summary>
    internal BackboneUsableCapacityState Capacity => _capacity;

    /// <summary>Attempts to get the pool for <paramref name="backbone"/>.</summary>
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    /// <remarks>
    /// One shared grace token covers every pool. Pools are not given a fresh
    /// <see cref="BackFillerShutdownRuntimeOptions.GracePeriod"/> budget each.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        grace.CancelAfter(_shutdownGrace);
        await DisposeCoreAsync(grace.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        using var grace = new CancellationTokenSource(_shutdownGrace);
        await DisposeCoreAsync(grace.Token).ConfigureAwait(false);
    }

    private NntpSessionPool CreatePool(BackFillerProviderDefinition provider)
    {
        var pool = new NntpSessionPool(provider, _options, _transport, _logger, _shutdownGrace);
        pool.ActiveSessionCountChanged += OnPoolActiveSessionCountChanged;
        return pool;
    }

    private void Unhook(NntpSessionPool pool)
    {
        pool.ActiveSessionCountChanged -= OnPoolActiveSessionCountChanged;
    }

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
