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
/// Application service that binds implicit-TLS NNTP listeners after a usable certificate context exists.
/// </summary>
/// <remarks>
/// Idle when <see cref="NntpdOptions.IsTlsListenerEnabled"/> is <see langword="false"/>.
/// Does not rebind or restart on certificate rotation; new handshakes observe the latest context.
/// PROXY preamble (when required) is consumed on the cleartext socket before TLS.
/// When TLS is enabled, <see cref="StartAsync"/> succeeds only after every planned endpoint has
/// bound; a bind failure disposes already-bound endpoints and fails startup. Accept-loop
/// failures after a successful bind do not fail startup.
/// </remarks>
public sealed class NntpTlsListenerService : IApplicationService, IAsyncDisposable
{
    private readonly IOptions<NntpdOptions> _options;
    private readonly ITlsCertificateContextProvider _certificateProvider;
    private readonly ITrustedProxyHosts _trustedProxyHosts;
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
    private readonly ILogger<NntpTlsListenerService> _logger;
    private readonly ConcurrentDictionary<NntpConnection, byte> _connections = new();
    private readonly List<SocketAcceptListener> _listeners = [];
    private readonly CancellationTokenSource _runCts = new();
    private Task? _execution;
    private int _started;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="NntpTlsListenerService"/> class.</summary>
    public NntpTlsListenerService(
        IOptions<NntpdOptions> options,
        ITlsCertificateContextProvider certificateProvider,
        ITrustedProxyHosts trustedProxyHosts,
        INntpAuthenticationProvider authenticationProvider,
        IArticleIngestionQueue articleIngestion,
        ITransitPeerAuthorization transitPeerAuthorization,
        ILoggerFactory loggerFactory,
        ILogger<NntpTlsListenerService> logger,
        ITransitInboundConnectionLimiter? inboundConnectionLimiter = null,
        IHistoryDb? historyDb = null,
        ISpeedTestCoordinator? speedTest = null,
        IFeedDiagnostics? feedDiagnostics = null,
        IListenSocketBinder? listenBinder = null,
        INntpSessionCensus? sessionCensus = null,
        ITransitPeerMetrics? peerMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(certificateProvider);
        ArgumentNullException.ThrowIfNull(trustedProxyHosts);
        ArgumentNullException.ThrowIfNull(authenticationProvider);
        ArgumentNullException.ThrowIfNull(articleIngestion);
        ArgumentNullException.ThrowIfNull(transitPeerAuthorization);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _authenticationProvider = authenticationProvider;
        _certificateProvider = certificateProvider;
        _trustedProxyHosts = trustedProxyHosts;
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
    public string Name => "NntpTlsListener";

    /// <inheritdoc />
    public Task? Execution => _execution;

    /// <summary>Gets whether TLS listeners were bound (tests).</summary>
    internal bool ListenersBound => _listeners.Count > 0;

    /// <summary>Gets the number of active TLS connections (tests).</summary>
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
            var options = _options.Value;
            if (!options.IsTlsListenerEnabled)
            {
                NetworkingLogMessages.TlsListenerIdle(_logger);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!_certificateProvider.IsAvailable)
            {
                throw new InvalidOperationException(
                    "TLS listener cannot start: no usable TLS certificate context is published.");
            }

            var bindings = ListenEndpointPlanner.Plan(options.BindAddress, options.BindPortTls);
            if (bindings.Count == 0)
            {
                throw new InvalidOperationException("No listen bindings were produced for the TLS NNTP port.");
            }

            foreach (var binding in bindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var listener = new SocketAcceptListener(
                    binding,
                    OnAcceptedAsync,
                    _loggerFactory.CreateLogger($"{nameof(SocketAcceptListener)}.Tls"),
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
                        "TLS",
                        binding.EndPoint.ToString(),
                        options.BindPortTls);
                    await listener.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                _listeners.Add(listener);
            }

            _execution = WaitUntilStoppedAsync(_runCts.Token);
            NetworkingLogMessages.TlsListenersStarted(_logger, _listeners.Count, options.BindPortTls);
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
                await connection.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                NetworkingLogMessages.TlsConnectionCompleteError(_logger, ex);
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
                NetworkingLogMessages.TlsAcceptDiscarded(_logger);
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
                NetworkingLogMessages.TlsProxyPreambleRejected(_logger, ex, tcpPeer);
                return;
            }

            connection = await NntpConnection.StartTlsAsync(
                    socket,
                    _certificateProvider,
                    preamble.Identity,
                    _loggerFactory.CreateLogger<NntpConnection>(),
                    cancellationToken,
                    preamble.Leftover,
                    _feedDiagnostics)
                .ConfigureAwait(false);
            _connections[connection] = 0;

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
                peerMetrics: _peerMetrics);

            if (!connection.TryGetNegotiatedTlsParameters(out var tlsVersion, out var cipher))
            {
                throw new InvalidOperationException("TLS connection missing negotiated parameters after handshake.");
            }

            ConnectionAcceptanceLogging.LogTlsAccepted(
                _logger,
                connection.ClientIdentity,
                tlsVersion,
                cipher);

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NetworkingLogMessages.TlsHandshakeOrSetupFailed(_logger, ex);
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
                try
                {
                    socket.Dispose();
                }
                catch
                {
                    // Best-effort.
                }
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
