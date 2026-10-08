using System.Collections.Concurrent;

namespace VectorNNTP.BackFiller.Nntp
{
    /// <summary>
    /// Bounded per-provider NNTP session pool. One lease owns one session.
    /// </summary>
    /// <remarks>
    /// Idle sessions are reused by <see cref="AcquireAsync"/>. DATE keepalive runs only while a session sits idle and <see cref="BackFillerProviderDefinition.DateKeepAliveEnabled"/> is set.
    /// A background fill replaces retired sessions up to the bound <see cref="BackFillerProviderDefinition.MaxSessions"/>.
    /// </remarks>
    internal sealed class NntpSessionPool : IAsyncDisposable
    {
        /// <summary>Provider snapshot. Replaced by <see cref="BindProvider"/>; not a frozen constructor copy.</summary>
        private BackFillerProviderDefinition _provider;

        /// <summary>
        /// <see cref="BackFillerProviderDefinition.MaxSessions"/> captured at construction.
        /// Shrink math treats this as the semaphore's original capacity. <see cref="BindProvider"/> does not change it.
        /// </summary>
        private readonly int _leaseCeiling;

        /// <summary>Timeouts and buffers copied into every session.</summary>
        private readonly NntpSessionOptions _options;

        /// <summary>Factory used by <see cref="CreateReadySessionAsync"/>.</summary>
        private readonly INntpTransportFactory _transport;

        /// <summary>
        /// Application-wide establishment concurrency gate shared by every pool.
        /// Held only around <see cref="NntpProviderSession.ConnectAsync"/>.
        /// </summary>
        private readonly ProviderSessionEstablishmentGate _establishment;

        /// <summary>Pool logger. Also passed to each <see cref="NntpProviderSession"/>.</summary>
        private readonly ILogger _logger;

        /// <summary>Clock for DATE keepalive delays. Production uses <see cref="TimeProvider.System"/>.</summary>
        private readonly TimeProvider _time;

        /// <summary>How long <see cref="DisposeAsync"/> and <see cref="AwaitKeepAlivesAsync"/> wait. Defaults to two seconds.</summary>
        private readonly TimeSpan _shutdownGrace;

        /// <summary>
        /// Acquire permits. The initial count and the semaphore maximum are the constructor <see cref="BackFillerProviderDefinition.MaxSessions"/>.
        /// <see cref="ReturnAsync"/> releases a permit only while outstanding leases are below the current max.
        /// </summary>
        private readonly SemaphoreSlim _leases;

        /// <summary>DATE loop for each idle session that has keepalive enabled. Absent while the session is leased.</summary>
        private readonly ConcurrentDictionary<NntpProviderSession, KeepAliveRegistration> _keepAlives = new();

        /// <summary>Sessions waiting for <see cref="AcquireAsync"/>.</summary>
        private readonly ConcurrentQueue<NntpProviderSession> _idle = new();

        /// <summary>Sessions created and not yet retired. The byte value is unused.</summary>
        private readonly ConcurrentDictionary<NntpProviderSession, byte> _live = new();

        /// <summary>Connection numbers currently allocated. Guarded by <see cref="_slotGate"/>.</summary>
        private readonly HashSet<int> _usedSlots = [];

        /// <summary>
        /// Non-recursive lock for <see cref="_usedSlots"/>.
        /// No caller enters it again on the same call stack.
        /// </summary>
        private readonly Lock _slotGate = new();

        /// <summary>Cancelled at the start of dispose. Linked into acquire waits and DATE loops.</summary>
        private readonly CancellationTokenSource _shutdown = new();

        /// <summary>Serializes session creation and <see cref="ShrinkToBoundAsync"/>.</summary>
        private readonly SemaphoreSlim _fillGate = new(1, 1);

        /// <summary>Sessions counted against the current max. Decremented when a create is abandoned or a live session is retired.</summary>
        private int _created;

        /// <summary>Leases issued by <see cref="Issue"/> and not yet finished by <see cref="ReturnAsync"/>.</summary>
        private int _activeLeases;

        /// <summary>Zero until dispose begins. The first dispose wins.</summary>
        private int _disposed;

        /// <summary>Zero until <see cref="StartPeriodicReplenish"/> publishes <see cref="_replenishTask"/>.</summary>
        private int _replenishStarted;

        /// <summary>Periodic deficit loop. Null before the first start.</summary>
        private Task? _replenishTask;

        /// <summary>
        /// Completes when no lease is outstanding. Replaced with a new incomplete source when <see cref="Issue"/> raises the count from zero.
        /// </summary>
        private TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Delay between periodic deficit fills toward <see cref="BackFillerProviderDefinition.MaxSessions"/>.</summary>
        internal static readonly TimeSpan ReplenishInterval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Creates a pool whose lease permits and connection-number ceiling start at <see cref="BackFillerProviderDefinition.MaxSessions"/> on <paramref name="provider"/>.
        /// </summary>
        /// <param name="provider">
        /// Provider identity. <see cref="BackFillerProviderDefinition.MaxSessions"/> must be at least 1.
        /// <see cref="BackFillerProviderDefinition.MinSessions"/> must be from 0 through that max. It is not used to size the pool.
        /// </param>
        /// <param name="options">Timeouts and buffers copied into each session.</param>
        /// <param name="transport">Opens sockets for new sessions.</param>
        /// <param name="logger">Pool and session logger.</param>
        /// <param name="establishment">
        /// Application-wide gate shared by every pool. Held only around
        /// <see cref="NntpProviderSession.ConnectAsync"/>.
        /// </param>
        /// <param name="shutdownGrace">How long <see cref="DisposeAsync"/> waits for leases. Two seconds when null.</param>
        /// <param name="timeProvider">Clock for DATE delays. <see cref="TimeProvider.System"/> when null.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="provider"/>, <paramref name="options"/>, <paramref name="transport"/>,
        /// <paramref name="logger"/>, or <paramref name="establishment"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">The session bounds on <paramref name="provider"/> are outside the constructor checks.</exception>
        internal NntpSessionPool(
            BackFillerProviderDefinition provider,
            NntpSessionOptions options,
            INntpTransportFactory transport,
            ILogger logger,
            ProviderSessionEstablishmentGate establishment,
            TimeSpan? shutdownGrace = null,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(establishment);
            if (provider.MaxSessions < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(provider), "MaxSessions must be at least 1.");
            }

            if (provider.MinSessions < 0 || provider.MinSessions > provider.MaxSessions)
            {
                throw new ArgumentOutOfRangeException(nameof(provider), "MinSessions must be between 0 and MaxSessions.");
            }

            _provider = provider;
            _options = options;
            _transport = transport;
            _establishment = establishment;
            _logger = logger;
            _time = timeProvider ?? TimeProvider.System;
            _shutdownGrace = shutdownGrace ?? TimeSpan.FromSeconds(2);
            _leaseCeiling = provider.MaxSessions;
            _leases = new SemaphoreSlim(provider.MaxSessions, provider.MaxSessions);
            _drained.TrySetResult();
        }

        /// <summary>Gets <see cref="BackFillerProviderDefinition.Backbone"/> from the bound provider snapshot.</summary>
        internal string Backbone => _provider.Backbone;

        /// <summary>Gets the provider snapshot this pool currently serves.</summary>
        /// <remarks>Updated by <see cref="BindProvider"/>.</remarks>
        internal BackFillerProviderDefinition Provider => _provider;

        /// <summary>Gets the number of session objects still in the live set, including sessions that are connecting and not yet retired.</summary>
        internal int LiveSessionCount => _live.Count;

        /// <summary>
        /// Gets the number of ACTIVE sessions: attached sessions that have completed connect
        /// (<see cref="NntpSessionState.Ready"/> or <see cref="NntpSessionState.Busy"/>).
        /// Connecting, failed, and retired sessions are not counted.
        /// </summary>
        internal int ActiveSessionCount
        {
            get
            {
                var count = 0;
                foreach (var session in _live.Keys)
                {
                    var state = session.State;
                    if (state is NntpSessionState.Ready or NntpSessionState.Busy)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Gets the number of outstanding leases.</summary>
        internal int ActiveLeaseCount => Volatile.Read(ref _activeLeases);

        /// <summary>Raised after ACTIVE session count may have changed.</summary>
        /// <remarks>Invoked synchronously on the caller that changed membership or finished a successful connect.</remarks>
        internal event Action? ActiveSessionCountChanged;

        /// <summary>
        /// Connects the desired <see cref="BackFillerProviderDefinition.MaxSessions"/> slots.
        /// Does not require Article Work. Failed attempts do not fail the call.
        /// </summary>
        /// <param name="cancellationToken">Cancels in-flight connects. Cancellation fails this call. A connect failure does not.</param>
        /// <exception cref="ObjectDisposedException">The pool is already disposed.</exception>
        /// <remarks>
        /// Starts the periodic replenish loop once. Missing slots are scheduled concurrently, but
        /// <see cref="ProviderSessionEstablishmentGate"/> bounds how many
        /// <see cref="NntpProviderSession.ConnectAsync"/> calls run at once across all pools.
        /// </remarks>
        internal async Task EnsureDesiredSessionsAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            StartPeriodicReplenish();
            var missing = Math.Max(0, _provider.MaxSessions - _live.Count);
            if (missing == 0)
            {
                NotifyActiveSessionCountChanged();
                return;
            }

            var attempts = new Task[missing];
            for (var i = 0; i < missing; i++)
            {
                attempts[i] = TryCreateIdleSessionAsync(cancellationToken);
            }

            await Task.WhenAll(attempts).ConfigureAwait(false);
            NotifyActiveSessionCountChanged();
        }

        /// <summary>Acquires an exclusive session lease, reusing an idle session or opening one up to the bound max.</summary>
        /// <param name="cancellationToken">Cancels the permit wait and connect. Pool shutdown cancels the same wait.</param>
        /// <returns>A lease that owns one ready session. Disposing the lease returns or retires it.</returns>
        /// <exception cref="ObjectDisposedException">The pool is disposed before the permit is taken or before a session is issued.</exception>
        /// <exception cref="OperationCanceledException">The caller token or <see cref="_shutdown"/> cancels the wait.</exception>
        /// <exception cref="InvalidOperationException">The created-session count is already at <see cref="BackFillerProviderDefinition.MaxSessions"/>.</exception>
        /// <exception cref="NntpProviderConnectException">
        /// The new session did not become <see cref="NntpSessionState.Ready"/>. It is retired without replenish.
        /// </exception>
        /// <remarks>
        /// Each idle candidate dequeued before the fill gate is held, if it is <see cref="NntpSessionState.Busy"/>, is waited on until it leaves that state, then kept only when <see cref="NntpProviderSession.IsReusable"/>.
        /// An idle candidate taken later, while the fill gate is held, is retired as soon as it is not reusable, including when it is still busy.
        /// A failed acquire releases the permit. A successful acquire holds it until <see cref="ReturnAsync"/>.
        /// </remarks>
        internal async Task<NntpSessionLease> AcquireAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            await _leases.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                while (_idle.TryDequeue(out var idle))
                {
                    StopKeepAlive(idle);
                    if (idle.State == NntpSessionState.Busy)
                    {
                        await WaitUntilNotBusyAsync(idle, linked.Token).ConfigureAwait(false);
                    }

                    if (idle.IsReusable)
                    {
                        return Issue(idle);
                    }

                    await RetireSessionAsync(idle, "idle session was not reusable").ConfigureAwait(false);
                }

                await _fillGate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    while (_idle.TryDequeue(out var idle))
                    {
                        StopKeepAlive(idle);
                        if (idle.IsReusable)
                        {
                            return Issue(idle);
                        }

                        await RetireSessionAsync(idle, "idle session was not reusable").ConfigureAwait(false);
                    }

                    if (Volatile.Read(ref _created) >= _provider.MaxSessions)
                    {
                        throw new InvalidOperationException("NNTP session pool exceeded MaxSessions.");
                    }

                    var created = await CreateReadySessionAsync(linked.Token).ConfigureAwait(false);
                    return Issue(created);
                }
                finally
                {
                    _fillGate.Release();
                }
            }
            catch
            {
                _leases.Release();
                throw;
            }
        }

        /// <summary>
        /// Ends one lease. A reusable session returns to the idle queue unless the pool is shutting down or the live count is above the bound max.
        /// </summary>
        /// <param name="session">Session previously issued by this pool.</param>
        /// <param name="retire"><see langword="true"/> disposes <paramref name="session"/> instead of idling it.</param>
        /// <remarks>
        /// The lease count is decremented even when retirement fails. The acquire permit is released only while outstanding leases are below the current max.
        /// A return that finds the live count above that max retires the session and consumes the permit so a shrunk pool does not hand the slot out again.
        /// An idle return starts DATE keepalive when <see cref="BackFillerProviderDefinition.DateKeepAliveEnabled"/> is set.
        /// </remarks>
        internal async Task ReturnAsync(NntpProviderSession session, bool retire)
        {
            try
            {
                if (retire || Volatile.Read(ref _disposed) == 1 || _shutdown.IsCancellationRequested)
                {
                    await RetireSessionAsync(session, retire ? "lease requested retirement" : "pool is shutting down")
                        .ConfigureAwait(false);
                }
                else if (session.IsReusable)
                {
                    if (_live.Count > _provider.MaxSessions)
                    {
                        await RetireSessionAsync(session, "max sessions shrink", replenish: false)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        EnqueueIdle(session);
                    }
                }
                else
                {
                    await RetireSessionAsync(session, "session is no longer reusable").ConfigureAwait(false);
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref _activeLeases) <= 0)
                {
                    _drained.TrySetResult();
                }

                try
                {
                    if (Volatile.Read(ref _activeLeases) < _provider.MaxSessions)
                    {
                        _leases.Release();
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>
        /// Updates the bound provider snapshot. Used when only <see cref="BackFillerProviderDefinition.MaxSessions"/> shrinks.
        /// </summary>
        /// <param name="provider">The new snapshot with the same connection identity.</param>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="provider"/> fails <see cref="BackFillerProviderDefinition.HasSameConnectionIdentity"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><see cref="BackFillerProviderDefinition.MaxSessions"/> is below 1.</exception>
        /// <remarks>Does not retire sessions, change permits, or reconnect. <see cref="ShrinkToBoundAsync"/> applies the new ceiling.</remarks>
        internal void BindProvider(BackFillerProviderDefinition provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!provider.HasSameConnectionIdentity(_provider))
            {
                throw new ArgumentException("Provider connection identity does not match this pool.", nameof(provider));
            }

            if (provider.MaxSessions < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(provider), "MaxSessions must be at least 1.");
            }

            _provider = provider;
        }

        /// <summary>
        /// Retires idle sessions above the bound <see cref="BackFillerProviderDefinition.MaxSessions"/>.
        /// Leased sessions above the bound retire when returned. Does not reconnect retained sessions.
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait for <see cref="_fillGate"/>. Idle retirement after the gate is acquired does not observe this token.</param>
        /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
        /// <remarks>
        /// Available acquire permits are consumed until they do not exceed the new bound minus permits already held.
        /// The semaphore maximum stays at <see cref="_leaseCeiling"/>.
        /// </remarks>
        internal async Task ShrinkToBoundAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            await _fillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var bound = _provider.MaxSessions;
                while (_live.Count > bound && _idle.TryDequeue(out var idle))
                {
                    StopKeepAlive(idle);
                    await RetireSessionAsync(idle, "max sessions shrink", replenish: false).ConfigureAwait(false);
                }

                var permitHolders = _leaseCeiling - _leases.CurrentCount;
                var desiredPermits = Math.Max(0, bound - permitHolders);
                while (_leases.CurrentCount > desiredPermits && _leases.Wait(0, CancellationToken.None))
                {
                }
            }
            finally
            {
                _fillGate.Release();
            }

            NotifyActiveSessionCountChanged();
        }

        /// <summary>
        /// Stops new leases, waits for outstanding leases to return, then retires remaining sessions.
        /// Used when the control plane replaces this pool. Does not force-close a leased session unless
        /// <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="cancellationToken">
        /// Bounds the wait for outstanding leases. When it is cancelled, the wait ends and remaining sessions are still retired.
        /// </param>
        /// <returns>The dispose task. This path does not apply <see cref="_shutdownGrace"/> itself.</returns>
        internal Task DrainAndDisposeAsync(CancellationToken cancellationToken) =>
            DisposeCoreAsync(forceAfterGrace: false, cancellationToken);

        /// <summary>
        /// Stops new leases, waits up to <see cref="_shutdownGrace"/> for outstanding leases, then retires every remaining session.
        /// </summary>
        /// <returns>A task that completes after remaining sessions have been retired.</returns>
        /// <remarks>
        /// The drain wait uses <see cref="CancellationToken.None"/> plus the grace timeout.
        /// A grace timeout does not abandon remaining sessions. A second call returns immediately.
        /// </remarks>
        public ValueTask DisposeAsync() => new(DisposeCoreAsync(forceAfterGrace: true, CancellationToken.None));

        /// <summary>
        /// Cancels shutdown, stops replenish and keepalive, waits for leases, then retires every session still tracked.
        /// </summary>
        /// <param name="forceAfterGrace">
        /// <see langword="true"/> waits on <see cref="_drained"/> for <see cref="_shutdownGrace"/>.
        /// <see langword="false"/> waits on <paramref name="cancellationToken"/> instead.
        /// </param>
        /// <param name="cancellationToken">Drain budget when <paramref name="forceAfterGrace"/> is false. Ignored for the lease wait when it is true.</param>
        /// <remarks>
        /// Timeout and cancellation of the lease wait are swallowed, and retirement still runs.
        /// Keepalive tasks are given <see cref="_shutdownGrace"/> and then abandoned if they are still running.
        /// </remarks>
        private async Task DisposeCoreAsync(bool forceAfterGrace, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _shutdown.CancelAsync().ConfigureAwait(false);
            var replenish = _replenishTask;
            if (replenish is not null)
            {
                try
                {
                    await replenish.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await AwaitKeepAlivesAsync().ConfigureAwait(false);
            try
            {
                if (forceAfterGrace)
                {
                    await _drained.Task.WaitAsync(_shutdownGrace, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await _drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }

            while (_idle.TryDequeue(out var idle))
            {
                await RetireSessionAsync(idle, "pool dispose").ConfigureAwait(false);
            }

            foreach (var live in _live.Keys)
            {
                await RetireSessionAsync(live, "pool dispose").ConfigureAwait(false);
            }

            _leases.Dispose();
            _fillGate.Dispose();
            _shutdown.Dispose();
        }

        /// <summary>Counts a new outstanding lease and returns it. The first lease of a generation replaces <see cref="_drained"/>.</summary>
        /// <param name="session">Ready session the lease will own.</param>
        /// <returns>A lease whose disposal calls <see cref="ReturnAsync"/>.</returns>
        private NntpSessionLease Issue(NntpProviderSession session)
        {
            if (Interlocked.Increment(ref _activeLeases) == 1)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return new NntpSessionLease(this, session);
        }

        /// <summary>Allocates a connection number, connects a session, and returns it only when it is <see cref="NntpSessionState.Ready"/>.</summary>
        /// <param name="cancellationToken">
        /// Cancels the establishment-gate wait and <see cref="NntpProviderSession.ConnectAsync"/>.
        /// </param>
        /// <returns>The ready session. It is in <see cref="_live"/> and is not yet idle.</returns>
        /// <exception cref="InvalidOperationException"><see cref="_created"/> would exceed <see cref="BackFillerProviderDefinition.MaxSessions"/>, or no connection number is free.</exception>
        /// <exception cref="NntpProviderConnectException">Connect returned a failure or the session is not ready. The session is retired without replenish.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> cancels the gate wait or connect.</exception>
        /// <remarks>
        /// The application-wide <see cref="_establishment"/> gate is held only around
        /// <see cref="NntpProviderSession.ConnectAsync"/> (TCP through Ready). Slot allocation and
        /// session construction run before the wait so cancelled waiters do not leave half-open sockets.
        /// </remarks>
        private async Task<NntpProviderSession> CreateReadySessionAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _created) > _provider.MaxSessions)
            {
                Interlocked.Decrement(ref _created);
                throw new InvalidOperationException("NNTP session pool exceeded MaxSessions.");
            }

            int connectionNumber;
            try
            {
                connectionNumber = AllocateConnectionNumber();
            }
            catch
            {
                Interlocked.Decrement(ref _created);
                throw;
            }

            var session = new NntpProviderSession(_provider, _options, _logger, connectionNumber);
            _live[session] = 0;
            try
            {
                NntpLogMessages.SessionEstablishmentWaiting(_logger, _provider.Backbone, connectionNumber);
                await _establishment.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                NntpLogMessages.SessionEstablishmentCancelled(_logger, _provider.Backbone, connectionNumber);
                await RetireSessionAsync(session, "NNTP connect was cancelled", replenish: false)
                    .ConfigureAwait(false);
                throw;
            }

            try
            {
                NntpLogMessages.SessionEstablishmentStarted(_logger, _provider.Backbone, connectionNumber);
                var failure = await session.ConnectAsync(_transport, cancellationToken).ConfigureAwait(false);
                if (failure is null && session.State == NntpSessionState.Ready)
                {
                    NntpLogMessages.SessionEstablishmentCompleted(_logger, _provider.Backbone, connectionNumber);
                    NotifyActiveSessionCountChanged();
                    return session;
                }

                if (failure?.Kind == ArticleRetrievalKind.Cancelled)
                {
                    NntpLogMessages.SessionEstablishmentCancelled(_logger, _provider.Backbone, connectionNumber);
                }
                else
                {
                    NntpLogMessages.SessionEstablishmentFailed(
                        _logger,
                        _provider.Backbone,
                        connectionNumber,
                        failure?.Reason ?? "connect failed");
                }

                await RetireSessionAsync(session, failure?.Reason ?? "connect failed", replenish: false)
                    .ConfigureAwait(false);
                throw new NntpProviderConnectException(
                    failure?.Kind ?? ArticleRetrievalKind.ProviderFailure,
                    failure?.StatusCode,
                    failure?.Reason ?? "NNTP connect failed.");
            }
            finally
            {
                _establishment.Release();
            }
        }

        /// <summary>Queues <paramref name="session"/> for acquire and starts its DATE loop when keepalive is enabled.</summary>
        /// <param name="session">Reusable session that is no longer leased.</param>
        private void EnqueueIdle(NntpProviderSession session)
        {
            _idle.Enqueue(session);
            StartKeepAlive(session);
        }

        /// <summary>Starts a DATE loop for <paramref name="session"/> unless keepalive is disabled, the pool is disposed, or shutdown has started.</summary>
        /// <param name="session">Idle session. A second registration for the same instance cancels the new loop.</param>
        /// <remarks>The loop task is stored on the registration before the dictionary add. A failed add cancels that new source.</remarks>
        private void StartKeepAlive(NntpProviderSession session)
        {
            if (!_provider.DateKeepAliveEnabled || Volatile.Read(ref _disposed) == 1 || _shutdown.IsCancellationRequested)
            {
                return;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var registration = new KeepAliveRegistration(cts)
            {
                Task = RunKeepAliveAsync(session, cts)
            };
            if (!_keepAlives.TryAdd(session, registration))
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>Cancels and detaches the DATE loop for <paramref name="session"/>. Does not wait for the loop to exit.</summary>
        /// <param name="session">Session leaving the idle queue or being retired.</param>
        private void StopKeepAlive(NntpProviderSession session)
        {
            if (!_keepAlives.TryRemove(session, out var registration))
            {
                return;
            }

            try
            {
                registration.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Waits <see cref="BackFillerProviderDefinition.KeepAliveSeconds"/>, then sends DATE without waiting for a busy article command.
        /// </summary>
        /// <param name="session">Idle session this loop owns.</param>
        /// <param name="cts">Loop cancellation, linked to <see cref="_shutdown"/>. Disposed when the loop exits.</param>
        /// <remarks>
        /// A non-reusable session or a failed DATE retires <paramref name="session"/>.
        /// <see cref="NntpProviderSession.SendDateKeepAliveAsync"/> is called with <see cref="_shutdown"/> and without waiting for the busy lock, so an in-use session skips DATE and stays leased.
        /// Cancellation of <paramref name="cts"/> ends the loop without retirement. Any other exception retires the session.
        /// The registration is removed and <paramref name="cts"/> is disposed on the way out.
        /// </remarks>
        private async Task RunKeepAliveAsync(NntpProviderSession session, CancellationTokenSource cts)
        {
            var token = cts.Token;
            var interval = TimeSpan.FromSeconds(_provider.KeepAliveSeconds);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(interval, _time, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (!session.IsReusable)
                    {
                        await RetireSessionAsync(session, "keepalive found a non-reusable session").ConfigureAwait(false);
                        return;
                    }

                    if (!await session.SendDateKeepAliveAsync(_shutdown.Token).ConfigureAwait(false))
                    {
                        await RetireSessionAsync(session, "DATE keepalive failed").ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                await RetireSessionAsync(session, "DATE keepalive failed").ConfigureAwait(false);
            }
            finally
            {
                _ = _keepAlives.TryRemove(session, out _);
                cts.Dispose();
            }
        }

        /// <summary>Waits for current DATE loops up to <see cref="_shutdownGrace"/>, then returns even if they are still running.</summary>
        /// <remarks>Timeout and cancellation are swallowed.</remarks>
        private async Task AwaitKeepAlivesAsync()
        {
            var tasks = _keepAlives.Values.Select(static registration => registration.Task).ToArray();
            if (tasks.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(tasks).WaitAsync(_shutdownGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// Stops keepalive, frees the connection number, disposes <paramref name="session"/>, and optionally asks the pool to replace it.
        /// </summary>
        /// <param name="session">Live session to drop. A session that is already absent is left alone.</param>
        /// <param name="reason">Text passed to <see cref="NntpLogMessages.SessionRetired"/>.</param>
        /// <param name="replenish"><see langword="true"/> schedules <see cref="RequestReplenish"/> after a session was actually removed.</param>
        private async Task RetireSessionAsync(NntpProviderSession session, string reason, bool replenish = true)
        {
            StopKeepAlive(session);
            if (_live.TryRemove(session, out _))
            {
                Interlocked.Decrement(ref _created);
                ReleaseConnectionNumber(session.ConnectionNumber);
                NntpLogMessages.SessionRetired(_logger, _provider.Backbone, reason);
                NotifyActiveSessionCountChanged();
                await session.DisposeAsync().ConfigureAwait(false);
                if (replenish)
                {
                    RequestReplenish();
                }
            }
        }

        /// <summary>Starts <see cref="RunReplenishAsync"/> once for this pool.</summary>
        private void StartPeriodicReplenish()
        {
            if (Interlocked.Exchange(ref _replenishStarted, 1) == 1)
            {
                return;
            }

            _replenishTask = RunReplenishAsync(_shutdown.Token);
        }

        /// <summary>Starts the periodic loop if needed and runs one observed fill unless the pool is disposed or shutting down.</summary>
        /// <remarks>The observed fill is not awaited. Its failures are logged by <see cref="FillDeficitObservedAsync"/>.</remarks>
        private void RequestReplenish()
        {
            if (Volatile.Read(ref _disposed) == 1 || _shutdown.IsCancellationRequested)
            {
                return;
            }

            StartPeriodicReplenish();
            _ = FillDeficitObservedAsync(_shutdown.Token);
        }

        /// <summary>Fills the deficit once every <see cref="ReplenishInterval"/> until <paramref name="cancellationToken"/> is cancelled.</summary>
        /// <param name="cancellationToken">Shutdown token. Cancellation ends the loop and is not rethrown.</param>
        private async Task RunReplenishAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(ReplenishInterval, cancellationToken).ConfigureAwait(false);
                    await FillDeficitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        /// <summary>Runs <see cref="FillDeficitAsync"/> and logs failures other than cancellation of <paramref name="cancellationToken"/>.</summary>
        /// <param name="cancellationToken">Forwarded to the fill. Cancellation is swallowed.</param>
        private async Task FillDeficitObservedAsync(CancellationToken cancellationToken)
        {
            try
            {
                await FillDeficitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                NntpLogMessages.SessionReplenishFailed(_logger, _provider.Backbone, ex.Message);
            }
        }

        /// <summary>Creates idle sessions until the live count reaches the bound max or one create fails.</summary>
        /// <param name="cancellationToken">Cancels the fill-gate wait and session creation. A disposed pool returns without creating.</param>
        private async Task FillDeficitAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            await _fillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (!cancellationToken.IsCancellationRequested
                       && Volatile.Read(ref _disposed) == 0
                       && _live.Count < _provider.MaxSessions)
                {
                    if (!await TryCreateIdleSessionAsync(cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            finally
            {
                _fillGate.Release();
            }
        }

        /// <summary>Connects one session and queues it when it becomes ready.</summary>
        /// <param name="cancellationToken">Forwarded to <see cref="CreateReadySessionAsync"/>.</param>
        /// <returns>
        /// <see langword="true"/> when an idle session was queued.
        /// <see langword="false"/> when connect failed or the pool is already at its max.
        /// </returns>
        /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
        private async Task<bool> TryCreateIdleSessionAsync(CancellationToken cancellationToken)
        {
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
                var session = await CreateReadySessionAsync(cancellationToken).ConfigureAwait(false);
                EnqueueIdle(session);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (NntpProviderConnectException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>Invokes <see cref="ActiveSessionCountChanged"/> synchronously.</summary>
        private void NotifyActiveSessionCountChanged()
        {
            ActiveSessionCountChanged?.Invoke();
        }

        /// <summary>Reserves the lowest unused connection number from 1 through the current max.</summary>
        /// <returns>The reserved one-based slot.</returns>
        /// <exception cref="InvalidOperationException">Every slot in that range is already reserved.</exception>
        private int AllocateConnectionNumber()
        {
            lock (_slotGate)
            {
                var limit = _provider.MaxSessions;
                for (var slot = 1; slot <= limit; slot++)
                {
                    if (_usedSlots.Add(slot))
                    {
                        return slot;
                    }
                }
            }

            throw new InvalidOperationException("NNTP session pool exceeded MaxSessions.");
        }

        /// <summary>Returns <paramref name="connectionNumber"/> to the free set. A number that is not reserved is ignored.</summary>
        /// <param name="connectionNumber">Slot previously returned by <see cref="AllocateConnectionNumber"/>.</param>
        private void ReleaseConnectionNumber(int connectionNumber)
        {
            lock (_slotGate)
            {
                _usedSlots.Remove(connectionNumber);
            }
        }

        /// <summary>Yields until <paramref name="session"/> leaves <see cref="NntpSessionState.Busy"/>.</summary>
        /// <param name="session">Session just taken from the idle queue.</param>
        /// <param name="cancellationToken">Checked on each yield. There is no separate timeout.</param>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
        private static async Task WaitUntilNotBusyAsync(NntpProviderSession session, CancellationToken cancellationToken)
        {
            while (session.State == NntpSessionState.Busy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        /// <summary>Cancellation and task for one idle session's DATE loop.</summary>
        /// <param name="cts">Linked to pool shutdown. Disposed when <see cref="RunKeepAliveAsync"/> exits.</param>
        private sealed class KeepAliveRegistration(CancellationTokenSource cts)
        {
            /// <summary>Cancels the DATE loop. Cancelling does not dispose this source.</summary>
            internal CancellationTokenSource Cts { get; } = cts;

            /// <summary>
            /// Loop task. Starts as <see cref="Task.CompletedTask"/> and is replaced before the registration is published.
            /// </summary>
            internal Task Task { get; init; } = Task.CompletedTask;
        }
    }

    /// <summary>Connect-time failure that preserves retrieval classification.</summary>
    internal sealed class NntpProviderConnectException : InvalidOperationException
    {
        /// <summary>Initializes a connect failure.</summary>
        /// <param name="kind">Retrieval classification copied from the failed connect.</param>
        /// <param name="statusCode">NNTP status when the server produced one; otherwise null.</param>
        /// <param name="reason">Exception message. Diagnostic text from the session.</param>
        internal NntpProviderConnectException(ArticleRetrievalKind kind, int? statusCode, string reason)
            : base(reason)
        {
            Kind = kind;
            StatusCode = statusCode;
        }

        /// <summary>Gets the retrieval classification.</summary>
        internal ArticleRetrievalKind Kind { get; }

        /// <summary>Gets the NNTP status when present.</summary>
        internal int? StatusCode { get; }
    }
}
