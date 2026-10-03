using System.Collections.Concurrent;

namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Bounded per-provider NNTP session pool. One lease owns one session.
/// </summary>
internal sealed class NntpSessionPool : IAsyncDisposable
{
    private BackFillerProviderDefinition _provider;
    private readonly int _leaseCeiling;
    private readonly NntpSessionOptions _options;
    private readonly INntpTransportFactory _transport;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _shutdownGrace;
    private readonly SemaphoreSlim _leases;
    private readonly ConcurrentDictionary<NntpProviderSession, KeepAliveRegistration> _keepAlives = new();
    private readonly ConcurrentQueue<NntpProviderSession> _idle = new();
    private readonly ConcurrentDictionary<NntpProviderSession, byte> _live = new();
    private readonly HashSet<int> _usedSlots = [];
    private readonly object _slotGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _fillGate = new(1, 1);
    private int _created;
    private int _activeLeases;
    private int _disposed;
    private int _replenishStarted;
    private Task? _replenishTask;
    private TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Delay between periodic deficit fills toward <see cref="BackFillerProviderDefinition.MaxSessions"/>.</summary>
    internal static readonly TimeSpan ReplenishInterval = TimeSpan.FromSeconds(15);

    /// <summary>Creates a pool for <paramref name="provider"/>.</summary>
    internal NntpSessionPool(
        BackFillerProviderDefinition provider,
        NntpSessionOptions options,
        INntpTransportFactory transport,
        ILogger logger,
        TimeSpan? shutdownGrace = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);
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
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _shutdownGrace = shutdownGrace ?? TimeSpan.FromSeconds(2);
        _leaseCeiling = provider.MaxSessions;
        _leases = new SemaphoreSlim(provider.MaxSessions, provider.MaxSessions);
        _drained.TrySetResult();
    }

    /// <summary>Gets the provider backbone.</summary>
    internal string Backbone => _provider.Backbone;

    /// <summary>Gets the provider definition this pool was created for.</summary>
    internal BackFillerProviderDefinition Provider => _provider;

    /// <summary>Gets the number of live session objects.</summary>
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
    internal event Action? ActiveSessionCountChanged;

    /// <summary>
    /// Connects the desired <see cref="BackFillerProviderDefinition.MaxSessions"/> slots.
    /// Does not require Article Work. Failed attempts do not fail the call.
    /// </summary>
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

    /// <summary>Acquires an exclusive session lease.</summary>
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
    /// <param name="cancellationToken">Cancellation token.</param>
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
            while (_leases.CurrentCount > desiredPermits && _leases.Wait(0))
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
    internal Task DrainAndDisposeAsync(CancellationToken cancellationToken) =>
        DisposeCoreAsync(forceAfterGrace: false, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(DisposeCoreAsync(forceAfterGrace: true, CancellationToken.None));

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
                await _drained.Task.WaitAsync(_shutdownGrace).ConfigureAwait(false);
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

    private NntpSessionLease Issue(NntpProviderSession session)
    {
        if (Interlocked.Increment(ref _activeLeases) == 1)
        {
            _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return new NntpSessionLease(this, session);
    }

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
        var failure = await session.ConnectAsync(_transport, cancellationToken).ConfigureAwait(false);
        if (failure is null && session.State == NntpSessionState.Ready)
        {
            NotifyActiveSessionCountChanged();
            return session;
        }

        await RetireSessionAsync(session, failure?.Reason ?? "connect failed", replenish: false)
            .ConfigureAwait(false);
        throw new NntpProviderConnectException(
            failure?.Kind ?? ArticleRetrievalKind.ProviderFailure,
            failure?.StatusCode,
            failure?.Reason ?? "NNTP connect failed.");
    }

    private void EnqueueIdle(NntpProviderSession session)
    {
        _idle.Enqueue(session);
        StartKeepAlive(session);
    }

    private void StartKeepAlive(NntpProviderSession session)
    {
        if (!_provider.DateKeepAliveEnabled || Volatile.Read(ref _disposed) == 1 || _shutdown.IsCancellationRequested)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var registration = new KeepAliveRegistration(cts);
        registration.Task = RunKeepAliveAsync(session, cts);
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

    private void StartPeriodicReplenish()
    {
        if (Interlocked.Exchange(ref _replenishStarted, 1) == 1)
        {
            return;
        }

        _replenishTask = RunReplenishAsync(_shutdown.Token);
    }

    private void RequestReplenish()
    {
        if (Volatile.Read(ref _disposed) == 1 || _shutdown.IsCancellationRequested)
        {
            return;
        }

        StartPeriodicReplenish();
        _ = FillDeficitObservedAsync(_shutdown.Token);
    }

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

    private void NotifyActiveSessionCountChanged()
    {
        ActiveSessionCountChanged?.Invoke();
    }

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

    private void ReleaseConnectionNumber(int connectionNumber)
    {
        lock (_slotGate)
        {
            _usedSlots.Remove(connectionNumber);
        }
    }

    private static async Task WaitUntilNotBusyAsync(NntpProviderSession session, CancellationToken cancellationToken)
    {
        while (session.State == NntpSessionState.Busy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private sealed class KeepAliveRegistration(CancellationTokenSource cts)
    {
        internal CancellationTokenSource Cts { get; } = cts;

        internal Task Task { get; set; } = Task.CompletedTask;
    }
}

/// <summary>Connect-time failure that preserves retrieval classification.</summary>
internal sealed class NntpProviderConnectException : InvalidOperationException
{
    /// <summary>Initializes a connect failure.</summary>
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
