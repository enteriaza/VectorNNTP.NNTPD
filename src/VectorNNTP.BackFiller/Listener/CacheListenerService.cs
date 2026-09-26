using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Retention;

namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// Process-wide cache Listener: binds configured endpoints, authenticates TLS, and serves retained articles.
/// Does not own retention, RabbitMQ, or Article Work settlement.
/// </summary>
public sealed class CacheListenerService : IHostedService, IAsyncDisposable
{
    private readonly BackFillerRuntimeOptions _runtime;
    private readonly ICacheListenerCertificateSource _certificates;
    private readonly IArticleRetentionAuthority _retention;
    private readonly ILogger<CacheListenerService> _logger;
    private readonly object _gate = new();
    private readonly List<Socket> _listenSockets = [];
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private readonly CancellationTokenSource _runCts = new();

    private CacheListenerCertificateMaterial? _certificate;
    private Task? _acceptTask;
    private CacheListenerState _state = CacheListenerState.Created;
    private int _started;
    private int _disposed;
    private int _activeConnections;

    /// <summary>Initializes the Listener service.</summary>
    public CacheListenerService(
        BackFillerRuntimeOptions runtime,
        ICacheListenerCertificateSource certificates,
        IArticleRetentionAuthority retention,
        ILogger<CacheListenerService> logger)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(logger);
        _runtime = runtime;
        _certificates = certificates;
        _retention = retention;
        _logger = logger;
    }

    /// <summary>Gets the local lifecycle state.</summary>
    public CacheListenerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Gets currently admitted connections.</summary>
    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    /// <summary>Gets bound listen endpoints (tests).</summary>
    internal IReadOnlyList<EndPoint?> LocalEndPoints
    {
        get
        {
            lock (_gate)
            {
                return [.. _listenSockets.Select(static socket => socket.LocalEndPoint)];
            }
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        lock (_gate)
        {
            _state = CacheListenerState.Starting;
        }

        try
        {
            if (!_certificates.TryGetCurrent(out var material))
            {
                throw new InvalidOperationException(
                    "Cache Listener cannot start because no TLS certificate is available. ACME provisioning is deferred; place backfiller-listener.pfx in CertificateDirectory.");
            }

            _certificate = material;
            var endpoints = CacheListenerEndpoints.Build(_runtime);
            if (endpoints.Count == 0)
            {
                throw new InvalidOperationException("Cache Listener has no bind endpoints.");
            }

            CacheListenerLogMessages.Starting(_logger, _runtime.BindPort, endpoints.Count);
            BindEndpoints(endpoints);
            if (_listenSockets.Count == 0)
            {
                throw new InvalidOperationException("Cache Listener failed to bind any endpoint.");
            }

            lock (_gate)
            {
                _state = CacheListenerState.Running;
            }

            CacheListenerLogMessages.Running(_logger, _listenSockets.Count, _runtime.BindPort);
            _acceptTask = AcceptAllAsync(_runCts.Token);
        }
        catch (Exception ex)
        {
            CacheListenerLogMessages.StartFailed(_logger, ex.Message);
            await DisposeBoundResourcesAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _state = CacheListenerState.Stopped;
            }

            Interlocked.Exchange(ref _started, 0);
            throw;
        }
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

        lock (_gate)
        {
            _state = CacheListenerState.Retiring;
        }

        CacheListenerLogMessages.Retiring(_logger);
        await _runCts.CancelAsync().ConfigureAwait(false);
        CloseListenSockets();

        var accept = _acceptTask;
        if (accept is not null)
        {
            try
            {
                await accept.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await DrainConnectionsAsync().ConfigureAwait(false);
        await DisposeBoundResourcesAsync().ConfigureAwait(false);
        _runCts.Dispose();
        lock (_gate)
        {
            _state = CacheListenerState.Stopped;
        }

        CacheListenerLogMessages.Stopped(_logger);
    }

    private void BindEndpoints(IReadOnlyList<IPEndPoint> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            try
            {
                var socket = CacheListenerEndpoints.CreateBoundListenSocket(endpoint);
                lock (_gate)
                {
                    _listenSockets.Add(socket);
                }

                CacheListenerLogMessages.EndpointBound(_logger, endpoint.ToString(), endpoint.AddressFamily.ToString());
            }
            catch (SocketException ex) when (
                IsImplicitWildcard(endpoint)
                && ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported or SocketError.AddressNotAvailable)
            {
                CacheListenerLogMessages.WildcardFamilySkipped(_logger, endpoint.AddressFamily.ToString(), ex.SocketErrorCode.ToString());
            }
        }
    }

    private bool IsImplicitWildcard(IPEndPoint endpoint)
    {
        if (_runtime.BindAddressTokens.Count == 0)
        {
            return true;
        }

        return _runtime.BindAddressTokens.Any(BackFillerOptions.IsBindAddressWildcard)
               && (endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any));
    }

    private async Task AcceptAllAsync(CancellationToken cancellationToken)
    {
        Socket[] sockets;
        lock (_gate)
        {
            sockets = [.. _listenSockets];
        }

        await Task.WhenAll(sockets.Select(socket => AcceptLoopAsync(socket, cancellationToken))).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(Socket listenSocket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await listenSocket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            var remote = accepted.RemoteEndPoint?.ToString() ?? "<unknown>";
            if (!TryAdmit())
            {
                CacheListenerLogMessages.CapacityRejected(_logger, remote, _runtime.Listener.MaxActiveConnections);
                accepted.Dispose();
                continue;
            }

            CacheListenerLogMessages.Accepted(_logger, remote, ActiveConnections);
            var connection = ProcessAsync(accepted, remote, cancellationToken);
            _connections[connection] = 0;
            _ = connection.ContinueWith(
                static (task, state) => ((CacheListenerService)state!)._connections.TryRemove(task, out _),
                this,
                TaskScheduler.Default);
        }
    }

    private async Task ProcessAsync(Socket accepted, string remote, CancellationToken cancellationToken)
    {
        var handshakeCompleted = false;
        try
        {
            await using var network = new NetworkStream(accepted, ownsSocket: true);
            var certificate = _certificate ?? throw new InvalidOperationException("Listener certificate is unavailable.");
            await using var ssl = new SslStream(network, leaveInnerStreamOpen: true);
            var options = new SslServerAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };
            if (certificate.Context is not null)
            {
                options.ServerCertificateContext = certificate.Context;
            }
            else
            {
                options.ServerCertificate = certificate.Certificate;
            }

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(_runtime.Listener.TlsHandshakeTimeout);
            try
            {
                await ssl.AuthenticateAsServerAsync(options, handshakeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (handshakeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("tls-handshake");
            }

            handshakeCompleted = true;
            await using var session = new CacheListenerSession(
                new StreamCacheListenerTransport(ssl, _runtime.Listener.IoProgressTimeout, leaveInnerStreamOpen: true),
                new CacheListenerRetentionHandler(_retention),
                _runtime.Listener);
            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AuthenticationException)
        {
            CacheListenerLogMessages.HandshakeFailed(_logger, remote);
        }
        catch (TimeoutException ex)
        {
            CacheListenerLogMessages.ConnectionTimedOut(
                _logger,
                handshakeCompleted ? "io-progress" : ex.Message,
                remote);
        }
        catch (IOException)
        {
        }
        catch (Exception ex)
        {
            CacheListenerLogMessages.ConnectionFailed(_logger, remote, ex.GetType().Name);
        }
        finally
        {
            ReleaseAdmit();
        }
    }

    private bool TryAdmit()
    {
        while (true)
        {
            var current = Volatile.Read(ref _activeConnections);
            if (current >= _runtime.Listener.MaxActiveConnections)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _activeConnections, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private void ReleaseAdmit() => Interlocked.Decrement(ref _activeConnections);

    private void CloseListenSockets()
    {
        lock (_gate)
        {
            foreach (var socket in _listenSockets)
            {
                try
                {
                    socket.Dispose();
                }
                catch (Exception)
                {
                }
            }

            _listenSockets.Clear();
        }
    }

    private async Task DrainConnectionsAsync()
    {
        var tasks = _connections.Keys.ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private async Task DisposeBoundResourcesAsync()
    {
        CloseListenSockets();
        _certificate?.Dispose();
        _certificate = null;
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
