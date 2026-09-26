using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Nntp;

namespace VectorNNTP.BackFiller.Accounts;

/// <summary>
/// MySQL provider-account control plane. Polls GrabberDB, publishes immutable snapshots,
/// and reconciles Phase 4 session pools. Does not own RabbitMQ, retention, or the Cache Listener.
/// </summary>
public sealed class ProviderAccountConfigurationService : IHostedService, IAsyncDisposable
{
    private readonly IProviderAccountSource _source;
    private readonly ProviderConfigurationCatalog _catalog;
    private readonly NntpProviderRegistry _registry;
    private readonly BackFillerRuntimeOptions _runtime;
    private readonly ILogger<ProviderAccountConfigurationService> _logger;
    private readonly CancellationTokenSource _runCts = new();
    private readonly object _gate = new();

    private IReadOnlyList<BackFillerProviderDefinition> _published = [];
    private Task? _pollTask;
    private int _refreshing;
    private int _started;
    private int _disposed;
    private bool _hadSuccessfulSnapshot;
    private bool _lastRefreshFailed;

    /// <summary>Initializes the control-plane service.</summary>
    public ProviderAccountConfigurationService(
        IProviderAccountSource source,
        ProviderConfigurationCatalog catalog,
        NntpProviderRegistry registry,
        BackFillerRuntimeOptions runtime,
        ILogger<ProviderAccountConfigurationService> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _catalog = catalog;
        _registry = registry;
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>Gets the live catalog this service publishes (tests).</summary>
    internal ProviderConfigurationCatalog Catalog => _catalog;

    /// <summary>Gets the last successfully published provider snapshot (tests).</summary>
    internal IReadOnlyList<BackFillerProviderDefinition> PublishedProviders
    {
        get
        {
            lock (_gate)
            {
                return _published;
            }
        }
    }

    /// <summary>Gets whether a refresh is currently running (tests).</summary>
    internal bool RefreshInProgress => Volatile.Read(ref _refreshing) == 1;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        ProviderAccountLogMessages.Starting(_logger, _runtime.ServerId);
        await RefreshAsync(required: true, cancellationToken).ConfigureAwait(false);
        _pollTask = PollAsync(_runCts.Token);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _runCts.CancelAsync().ConfigureAwait(false);
        var poll = _pollTask;
        if (poll is not null)
        {
            try
            {
                await poll.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _runCts.Dispose();
        ProviderAccountLogMessages.Stopped(_logger);
    }

    /// <summary>Runs one refresh. Used by tests. Concurrent calls are skipped.</summary>
    internal Task<bool> RefreshOnceAsync(CancellationToken cancellationToken) =>
        RefreshAsync(required: false, cancellationToken);

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_runtime.Accounts.RefreshInterval, cancellationToken).ConfigureAwait(false);
                _ = await RefreshAsync(required: false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> RefreshAsync(bool required, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            var rows = await _source.QueryAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var mapped = ProviderAccountMapper.Map(rows);
            foreach (var rejected in mapped.Rejected)
            {
                ProviderAccountLogMessages.RowRejected(_logger, rejected.Backbone, rejected.Reason);
            }

            IReadOnlyList<BackFillerProviderDefinition> previous;
            lock (_gate)
            {
                previous = _published;
            }

            if (SnapshotsEqual(previous, mapped.Providers))
            {
                ProviderAccountLogMessages.SnapshotUnchanged(_logger, mapped.Providers.Count);
                if (_lastRefreshFailed)
                {
                    ProviderAccountLogMessages.RefreshRecovered(_logger, mapped.Providers.Count);
                }

                _lastRefreshFailed = false;
                _hadSuccessfulSnapshot = true;
                return true;
            }

            await _registry.ApplySnapshotAsync(mapped.Providers, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _published = mapped.Providers;
            }

            LogDelta(previous, mapped.Providers);
            ProviderAccountLogMessages.SnapshotLoaded(
                _logger,
                _runtime.ServerId,
                mapped.Providers.Count,
                mapped.Rejected.Count);
            if (_lastRefreshFailed)
            {
                ProviderAccountLogMessages.RefreshRecovered(_logger, mapped.Providers.Count);
            }

            _lastRefreshFailed = false;
            _hadSuccessfulSnapshot = true;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (required && !_hadSuccessfulSnapshot)
            {
                throw;
            }

            _lastRefreshFailed = true;
            ProviderAccountLogMessages.RefreshFailed(_logger, ex);
            return false;
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private void LogDelta(
        IReadOnlyList<BackFillerProviderDefinition> previous,
        IReadOnlyList<BackFillerProviderDefinition> current)
    {
        var previousByBackbone = previous.ToDictionary(static item => item.Backbone, StringComparer.OrdinalIgnoreCase);
        var currentByBackbone = current.ToDictionary(static item => item.Backbone, StringComparer.OrdinalIgnoreCase);

        foreach (var added in current)
        {
            if (!previousByBackbone.TryGetValue(added.Backbone, out var existing))
            {
                ProviderAccountLogMessages.ProviderAdded(
                    _logger,
                    added.Backbone,
                    added.Host,
                    added.Port,
                    added.UseTls,
                    added.MinSessions,
                    added.MaxSessions);
                continue;
            }

            if (existing != added)
            {
                ProviderAccountLogMessages.ProviderChanged(
                    _logger,
                    added.Backbone,
                    added.Host,
                    added.Port,
                    added.UseTls,
                    added.MinSessions,
                    added.MaxSessions);
            }
        }

        foreach (var removed in previous)
        {
            if (!currentByBackbone.ContainsKey(removed.Backbone))
            {
                ProviderAccountLogMessages.ProviderRemoved(_logger, removed.Backbone);
            }
        }
    }

    private static bool SnapshotsEqual(
        IReadOnlyList<BackFillerProviderDefinition> left,
        IReadOnlyList<BackFillerProviderDefinition> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var rightByBackbone = right.ToDictionary(static item => item.Backbone, StringComparer.OrdinalIgnoreCase);
        foreach (var item in left)
        {
            if (!rightByBackbone.TryGetValue(item.Backbone, out var match) || match != item)
            {
                return false;
            }
        }

        return true;
    }
}
