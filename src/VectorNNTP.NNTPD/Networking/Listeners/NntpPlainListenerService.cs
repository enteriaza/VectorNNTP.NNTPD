using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.History;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Authentication;
using VectorNNTP.NNTPD.Session.SpeedTest;
using VectorNNTP.NNTPD.Transit;
using VectorNNTP.NNTPD.Diagnostics;

namespace VectorNNTP.NNTPD.Networking.Listeners;

/// <summary>
/// Application service that binds cleartext NNTP TCP listeners and owns accepted plain connections.
/// </summary>
/// <remarks>
/// Starts after Cloudflare DNS reconciliation and before ACME. Does not wait for certificates.
/// <see cref="StartAsync"/> succeeds only after every planned endpoint has bound; a bind
/// failure disposes already-bound endpoints and fails startup. Accept-loop failures after a
/// successful bind do not fail startup.
/// </remarks>
public sealed class NntpPlainListenerService : IApplicationService, IAsyncDisposable
{
    private readonly IOptions<NntpdOptions> _options;
    private readonly ITrustedProxyHosts _trustedProxyHosts;
    private readonly ITlsCertificateContextProvider _certificateProvider;
    private readonly INntpAuthenticationProvider _authenticationProvider;
    private readonly IArticleIngestionQueue _articleIngestion;
    private readonly IHistoryDb? _historyDb;
    private readonly ISpeedTestCoordinator? _speedTest;
    private readonly ITransitPeerAuthorization _transitPeerAuthorization;
    private readonly ITransitInboundConnectionLimiter _inboundConnectionLimiter;
    private readonly IFeedDiagnostics _feedDiagnostics;
    private readonly INntpSessionCensus? _sessionCensus;
    private readonly ITransitPeerMetrics? _peerMetrics;
    private readonly IListenSocketBinder _listenBinder;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<NntpPlainListenerService> _logger;
    private readonly ConcurrentDictionary<NntpConnection, byte> _connections = new();
    private readonly List<SocketAcceptListener> _listeners = [];
    private readonly CancellationTokenSource _runCts = new();
    private Task? _execution;
    private int _started;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="NntpPlainListenerService"/> class.</summary>
    public NntpPlainListenerService(
        IOptions<NntpdOptions> options,
        ITrustedProxyHosts trustedProxyHosts,
        ITlsCertificateContextProvider certificateProvider,
        INntpAuthenticationProvider authenticationProvider,
        IArticleIngestionQueue articleIngestion,
        ITransitPeerAuthorization transitPeerAuthorization,
        ILoggerFactory loggerFactory,
        ILogger<NntpPlainListenerService> logger,
        ITransitInboundConnectionLimiter? inboundConnectionLimiter = null,
        IHistoryDb? historyDb = null,
        ISpeedTestCoordinator? speedTest = null,
        IFeedDiagnostics? feedDiagnostics = null,
        IListenSocketBinder? listenBinder = null,
        INntpSessionCensus? sessionCensus = null,
        ITransitPeerMetrics? peerMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trustedProxyHosts);
        ArgumentNullException.ThrowIfNull(certificateProvider);
        ArgumentNullException.ThrowIfNull(authenticationProvider);
        ArgumentNullException.ThrowIfNull(articleIngestion);
        ArgumentNullException.ThrowIfNull(transitPeerAuthorization);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _trustedProxyHosts = trustedProxyHosts;
        _certificateProvider = certificateProvider;
        _authenticationProvider = authenticationProvider;
        _articleIngestion = articleIngestion;
        _transitPeerAuthorization = transitPeerAuthorization;
        _inboundConnectionLimiter = inboundConnectionLimiter ?? TransitInboundConnectionLimiter.Disabled;
        _historyDb = historyDb;
        _speedTest = speedTest;
        _feedDiagnostics = feedDiagnostics ?? NullFeedDiagnostics.Instance;
        _sessionCensus = sessionCensus;
        _peerMetrics = peerMetrics;
        _listenBinder = listenBinder ?? SocketListenBinder.Instance;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "NntpPlainListener";

    /// <inheritdoc />
    public Task? Execution => _execution;

    /// <summary>Gets the number of active plain connections (tests).</summary>
    internal int ActiveConnectionCount => _connections.Count;

    /// <summary>Gets bound local endpoints after start (tests).</summary>
    internal IReadOnlyList<IPEndPoint> LocalEndPoints =>
        _listeners.Select(static l => l.LocalEndPoint).ToArray();

    /// <summary>Gets whether any accept loop is still running (tests).</summary>
    internal bool HasActiveAcceptLoops => _listeners.Exists(static l => l.AcceptLoopActive);

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = _options.Value;
            var bindings = ListenEndpointPlanner.Plan(options.BindAddress, options.BindPort);
            if (bindings.Count == 0)
            {
                throw new InvalidOperationException("No listen bindings were produced for the plain NNTP port.");
            }

            foreach (var binding in bindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var listener = new SocketAcceptListener(
                    binding,
                    OnAcceptedAsync,
                    _loggerFactory.CreateLogger($"{nameof(SocketAcceptListener)}.Plain"),
                    _listenBinder);
                try
                {
                    listener.Start();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await listener.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    NetworkingLogMessages.ListenerBindFailed(
                        _logger,
                        ex,
                        "Plain",
                        binding.EndPoint.ToString(),
                        options.BindPort);
                    await listener.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                _listeners.Add(listener);
            }

            _execution = WaitUntilStoppedAsync(_runCts.Token);
            NetworkingLogMessages.PlainListenersStarted(_logger, _listeners.Count, options.BindPort);
        }
        catch
        {
            await RollbackPartialStartupAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _runCts.CancelAsync().ConfigureAwait(false);

        foreach (var listener in _listeners)
        {
            await listener.StopAsync().ConfigureAwait(false);
        }

        var connections = _connections.Keys.ToArray();
        foreach (var connection in connections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (connection is NntpConnection nntpConnection)
                {
                    nntpConnection.NoteDisconnectReason(TcpDisconnectReason.Shutdown);
                }

                await connection.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                NetworkingLogMessages.PlainConnectionCompleteError(_logger, ex);
            }
        }

        foreach (var connection in connections)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort.
            }

            _connections.TryRemove(connection, out _);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort dispose.
        }

        foreach (var listener in _listeners)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        _listeners.Clear();
        _runCts.Dispose();
    }

    private async Task RollbackPartialStartupAsync()
    {
        foreach (var listener in _listeners)
        {
            try
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort rollback of already-bound endpoints.
            }
        }

        _listeners.Clear();
        Interlocked.Exchange(ref _started, 0);
    }

    private async ValueTask OnAcceptedAsync(Socket socket, CancellationToken cancellationToken)
    {
        NntpConnection? connection = null;
        try
        {
            if (!ProxyPreambleResolver.TryGetTcpPeer(socket, out var tcpPeer))
            {
                NetworkingLogMessages.PlainAcceptDiscarded(_logger);
                return;
            }

            ProxyPreambleResolution preamble;
            try
            {
                preamble = await ProxyPreambleResolver
                    .ResolveAsync(socket, tcpPeer, _trustedProxyHosts, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ProxyProtocolException ex)
            {
                NetworkingLogMessages.PlainProxyPreambleRejected(_logger, ex, tcpPeer);
                return;
            }

            connection = NntpConnection.StartPlain(
                socket,
                preamble.Identity,
                _loggerFactory.CreateLogger<NntpConnection>(),
                preamble.Leftover,
                _feedDiagnostics);
            _connections[connection] = 0;

            // Session owns NNTP greeting/command loop; transport owns the socket lifecycle.
            var session = new NntpSession(
                connection,
                _loggerFactory.CreateLogger<NntpSession>(),
                certificateProvider: _certificateProvider,
                authenticationProvider: _authenticationProvider,
                allowCleartextAuth: _options.Value.AllowCleartextAuth,
                loggerFactory: _loggerFactory,
                articleIngestion: _articleIngestion,
                transitPeerAuthorization: _transitPeerAuthorization,
                streamOutstandingArticleDepth: _options.Value.Transit.StreamOutstandingArticleDepth,
                historyDb: _historyDb,
                speedTest: _speedTest,
                sessionCensus: _sessionCensus,
                peerMetrics: _peerMetrics,
                commandIdleTimeout: TimeSpan.FromSeconds(_options.Value.IdleTime));
            ConnectionAcceptanceLogging.LogPlainAccepted(_logger, connection.ClientIdentity);

            if (!TransitConnectionAdmission.TryAdmit(_inboundConnectionLimiter, session, out var lease))
            {
                session.RecordPeerRejected();
                _feedDiagnostics.OnRejected(
                    session.Authorization.TransitPeerName ?? "none",
                    ConnectionAcceptanceLogging.FormatEndpoint(session.ClientIdentity.Client));
                await TransitConnectionAdmission
                    .WriteUnavailableAsync(connection, _runCts.Token)
                    .ConfigureAwait(false);
                return;
            }

            session.RecordPeerAccepted();
            session.FeedProbe = _feedDiagnostics.OnAccepted(session);
            try
            {
                await session.RunAsync(_runCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_runCts.IsCancellationRequested)
            {
                // Listener stopping.
            }
            finally
            {
                _feedDiagnostics.OnReleased(session.FeedProbe);
                lease.Dispose();
            }
        }
        finally
        {
            if (connection is not null)
            {
                _connections.TryRemove(connection, out _);
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort.
                }
            }
            else
            {
                socket.Dispose();
            }
        }
    }

    private async Task WaitUntilStoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected.
        }
    }
}
