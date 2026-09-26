using System.Collections.Concurrent;

namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Bounded per-provider NNTP session pool. One lease owns one session.
/// </summary>
public sealed class NntpSessionPool : IAsyncDisposable
{
    private readonly BackFillerProviderDefinition _provider;
    private readonly NntpSessionOptions _options;
    private readonly INntpTransportFactory _transport;
    private readonly ILogger _logger;
    private readonly TimeSpan _shutdownGrace;
    private readonly SemaphoreSlim _leases;
    private readonly ConcurrentQueue<NntpProviderSession> _idle = new();
    private readonly ConcurrentDictionary<NntpProviderSession, byte> _live = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _created;
    private int _activeLeases;
    private int _disposed;
    private TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates a pool for <paramref name="provider"/>.</summary>
    public NntpSessionPool(
        BackFillerProviderDefinition provider,
        NntpSessionOptions options,
        INntpTransportFactory transport,
        ILogger logger,
        TimeSpan? shutdownGrace = null)
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
        _shutdownGrace = shutdownGrace ?? TimeSpan.FromSeconds(2);
        _leases = new SemaphoreSlim(provider.MaxSessions, provider.MaxSessions);
        _drained.TrySetResult();
    }

    /// <summary>Gets the provider backbone.</summary>
    public string Backbone => _provider.Backbone;

    /// <summary>Gets the provider definition this pool was created for.</summary>
    internal BackFillerProviderDefinition Provider => _provider;

    /// <summary>Gets the number of live session objects.</summary>
    public int LiveSessionCount => _live.Count;

    /// <summary>Gets the number of outstanding leases.</summary>
    public int ActiveLeaseCount => Volatile.Read(ref _activeLeases);

    /// <summary>Connects <see cref="BackFillerProviderDefinition.MinSessions"/> sessions into the idle pool.</summary>
    public async Task WarmupAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < _provider.MinSessions; i++)
        {
            var session = await CreateReadySessionAsync(cancellationToken).ConfigureAwait(false);
            _idle.Enqueue(session);
        }
    }

    /// <summary>Acquires an exclusive session lease.</summary>
    public async Task<NntpSessionLease> AcquireAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _leases.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            while (_idle.TryDequeue(out var idle))
            {
                if (idle.IsReusable)
                {
                    return Issue(idle);
                }

                await RetireSessionAsync(idle, "idle session was not reusable").ConfigureAwait(false);
            }

            var created = await CreateReadySessionAsync(linked.Token).ConfigureAwait(false);
            return Issue(created);
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
                _idle.Enqueue(session);
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
                _leases.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
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

        var session = new NntpProviderSession(_provider, _options, _logger);
        _live[session] = 0;
        var failure = await session.ConnectAsync(_transport, cancellationToken).ConfigureAwait(false);
        if (failure is null && session.State == NntpSessionState.Ready)
        {
            return session;
        }

        await RetireSessionAsync(session, failure?.Reason ?? "connect failed").ConfigureAwait(false);
        throw new NntpProviderConnectException(
            failure?.Kind ?? ArticleRetrievalKind.ProviderFailure,
            failure?.StatusCode,
            failure?.Reason ?? "NNTP connect failed.");
    }

    private async Task RetireSessionAsync(NntpProviderSession session, string reason)
    {
        if (_live.TryRemove(session, out _))
        {
            Interlocked.Decrement(ref _created);
            NntpLogMessages.SessionRetired(_logger, _provider.Backbone, reason);
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Connect-time failure that preserves retrieval classification.</summary>
public sealed class NntpProviderConnectException : InvalidOperationException
{
    /// <summary>Initializes a connect failure.</summary>
    public NntpProviderConnectException(ArticleRetrievalKind kind, int? statusCode, string reason)
        : base(reason)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    /// <summary>Gets the retrieval classification.</summary>
    public ArticleRetrievalKind Kind { get; }

    /// <summary>Gets the NNTP status when present.</summary>
    public int? StatusCode { get; }
}
