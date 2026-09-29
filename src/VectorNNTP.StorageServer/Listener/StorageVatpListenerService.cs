using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Listeners;
using VectorNNTP.StorageServer.Configuration;

namespace VectorNNTP.StorageServer.Listener;

/// <summary>
/// Process-wide VATP TLS listener: binds configured endpoints, authenticates TLS, and serves
/// minimal VATP sessions. Does not own article storage.
/// </summary>
public sealed class StorageVatpListenerService : IApplicationService, IAsyncDisposable
{
    private readonly StorageServerRuntimeOptions _runtime;
    private readonly ITlsCertificateContextProvider _certificates;
    private readonly IStorageArticleOpenBoundary _openBoundary;
    private readonly IAcmeCertificateReadiness _readiness;
    private readonly ILogger<StorageVatpListenerService> _logger;
    private readonly object _gate = new();
    private readonly List<Socket> _listenSockets = [];
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private readonly CancellationTokenSource _runCts = new();

    private Task? _acceptTask;
    private StorageVatpListenerState _state = StorageVatpListenerState.Created;
    private int _started;
    private int _disposed;
    private int _activeConnections;

    /// <summary>Initializes the Listener service for isolated tests with a pre-ready certificate gate.</summary>
    public StorageVatpListenerService(
        StorageServerRuntimeOptions runtime,
        ITlsCertificateContextProvider certificates,
        IStorageArticleOpenBoundary openBoundary,
        ILogger<StorageVatpListenerService> logger)
        : this(runtime, certificates, openBoundary, CreateReadyGate(), logger)
    {
    }

    /// <summary>Initializes the Listener service.</summary>
    public StorageVatpListenerService(
        StorageServerRuntimeOptions runtime,
        ITlsCertificateContextProvider certificates,
        IStorageArticleOpenBoundary openBoundary,
        IAcmeCertificateReadiness readiness,
        ILogger<StorageVatpListenerService> logger)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(openBoundary);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(logger);
        _runtime = runtime;
        _certificates = certificates;
        _openBoundary = openBoundary;
        _readiness = readiness;
        _logger = logger;
    }

    private static IAcmeCertificateReadiness CreateReadyGate()
    {
        var readiness = new AcmeCertificateReadiness();
        readiness.MarkReady();
        return readiness;
    }

    /// <inheritdoc />
    public string Name => "StorageVatpListener";

    /// <inheritdoc />
    public Task? Execution => _acceptTask;

    /// <summary>Gets the local lifecycle state.</summary>
    public StorageVatpListenerState State
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
            _state = StorageVatpListenerState.Starting;
        }

        try
        {
            if (!_readiness.IsReady)
            {
                throw new InvalidOperationException(
                    "Storage VATP Listener cannot start because the ACME certificate is not ready. StorageServer is TLS-only and the listener must wait for a usable certificate.");
            }

            if (!_certificates.IsAvailable)
            {
                throw new InvalidOperationException(
                    $"Storage VATP Listener cannot start because no TLS certificate is available from ACME state '{_runtime.CertificateDirectory}'.");
            }

            var bindings = ListenEndpointPlanner.Plan(_runtime.BindAddressTokens, _runtime.BindPortTls);
            if (bindings.Count == 0)
            {
                throw new InvalidOperationException("Storage VATP Listener has no bind endpoints.");
            }

            StorageVatpListenerLogMessages.Starting(_logger, _runtime.BindPortTls, bindings.Count);
            BindEndpoints(bindings);
            if (_listenSockets.Count == 0)
            {
                throw new InvalidOperationException("Storage VATP Listener failed to bind any endpoint.");
            }

            lock (_gate)
            {
                _state = StorageVatpListenerState.Running;
            }

            StorageVatpListenerLogMessages.Running(_logger, _listenSockets.Count, _runtime.BindPortTls);
            _acceptTask = AcceptAllAsync(_runCts.Token);
        }
        catch (Exception ex)
        {
            StorageVatpListenerLogMessages.StartFailed(_logger, ex.Message);
            await DisposeBoundResourcesAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _state = StorageVatpListenerState.Stopped;
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
            _state = StorageVatpListenerState.Retiring;
        }

        StorageVatpListenerLogMessages.Retiring(_logger);
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
            _state = StorageVatpListenerState.Stopped;
        }

        StorageVatpListenerLogMessages.Stopped(_logger);
    }

    private void BindEndpoints(IReadOnlyList<ListenBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            try
            {
                var socket = CreateBoundListenSocket(binding);
                lock (_gate)
                {
                    _listenSockets.Add(socket);
                }

                StorageVatpListenerLogMessages.EndpointBound(
                    _logger,
                    binding.EndPoint.ToString() ?? string.Empty,
                    binding.Address.AddressFamily.ToString());
            }
            catch (SocketException ex) when (
                IsImplicitWildcard(binding.EndPoint)
                && ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported or SocketError.AddressNotAvailable)
            {
                StorageVatpListenerLogMessages.WildcardFamilySkipped(
                    _logger,
                    binding.Address.AddressFamily.ToString(),
                    ex.SocketErrorCode.ToString());
            }
        }
    }

    private static Socket CreateBoundListenSocket(ListenBinding binding)
    {
        var endpoint = binding.EndPoint;
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = binding.DualMode;
        }

        try
        {
            socket.Bind(endpoint);
            socket.Listen(512);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private bool IsImplicitWildcard(IPEndPoint endpoint)
    {
        if (_runtime.BindAddressTokens.Count == 0)
        {
            return true;
        }

        return _runtime.BindAddressTokens.Any(StorageServerOptions.IsBindAddressWildcard)
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
                StorageVatpListenerLogMessages.CapacityRejected(_logger, remote, _runtime.Listener.MaxActiveConnections);
                accepted.Dispose();
                continue;
            }

            StorageVatpListenerLogMessages.Accepted(_logger, remote, ActiveConnections);
            var connection = ProcessAsync(accepted, remote, cancellationToken);
            _connections[connection] = 0;
            _ = connection.ContinueWith(
                static (task, state) => ((StorageVatpListenerService)state!)._connections.TryRemove(task, out _),
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
            using var lease = _certificates.Acquire();
            await using var ssl = new SslStream(network, leaveInnerStreamOpen: true);
            var options = new SslServerAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ServerCertificateContext = lease.Context,
            };

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
            await using var transport = new StreamStorageVatpTransport(
                ssl,
                _runtime.Listener.IoProgressTimeout,
                leaveInnerStreamOpen: true);
            await using var vatpSession = new StorageVatpSession(
                transport,
                _openBoundary,
                _runtime.Listener,
                _logger);
            await vatpSession.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AuthenticationException)
        {
            StorageVatpListenerLogMessages.HandshakeFailed(_logger, remote);
        }
        catch (TimeoutException ex)
        {
            StorageVatpListenerLogMessages.ConnectionTimedOut(
                _logger,
                handshakeCompleted ? "io-progress" : ex.Message,
                remote);
        }
        catch (IOException)
        {
        }
        catch (Exception ex)
        {
            StorageVatpListenerLogMessages.ConnectionFailed(_logger, remote, ex.GetType().Name);
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

    private Task DisposeBoundResourcesAsync()
    {
        CloseListenSockets();
        return Task.CompletedTask;
    }
}

/// <summary>Local lifecycle states for the StorageServer VATP listener.</summary>
public enum StorageVatpListenerState
{
    /// <summary>Created but not started.</summary>
    Created = 0,

    /// <summary>Start in progress.</summary>
    Starting = 1,

    /// <summary>Accepting connections.</summary>
    Running = 2,

    /// <summary>Shutdown in progress.</summary>
    Retiring = 3,

    /// <summary>Fully stopped.</summary>
    Stopped = 4,
}
