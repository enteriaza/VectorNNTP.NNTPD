using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Authentication;

namespace VectorNNTP.NNTPD.Networking.Listeners;

/// <summary>
/// Application service that binds implicit-TLS NNTP listeners after a usable certificate context exists.
/// </summary>
/// <remarks>
/// Idle when <see cref="NntpdOptions.IsTlsListenerEnabled"/> is <see langword="false"/>.
/// Does not rebind or restart on certificate rotation; new handshakes observe the latest context.
/// PROXY preamble (when required) is consumed on the cleartext socket before TLS.
/// </remarks>
public sealed class NntpTlsListenerService : IApplicationService, IAsyncDisposable
{
    private readonly IOptions<NntpdOptions> _options;
    private readonly ITlsCertificateContextProvider _certificateProvider;
    private readonly ITrustedProxyHosts _trustedProxyHosts;
    private readonly INntpAuthenticationProvider _authenticationProvider;
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
        ILoggerFactory loggerFactory,
        ILogger<NntpTlsListenerService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(certificateProvider);
        ArgumentNullException.ThrowIfNull(trustedProxyHosts);
        ArgumentNullException.ThrowIfNull(authenticationProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _authenticationProvider = authenticationProvider;
        _certificateProvider = certificateProvider;
        _trustedProxyHosts = trustedProxyHosts;
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

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        var options = _options.Value;
        if (!options.IsTlsListenerEnabled)
        {
            _logger.LogInformation("TLS disabled (BindPortTls=0); TLS NNTP listener idle.");
            return Task.CompletedTask;
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
                _loggerFactory.CreateLogger($"{nameof(SocketAcceptListener)}.Tls"));
            listener.Start();
            _listeners.Add(listener);
        }

        _execution = WaitUntilStoppedAsync(_runCts.Token);
        _logger.LogInformation(
            "TLS NNTP listeners started ({ListenerCount}) on port {Port}.",
            _listeners.Count,
            options.BindPortTls);
        return Task.CompletedTask;
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
                _logger.LogDebug(ex, "Error completing TLS connection during stop.");
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

    private async ValueTask OnAcceptedAsync(Socket socket, CancellationToken cancellationToken)
    {
        NntpConnection? connection = null;
        try
        {
            if (!ProxyPreambleResolver.TryGetTcpPeer(socket, out var tcpPeer))
            {
                _logger.LogDebug("TLS accept discarded: remote endpoint unavailable.");
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
                _logger.LogInformation(
                    ex,
                    "Rejected TLS connection from trusted proxy peer {TcpPeer}: invalid PROXY preamble.",
                    tcpPeer);
                return;
            }

            connection = await NntpConnection.StartTlsAsync(
                    socket,
                    _certificateProvider,
                    preamble.Identity,
                    _loggerFactory.CreateLogger<NntpConnection>(),
                    cancellationToken,
                    preamble.Leftover)
                .ConfigureAwait(false);
            _connections[connection] = 0;

            var session = new NntpSession(
                connection,
                _loggerFactory.CreateLogger<NntpSession>(),
                certificateProvider: _certificateProvider,
                authenticationProvider: _authenticationProvider,
                allowCleartextAuth: _options.Value.AllowCleartextAuth,
                loggerFactory: _loggerFactory);

            if (!connection.TryGetNegotiatedTlsParameters(out var tlsVersion, out var cipher))
            {
                throw new InvalidOperationException("TLS connection missing negotiated parameters after handshake.");
            }

            ConnectionAcceptanceLogging.LogTlsAccepted(
                _logger,
                connection.ClientIdentity,
                tlsVersion,
                cipher);

            try
            {
                await session.RunAsync(_runCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_runCts.IsCancellationRequested)
            {
                // Listener stopping.
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "TLS handshake or connection setup failed; listener continues.");
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
