using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Owns one <see cref="NntpSessionPool"/> per configured provider. Does not poll MySQL.
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
            if (_pools.TryGetValue(backbone, out pool!))
            {
                return true;
            }

            if (!_catalog.TryGetProvider(backbone, out var provider))
            {
                pool = null!;
                return false;
            }

            pool = new NntpSessionPool(provider, _options, _transport, _logger, _shutdownGrace);
            _pools[provider.Backbone] = pool;
            return true;
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
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        grace.CancelAfter(_shutdownGrace);
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (var pool in _pools.Values)
        {
            await pool.DisposeAsync().ConfigureAwait(false);
        }

        _pools.Clear();
    }
}
