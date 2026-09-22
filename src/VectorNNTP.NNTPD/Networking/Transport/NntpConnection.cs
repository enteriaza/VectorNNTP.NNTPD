using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Proxy;

namespace VectorNNTP.NNTPD.Networking.Transport;

/// <summary>
/// Duplex pipe transport over a replaceable byte-stream I/O owner (<see cref="ConnectionByteTransport"/>).
/// </summary>
/// <remarks>
/// <para>
/// Ownership: this type owns the accepted socket and the <see cref="ConnectionByteTransport"/> (plain
/// <see cref="NetworkStream"/> or authenticated <see cref="SslStream"/>). The application owns
/// consumption of <see cref="Input"/> / production to <see cref="Output"/>.
/// </para>
/// <para>
/// Plain connections may call <see cref="UpgradeToTlsAsync"/> to authenticate TLS on the same socket.
/// Plain or TLS connections may call <see cref="UpgradeToDeflateAsync"/> to activate bidirectional raw
/// DEFLATE above the current byte stream (RFC 8054 layering: NNTP → DEFLATE → TLS → TCP).
/// Pumps keep running across upgrades; exclusive stream ownership is enforced by transport quiescence.
/// The NNTP STARTTLS and COMPRESS commands are not implemented here.
/// </para>
/// <para>
/// NNTPD performs server-side TLS authentication only. Client certificates are never requested or validated.
/// </para>
/// </remarks>
public sealed class NntpConnection : INntpConnection
{
    private const int ModePlain = 0;
    private const int ModeUpgrading = 1;
    private const int ModeTls = 2;

    private const int CompressionOff = 0;
    private const int CompressionUpgrading = 1;
    private const int CompressionOn = 2;

    private readonly ILogger _logger;
    private readonly Pipe _inputPipe;
    private readonly Pipe _outputPipe;
    private readonly CancellationTokenSource _connectionCts = new();
    private readonly object _completeGate = new();
    private readonly ConnectionClientIdentity _clientIdentity;
    private Socket? _socket;
    private ConnectionByteTransport? _transport;
    private TlsCertificateLease? _certificateLease;
    private byte[]? _receivePrefix;
    private Task? _receiveTask;
    private Task? _sendTask;
    private int _mode;
    private int _compression;
    private int _inplaceUpgradeBusy;
    private int _completed;
    private int _disposed;
    private int _sendPumpAwaitingOutput;
    private long _outboundIdleVersion;
    private TaskCompletionSource? _outboundIdleWaiter;

    private NntpConnection(
        Socket socket,
        ConnectionByteTransport transport,
        EndPoint? remoteEndPoint,
        EndPoint? localEndPoint,
        bool isTls,
        ConnectionClientIdentity clientIdentity,
        ILogger logger)
    {
        _socket = socket;
        _transport = transport;
        RemoteEndPoint = remoteEndPoint;
        LocalEndPoint = localEndPoint;
        _mode = isTls ? ModeTls : ModePlain;
        _clientIdentity = clientIdentity;
        _logger = logger;
        var options = NntpPipeOptions.Create();
        _inputPipe = new Pipe(options);
        _outputPipe = new Pipe(options);
    }

    /// <inheritdoc />
    public PipeReader Input => _inputPipe.Reader;

    /// <inheritdoc />
    public PipeWriter Output => _outputPipe.Writer;

    /// <inheritdoc />
    public EndPoint? RemoteEndPoint { get; }

    /// <inheritdoc />
    public EndPoint? LocalEndPoint { get; }

    /// <inheritdoc />
    public ConnectionClientIdentity ClientIdentity => _clientIdentity;

    /// <inheritdoc />
    public bool IsTls => Volatile.Read(ref _mode) == ModeTls;

    /// <inheritdoc />
    public bool IsCompressed => Volatile.Read(ref _compression) == CompressionOn;

    /// <inheritdoc />
    public long OutboundIdleVersion => Volatile.Read(ref _outboundIdleVersion);

    /// <inheritdoc />
    public bool IsCompleted => Volatile.Read(ref _completed) == 1;

    /// <inheritdoc />
    public Task WaitForOutboundDeliveryAsync(
        long outboundIdleVersionBeforeFlush,
        CancellationToken cancellationToken = default) =>
        WaitForOutboundIdleAfterAsync(outboundIdleVersionBeforeFlush, cancellationToken);

    /// <inheritdoc />
    public async Task WaitForOutboundDeliveryAndPauseReadsAsync(
        long outboundIdleVersionBeforeFlush,
        CancellationToken cancellationToken = default)
    {
        var transport = _transport ?? throw new ObjectDisposedException(nameof(NntpConnection));
        await WaitForOutboundIdleAfterAsync(outboundIdleVersionBeforeFlush, cancellationToken)
            .ConfigureAwait(false);
        await transport.PauseReadsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public CancellationToken ConnectionClosed => _connectionCts.Token;

    /// <summary>Creates and starts a plain TCP connection transport.</summary>
    public static NntpConnection StartPlain(
        Socket socket,
        ConnectionClientIdentity clientIdentity,
        ILogger logger,
        ReadOnlyMemory<byte> receivePrefix = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(clientIdentity);
        ArgumentNullException.ThrowIfNull(logger);

        ConfigureAcceptedSocket(socket);
        var network = new NetworkStream(socket, ownsSocket: false);
        var transport = new ConnectionByteTransport(network, isTls: false);
        var connection = new NntpConnection(
            socket,
            transport,
            TryGetRemote(socket),
            TryGetLocal(socket),
            isTls: false,
            clientIdentity,
            logger);
        if (!receivePrefix.IsEmpty)
        {
            connection._receivePrefix = receivePrefix.ToArray();
        }

        connection.StartPumps();
        return connection;
    }

    /// <summary>
    /// Performs a server TLS handshake then starts the duplex pipe pumps over the encrypted stream.
    /// </summary>
    public static async Task<NntpConnection> StartTlsAsync(
        Socket socket,
        ITlsCertificateContextProvider certificateProvider,
        ConnectionClientIdentity clientIdentity,
        ILogger logger,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte> tlsPrefix = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(certificateProvider);
        ArgumentNullException.ThrowIfNull(clientIdentity);
        ArgumentNullException.ThrowIfNull(logger);

        ConfigureAcceptedSocket(socket);
        var remote = TryGetRemote(socket);
        var local = TryGetLocal(socket);
        var lease = certificateProvider.Acquire();

        var networkStream = new NetworkStream(socket, ownsSocket: false);
        Stream sslInner = tlsPrefix.IsEmpty
            ? networkStream
            : new PrefixedStream(networkStream, tlsPrefix, leaveInnerOpen: false);
        var sslStream = new SslStream(sslInner, leaveInnerStreamOpen: false);

        try
        {
            await sslStream
                .AuthenticateAsServerAsync(CreateServerAuthenticationOptions(lease.Context), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            lease.Dispose();
            socket.Dispose();
            throw;
        }

        var transport = new ConnectionByteTransport(sslStream, isTls: true);
        var connection = new NntpConnection(socket, transport, remote, local, isTls: true, clientIdentity, logger)
        {
            _certificateLease = lease,
        };

        connection.StartPumps();
        logger.LogDebug("TLS handshake completed for {Remote}.", remote);
        return connection;
    }

    /// <inheritdoc />
    public async Task UpgradeToTlsAsync(
        ITlsCertificateContextProvider certificateProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificateProvider);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (Volatile.Read(ref _completed) == 1)
        {
            throw new ObjectDisposedException(nameof(NntpConnection), "Connection is closed.");
        }

        if (Volatile.Read(ref _mode) == ModeTls)
        {
            throw new InvalidOperationException("Connection is already TLS-protected.");
        }

        if (Volatile.Read(ref _compression) != CompressionOff)
        {
            throw new InvalidOperationException("Cannot negotiate TLS after DEFLATE is active.");
        }

        if (Interlocked.CompareExchange(ref _inplaceUpgradeBusy, 1, 0) != 0)
        {
            throw new InvalidOperationException("A transport layer upgrade is already in progress.");
        }

        var prior = Interlocked.CompareExchange(ref _mode, ModeUpgrading, ModePlain);
        if (prior != ModePlain)
        {
            Volatile.Write(ref _inplaceUpgradeBusy, 0);
            if (prior == ModeUpgrading)
            {
                throw new InvalidOperationException("A TLS upgrade is already in progress.");
            }

            if (prior == ModeTls)
            {
                throw new InvalidOperationException("Connection is already TLS-protected.");
            }

            throw new InvalidOperationException("Connection cannot be upgraded in its current state.");
        }

        if (Volatile.Read(ref _compression) != CompressionOff)
        {
            Volatile.Write(ref _mode, ModePlain);
            Volatile.Write(ref _inplaceUpgradeBusy, 0);
            throw new InvalidOperationException("Cannot negotiate TLS after DEFLATE is active.");
        }

        var transport = _transport ?? throw new ObjectDisposedException(nameof(NntpConnection));
        Exception? failure = null;
        var quiesced = false;
        var preconditionFailed = false;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _connectionCts.Token);

            // Reads may already be paused by WaitForOutboundDeliveryAndPauseReadsAsync (STARTTLS).
            if (!transport.IsReadsPaused)
            {
                await transport.PauseReadsAsync(linked.Token).ConfigureAwait(false);
            }

            await WaitForOutboundIdleAsync(linked.Token).ConfigureAwait(false);
            await transport.QuiesceWritesAsync(linked.Token).ConfigureAwait(false);
            quiesced = true;

            try
            {
                EnsureApplicationInputDrainedForUpgrade();
            }
            catch (InvalidOperationException)
            {
                preconditionFailed = true;
                transport.ResumeFromQuiesceWithoutUpgrade();
                quiesced = false;
                Volatile.Write(ref _mode, ModePlain);
                throw;
            }

            var lease = certificateProvider.Acquire();
            var plainStream = transport.TakeQuiescedStreamForTlsWrap();
            var upgradePrefix = transport.TakePendingUpgradePrefix();
            Stream sslInner = upgradePrefix.IsEmpty
                ? plainStream
                : new PrefixedStream(plainStream, upgradePrefix, leaveInnerOpen: false);
            var sslStream = new SslStream(sslInner, leaveInnerStreamOpen: false);

            try
            {
                await sslStream
                    .AuthenticateAsServerAsync(CreateServerAuthenticationOptions(lease.Context), linked.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    await sslStream.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort; disposes plain NetworkStream as well.
                }

                lease.Dispose();
                failure = ex;
                throw;
            }

            _certificateLease = lease;
            transport.PublishTlsAndResume(sslStream);
            Volatile.Write(ref _mode, ModeTls);
            _logger.LogDebug("In-place TLS upgrade completed for {Remote}.", RemoteEndPoint);
        }
        catch (Exception ex) when (failure is null && !preconditionFailed)
        {
            failure = ex;
            throw;
        }
        finally
        {
            if (failure is not null)
            {
                // Mark terminal and cancel ConnectionClosed before waking pumps so the session
                // dispatcher cannot write a secondary NNTP status onto a dying transport.
                if (Volatile.Read(ref _completed) == 0)
                {
                    await CompleteAsync(failure).ConfigureAwait(false);
                }

                if (quiesced)
                {
                    transport.AbortQuiesceForConnectionTeardown();
                }
                else if (Volatile.Read(ref _mode) == ModeUpgrading)
                {
                    Volatile.Write(ref _mode, ModePlain);
                }

                Volatile.Write(ref _inplaceUpgradeBusy, 0);
            }
            else
            {
                Volatile.Write(ref _inplaceUpgradeBusy, 0);
            }
        }
    }

    /// <inheritdoc />
    public async Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (Volatile.Read(ref _completed) == 1)
        {
            throw new ObjectDisposedException(nameof(NntpConnection), "Connection is closed.");
        }

        if (Volatile.Read(ref _compression) == CompressionOn)
        {
            throw new InvalidOperationException("Connection is already DEFLATE-compressed.");
        }

        if (Interlocked.CompareExchange(ref _inplaceUpgradeBusy, 1, 0) != 0)
        {
            throw new InvalidOperationException("A transport layer upgrade is already in progress.");
        }

        var prior = Interlocked.CompareExchange(ref _compression, CompressionUpgrading, CompressionOff);
        if (prior != CompressionOff)
        {
            Volatile.Write(ref _inplaceUpgradeBusy, 0);
            if (prior == CompressionUpgrading)
            {
                throw new InvalidOperationException("A DEFLATE upgrade is already in progress.");
            }

            throw new InvalidOperationException("Connection is already DEFLATE-compressed.");
        }

        if (Volatile.Read(ref _mode) == ModeUpgrading)
        {
            Volatile.Write(ref _compression, CompressionOff);
            Volatile.Write(ref _inplaceUpgradeBusy, 0);
            throw new InvalidOperationException("A TLS upgrade is already in progress.");
        }

        var transport = _transport ?? throw new ObjectDisposedException(nameof(NntpConnection));
        Exception? failure = null;
        var quiesced = false;
        var preconditionFailed = false;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _connectionCts.Token);

            if (!transport.IsReadsPaused)
            {
                await transport.PauseReadsAsync(linked.Token).ConfigureAwait(false);
            }

            await WaitForOutboundIdleAsync(linked.Token).ConfigureAwait(false);
            await transport.QuiesceWritesAsync(linked.Token).ConfigureAwait(false);
            quiesced = true;

            try
            {
                EnsureApplicationInputDrainedForUpgrade();
            }
            catch (InvalidOperationException)
            {
                preconditionFailed = true;
                transport.ResumeFromQuiesceWithoutUpgrade();
                quiesced = false;
                Volatile.Write(ref _compression, CompressionOff);
                throw;
            }

            var inner = transport.TakeQuiescedStreamForDeflateWrap();
            var upgradePrefix = transport.TakePendingUpgradePrefix();
            Stream deflateInner = upgradePrefix.IsEmpty
                ? inner
                : new PrefixedStream(inner, upgradePrefix, leaveInnerOpen: false);
            var deflate = new NntpDeflateStream(deflateInner);
            transport.PublishDeflateAndResume(deflate);
            Volatile.Write(ref _compression, CompressionOn);
            _logger.LogDebug("In-place DEFLATE compression activated for {Remote}.", RemoteEndPoint);
        }
        catch (Exception ex) when (!preconditionFailed)
        {
            failure = ex;
            throw;
        }
        finally
        {
            if (failure is not null)
            {
                if (Volatile.Read(ref _completed) == 0)
                {
                    await CompleteAsync(failure).ConfigureAwait(false);
                }

                if (quiesced)
                {
                    transport.AbortQuiesceForConnectionTeardown();
                }
                else if (Volatile.Read(ref _compression) == CompressionUpgrading)
                {
                    Volatile.Write(ref _compression, CompressionOff);
                }
            }

            Volatile.Write(ref _inplaceUpgradeBusy, 0);
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1)
        {
            return;
        }

        try
        {
            await _connectionCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        try
        {
            await _inputPipe.Writer.CompleteAsync(exception).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            await _outputPipe.Writer.CompleteAsync(exception).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            await _inputPipe.Reader.CompleteAsync(exception).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            await _outputPipe.Reader.CompleteAsync(exception).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort.
        }

        var receive = _receiveTask;
        var send = _sendTask;
        if (receive is not null)
        {
            try
            {
                await receive.ConfigureAwait(false);
            }
            catch
            {
                // Observed via pump logging.
            }
        }

        if (send is not null)
        {
            try
            {
                await send.ConfigureAwait(false);
            }
            catch
            {
                // Observed via pump logging.
            }
        }

        await CloseTransportSocketAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await CompleteAsync().ConfigureAwait(false);
        _connectionCts.Dispose();
        _certificateLease?.Dispose();
        _certificateLease = null;
    }

    /// <summary>Shared server TLS options (server auth only; no client certificates).</summary>
    internal static SslServerAuthenticationOptions CreateServerAuthenticationOptions(
        SslStreamCertificateContext certificateContext) =>
        new()
        {
            ServerCertificateContext = certificateContext,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };

    private void StartPumps()
    {
        _receiveTask = ReceiveAsync();
        _sendTask = SendAsync();
        _ = ObservePumpAsync(_receiveTask, "receive");
        _ = ObservePumpAsync(_sendTask, "send");
    }

    private void EnsureApplicationInputDrainedForUpgrade()
    {
        if (!_inputPipe.Reader.TryRead(out var result))
        {
            return;
        }

        try
        {
            if (!result.Buffer.IsEmpty)
            {
                throw new InvalidOperationException(
                    "Cannot upgrade to TLS while unconsumed plaintext remains in Input. " +
                    "Drain application data before calling UpgradeToTlsAsync.");
            }
        }
        finally
        {
            // Leave unconsumed bytes in place for the caller (do not examine past start on failure).
            _inputPipe.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.Start);
        }
    }

    private async Task ObservePumpAsync(Task pump, string name)
    {
        Exception? error = null;
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_connectionCts.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            error = ex;
            _logger.LogDebug(ex, "NNTP connection {Pump} pump ended with an error.", name);
        }
        finally
        {
            if (Volatile.Read(ref _completed) == 0)
            {
                _ = CompleteAsync(error);
            }
        }
    }

    private async Task WaitForOutboundIdleAsync(CancellationToken cancellationToken) =>
        await WaitForOutboundIdleAfterAsync(Volatile.Read(ref _outboundIdleVersion) - 1, cancellationToken)
            .ConfigureAwait(false);

    private async Task WaitForOutboundIdleAfterAsync(
        long outboundIdleVersionBeforeFlush,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Volatile.Read(ref _sendPumpAwaitingOutput) == 1
                && Volatile.Read(ref _outboundIdleVersion) > outboundIdleVersionBeforeFlush)
            {
                return;
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _outboundIdleWaiter, waiter);

            if (Volatile.Read(ref _sendPumpAwaitingOutput) == 1
                && Volatile.Read(ref _outboundIdleVersion) > outboundIdleVersionBeforeFlush)
            {
                Volatile.Write(ref _outboundIdleWaiter, null);
                waiter.TrySetResult();
                return;
            }

            await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void SignalOutboundIdle()
    {
        Interlocked.Increment(ref _outboundIdleVersion);
        Volatile.Write(ref _sendPumpAwaitingOutput, 1);
        var waiter = Interlocked.Exchange(ref _outboundIdleWaiter, null);
        waiter?.TrySetResult();
    }

    private void SignalOutboundBusy() => Volatile.Write(ref _sendPumpAwaitingOutput, 0);

    private async Task ReceiveAsync()
    {
        var transport = _transport ?? throw new InvalidOperationException("Transport missing.");
        var writer = _inputPipe.Writer;
        var token = _connectionCts.Token;

        var prefix = Interlocked.Exchange(ref _receivePrefix, null);
        if (prefix is { Length: > 0 })
        {
            var memory = writer.GetMemory(prefix.Length);
            prefix.CopyTo(memory);
            writer.Advance(prefix.Length);
            var prefixFlush = await writer.FlushAsync(token).ConfigureAwait(false);
            if (prefixFlush.IsCompleted || prefixFlush.IsCanceled)
            {
                await writer.CompleteAsync().ConfigureAwait(false);
                return;
            }
        }

        while (!token.IsCancellationRequested)
        {
            var memory = writer.GetMemory(NntpPipeOptions.MinimumSegmentSize);
            var bytes = await transport.ReadAsync(memory, token).ConfigureAwait(false);
            if (bytes == 0)
            {
                break;
            }

            // If Quiesce completed while this read returned, octets belong to the upgrade wrap
            // (e.g. TLS ClientHello), not the application Input pipe.
            if (!transport.TryCommitReadToApplication(memory.Span[..bytes]))
            {
                continue;
            }

            writer.Advance(bytes);
            var flush = await writer.FlushAsync(token).ConfigureAwait(false);
            if (flush.IsCompleted || flush.IsCanceled)
            {
                break;
            }
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }

    private async Task SendAsync()
    {
        var transport = _transport ?? throw new InvalidOperationException("Transport missing.");
        var reader = _outputPipe.Reader;
        var token = _connectionCts.Token;
        var socket = _socket;

        while (!token.IsCancellationRequested)
        {
            SignalOutboundIdle();
            var result = await reader.ReadAsync(token).ConfigureAwait(false);
            SignalOutboundBusy();
            var buffer = result.Buffer;
            var consumed = buffer.Start;
            try
            {
                if (buffer.IsEmpty && result.IsCompleted)
                {
                    break;
                }

                foreach (var segment in buffer)
                {
                    if (segment.Length == 0)
                    {
                        continue;
                    }

                    await transport.WriteAsync(segment, token).ConfigureAwait(false);
                }

                // Required for DEFLATE: sync-flush compressed bytes to the peer without ending the stream.
                // No-op / cheap for NetworkStream and SslStream over NetworkStream.
                await transport.FlushAsync(token).ConfigureAwait(false);

                consumed = buffer.End;
            }
            finally
            {
                reader.AdvanceTo(consumed);
            }

            if (result.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync().ConfigureAwait(false);

        // Half-close TCP send only for plaintext end-of-stream; TLS shutdown is via SslStream dispose.
        if (socket is not null && !IsTls)
        {
            try
            {
                socket.Shutdown(SocketShutdown.Send);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task CloseTransportSocketAsync()
    {
        ConnectionByteTransport? transport;
        Socket? socket;
        lock (_completeGate)
        {
            transport = _transport;
            _transport = null;
            socket = _socket;
            _socket = null;
        }

        if (transport is not null)
        {
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort.
            }
        }

        if (socket is not null)
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

    private static void ConfigureAcceptedSocket(Socket socket)
    {
        try
        {
            socket.NoDelay = true;
        }
        catch (SocketException)
        {
        }
    }

    private static EndPoint? TryGetRemote(Socket socket)
    {
        try
        {
            return socket.RemoteEndPoint;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static EndPoint? TryGetLocal(Socket socket)
    {
        try
        {
            return socket.LocalEndPoint;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }
}
