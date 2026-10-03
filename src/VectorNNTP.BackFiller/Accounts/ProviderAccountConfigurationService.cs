using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Nntp;

namespace VectorNNTP.BackFiller.Accounts
{
    /// <summary>
    /// MySQL provider-account control plane. Polls NntpDB, publishes immutable snapshots,
    /// and reconciles Phase 4 session pools. Does not own RabbitMQ, retention, or the Cache Listener.
    /// </summary>
    /// <remarks>
    /// Catalogue publication and pool reconciliation both happen inside
    /// <see cref="NntpProviderRegistry.ApplySnapshotAsync"/>. This service does not call
    /// <see cref="ProviderConfigurationCatalog.Publish"/> itself.
    /// A failed refresh does not replace the last applied snapshot. The first required
    /// refresh propagates that failure instead of logging it.
    /// </remarks>
    internal sealed class ProviderAccountConfigurationService : IHostedService, IAsyncDisposable
    {
        /// <summary>Account query. Not disposed by this service.</summary>
        private readonly IProviderAccountSource _source;

        /// <summary>Receives each changed snapshot through <see cref="NntpProviderRegistry.ApplySnapshotAsync"/>.</summary>
        private readonly NntpProviderRegistry _registry;

        /// <summary>Supplies <see cref="BackFillerRuntimeOptions.ServerId"/> and <see cref="BackFillerRuntimeOptions.AccountRefreshInterval"/>.</summary>
        private readonly BackFillerRuntimeOptions _runtime;

        /// <summary>Control-plane logger. Events are written through <see cref="ProviderAccountLogMessages"/>.</summary>
        private readonly ILogger<ProviderAccountConfigurationService> _logger;

        /// <summary>
        /// Poll-loop token source, created with this instance. <see cref="DisposeAsync"/> cancels and disposes it.
        /// The initial refresh does not use this token.
        /// </summary>
        private readonly CancellationTokenSource _runCts = new();

        /// <summary>Guards reads and writes of <see cref="_published"/> only.</summary>
        private readonly Lock _gate = new();

        /// <summary>
        /// The last snapshot passed to <see cref="NntpProviderRegistry.ApplySnapshotAsync"/>.
        /// Empty until the first applied change. Replaced under <see cref="_gate"/>; not mutated in place.
        /// </summary>
        private IReadOnlyList<BackFillerProviderDefinition> _published = [];

        /// <summary>Poll loop started after the first required refresh returns. Read by <see cref="DisposeAsync"/> without further synchronization.</summary>
        private Task? _pollTask;

        /// <summary>Zero when no refresh holds the gate, one while <see cref="RefreshAsync"/> is inside its try. Updated with interlocked and volatile operations.</summary>
        private int _refreshing;

        /// <summary>Zero until the first <see cref="StartAsync"/> passes the start latch. A failed first start still leaves this set.</summary>
        private int _started;

        /// <summary>Zero until the first <see cref="DisposeAsync"/> passes the disposal latch.</summary>
        private int _disposed;

        /// <summary>Set when a refresh maps successfully, including an unchanged snapshot. Written only by the refresh that holds <see cref="_refreshing"/>.</summary>
        private bool _hadSuccessfulSnapshot;

        /// <summary>Set when a refresh failure is retained. Cleared on the next successful map. Written only by the refresh that holds <see cref="_refreshing"/>.</summary>
        private bool _lastRefreshFailed;

        /// <summary>Retains the account source, registry, runtime snapshot, and logger.</summary>
        /// <param name="source">Query used by each refresh.</param>
        /// <param name="registry">Registry that applies a changed snapshot.</param>
        /// <param name="runtime">Server id and poll interval.</param>
        /// <param name="logger">Logger passed to <see cref="ProviderAccountLogMessages"/>.</param>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        /// <remarks>
        /// The live <see cref="ProviderConfigurationCatalog"/> is owned by <see cref="NntpProviderRegistry"/>.
        /// <see cref="NntpProviderRegistry.ApplySnapshotAsync"/> publishes it. This service does not hold that instance.
        /// </remarks>
        internal ProviderAccountConfigurationService(
            IProviderAccountSource source,
            NntpProviderRegistry registry,
            BackFillerRuntimeOptions runtime,
            ILogger<ProviderAccountConfigurationService> logger)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(logger);
            _source = source;
            _registry = registry;
            _runtime = runtime;
            _logger = logger;
        }

        /// <summary>Gets the last successfully published provider snapshot (tests).</summary>
        /// <remarks>The lock covers the reference read. The returned list is not copied.</remarks>
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
        /// <remarks>A volatile read of <see cref="_refreshing"/>. It can change as soon as the refresh leaves its <c>finally</c>.</remarks>
        internal bool RefreshInProgress => Volatile.Read(ref _refreshing) == 1;

        /// <summary>
        /// Accepts the first start, logs it, runs one required refresh, then starts the poll loop.
        /// </summary>
        /// <param name="cancellationToken">Observed only by that required refresh. A later poll uses <see cref="_runCts"/>.</param>
        /// <returns>A task that completes once the poll task has been assigned, or immediately when start already ran.</returns>
        /// <remarks>
        /// The start latch is set before the refresh. If that refresh throws, a later call returns without starting the poll.
        /// This method is not coordinated with <see cref="DisposeAsync"/>. Dispose can cancel and dispose
        /// <see cref="_runCts"/> while the required refresh is still running; the poll is assigned only after that refresh returns.
        /// </remarks>
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

        /// <summary>Stops the control plane by disposing of it.</summary>
        /// <param name="cancellationToken">Not observed.</param>
        /// <returns>The task returned by <see cref="DisposeAsync"/>.</returns>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>Cancels the poll loop, awaits it, and disposes the poll token source.</summary>
        /// <returns>A task that completes after the stopped event is written, or immediately when dispose already ran.</returns>
        /// <remarks>
        /// The disposal latch makes a second call a no-op. <see cref="OperationCanceledException"/> from the poll task is ignored.
        /// Any other exception from that task propagates, and the token source is left undisposed in that case because disposal follows to await.
        /// Dependencies injected through the constructor are not disposed of.
        /// </remarks>
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
        /// <param name="cancellationToken">Forwarded to <see cref="RefreshAsync"/> with <c>required</c> false.</param>
        /// <returns>The result of that refresh. <see langword="false"/> when another refresh holds the latch or the failure is retained.</returns>
        internal Task<bool> RefreshOnceAsync(CancellationToken cancellationToken) =>
            RefreshAsync(required: false, cancellationToken);

        /// <summary>Waits <see cref="BackFillerRuntimeOptions.AccountRefreshInterval"/>, refreshes, and repeats until cancelled.</summary>
        /// <param name="cancellationToken">Delay and refresh token. Cancellation ends the loop.</param>
        /// <returns>A task that completes after cancellation is observed.</returns>
        /// <remarks>The non-required refresh retains query failures. This loop does not log them again.</remarks>
        private async Task PollAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_runtime.AccountRefreshInterval, cancellationToken).ConfigureAwait(false);
                    _ = await RefreshAsync(required: false, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        /// <summary>Queries, maps, and either keeps or applies the snapshot.</summary>
        /// <param name="required">
        /// When <see langword="true"/> and no refresh has succeeded yet, a non-cancellation failure is rethrown
        /// and <see cref="ProviderAccountLogMessages.RefreshFailed"/> is not written.
        /// </param>
        /// <param name="cancellationToken">Observed by the query and again before mapping. Also forwarded to the registry applying.</param>
        /// <returns>
        /// <see langword="false"/> when another refresh holds <see cref="_refreshing"/> or a failure is retained.
        /// <see langword="true"/> when the mapped snapshot was unchanged or was applied.
        /// </returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled. The last-failed flag is not updated.</exception>
        /// <remarks>
        /// One refresh runs at a time. The latch is cleared in <c>finally</c>, including when this method throws.
        /// An unchanged snapshot does not call the registry. Rejected rows are logged before the equality check,
        /// including when a later applying fails. <see cref="OperationCanceledException"/> is rethrown only when
        /// <paramref name="cancellationToken"/> is already cancellation-requested; any other
        /// <see cref="OperationCanceledException"/> is handled as a retained or required failure.
        /// </remarks>
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

        /// <summary>Logs additions, record changes, and removals. Passwords are not written.</summary>
        /// <param name="previous">Snapshot applied before this refresh.</param>
        /// <param name="current">Snapshot just applied.</param>
        /// <remarks>
        /// Backbones are matched ordinal-ignore-case. Inequality uses the definition's record equality,
        /// so a keepalive or password change is logged as a configuration change without those values.
        /// Removals are logged after additions and changes.
        /// </remarks>
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

        /// <summary>Compares two snapshots by backbone, ignoring list order.</summary>
        /// <param name="left">Previous snapshot.</param>
        /// <param name="right">Mapped snapshot.</param>
        /// <returns>
        /// <see langword="true"/> when the counts match and every <paramref name="left"/> entry has an
        /// ordinal-ignore-case backbone match in <paramref name="right"/> that compares equal as a record.
        /// </returns>
        /// <remarks>Backbone keys each side. The mapper rejects duplicate backbones before publication.</remarks>
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
}
