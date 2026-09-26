using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Owns one <see cref="NntpSessionPool"/> per configured provider. Pool replacement is driven by the account control plane.
/// </summary>
public sealed class NntpProviderRegistry : IHostedService, IAsyncDisposable
{
    private readonly IBackFillerProviderCatalog _catalog;
    private readonly INntpTransportFactory _transport;
    private readonly NntpSessionOptions _options;
    private readonly TimeSpan _shutdownGrace;
    private readonly ILogger<NntpProviderRegistry> _logger;
    private readonly ConcurrentDictionary<string, NntpSessionPool> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _disposed;

    /// <summary>Initializes the registry.</summary>
    public NntpProviderRegistry(
        IBackFillerProviderCatalog catalog,
        INntpTransportFactory transport,
        BackFillerRuntimeOptions runtime,
        ILogger<NntpProviderRegistry> logger)
        : this(catalog, transport, NntpSessionOptions.Default, runtime.Shutdown.GracePeriod, logger)
    {
    }

    /// <summary>Initializes the registry with explicit session options (tests).</summary>
    public NntpProviderRegistry(
        IBackFillerProviderCatalog catalog,
        INntpTransportFactory transport,
        NntpSessionOptions options,
        TimeSpan shutdownGrace,
        ILogger<NntpProviderRegistry> logger)
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
    }

    /// <summary>Attempts to get the pool for <paramref name="backbone"/>.</summary>
    public bool TryGetPool(string backbone, out NntpSessionPool pool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                pool = null!;
                return false;
            }

            if (!_catalog.TryGetProvider(backbone, out var provider))
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

            pool = new NntpSessionPool(provider, _options, _transport, _logger, _shutdownGrace);
            _pools[provider.Backbone] = pool;
            return true;
        }
    }

    /// <summary>
    /// Publishes <paramref name="providers"/> and reconciles pools. Unchanged providers keep their pool.
    /// Retired pools drain outstanding leases before dispose.
    /// </summary>
    public async Task ApplySnapshotAsync(
        IReadOnlyList<BackFillerProviderDefinition> providers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var retiring = new List<NntpSessionPool>();
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
                if (!desired.TryGetValue(pair.Key, out var next))
                {
                    if (_pools.TryRemove(pair.Key, out var removed))
                    {
                        retiring.Add(removed);
                    }

                    continue;
                }

                if (pair.Value.Provider == next)
                {
                    continue;
                }

                if (_pools.TryRemove(pair.Key, out var replaced))
                {
                    retiring.Add(replaced);
                }

                var created = new NntpSessionPool(next, _options, _transport, _logger, _shutdownGrace);
                _pools[next.Backbone] = created;
                warming.Add(created);
            }

            foreach (var provider in desired.Values)
            {
                if (_pools.ContainsKey(provider.Backbone))
                {
                    continue;
                }

                var created = new NntpSessionPool(provider, _options, _transport, _logger, _shutdownGrace);
                _pools[provider.Backbone] = created;
                warming.Add(created);
            }
        }

        foreach (var pool in retiring)
        {
            await pool.DrainAndDisposeAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var pool in warming)
        {
            if (pool.Provider.MinSessions <= 0)
            {
                continue;
            }

            await pool.WarmupAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _catalog.Providers)
        {
            if (provider.MinSessions <= 0)
            {
                continue;
            }

            if (!TryGetPool(provider.Backbone, out var pool))
            {
                continue;
            }

            await pool.WarmupAsync(cancellationToken).ConfigureAwait(false);
        }
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
            await pool.DrainAndDisposeAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
