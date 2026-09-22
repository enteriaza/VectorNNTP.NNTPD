using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Networking.Certificates;

namespace VectorNNTP.NNTPD.Networking.Transport;

/// <summary>
/// Duplex pipe transport over a plain TCP <see cref="Socket"/> or an authenticated <see cref="SslStream"/>.
/// </summary>
/// <remarks>
/// Ownership: this type owns the accepted socket (and TLS stream when used). The application owns
/// consumption of <see cref="Input"/> / production to <see cref="Output"/>. Pumps stop when the
/// connection is completed, cancelled, or the peer closes.
/// </remarks>
public sealed class NntpConnection : INntpConnection
{
    private readonly ILogger _logger;
    private readonly Pipe _inputPipe;
    private readonly Pipe _outputPipe;
    private readonly CancellationTokenSource _connectionCts = new();
    private readonly object _completeGate = new();
    private Socket? _socket;
    private SslStream? _sslStream;
    /// <summary>
    /// Lease retained for the TLS connection lifetime so rotation cannot dispose the context used by
    /// this connection's <see cref="SslStream"/> while the connection is still alive.
    /// </summary>
    private TlsCertificateLease? _certificateLease;
    private Task? _receiveTask;
    private Task? _sendTask;
    private int _completed;
    private int _disposed;

    private NntpConnection(
        Socket socket,
        EndPoint? remoteEndPoint,
        EndPoint? localEndPoint,
        bool isTls,
        ILogger logger)
    {
        _socket = socket;
        RemoteEndPoint = remoteEndPoint;
        LocalEndPoint = localEndPoint;
        IsTls = isTls;
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
    public bool IsTls { get; }

    /// <inheritdoc />
    public CancellationToken ConnectionClosed => _connectionCts.Token;

    /// <summary>Creates and starts a plain TCP connection transport.</summary>
    public static NntpConnection StartPlain(Socket socket, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(logger);

        ConfigureAcceptedSocket(socket);
        var connection = new NntpConnection(
            socket,
            TryGetRemote(socket),
            TryGetLocal(socket),
            isTls: false,
            logger);
        connection.StartPumps();
        return connection;
    }

    /// <summary>
    /// Performs a server TLS handshake then starts the duplex pipe pumps over the encrypted stream.
    /// </summary>
    /// <remarks>
    /// Acquires a certificate context lease before the handshake and holds it until
    /// <see cref="DisposeAsync"/> so the leased context object outlives handshake-only use and remains
    /// valid across later certificate publication/rotation.
    /// </remarks>
    public static async Task<NntpConnection> StartTlsAsync(
        Socket socket,
        ITlsCertificateContextProvider certificateProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(certificateProvider);
        ArgumentNullException.ThrowIfNull(logger);

        ConfigureAcceptedSocket(socket);
        var remote = TryGetRemote(socket);
        var local = TryGetLocal(socket);
        var connection = new NntpConnection(socket, remote, local, isTls: true, logger);
        // Connection-lifetime lease (not handshake-only); released in DisposeAsync.
        var lease = certificateProvider.Acquire();
        connection._certificateLease = lease;

        // NetworkStream does not own the socket; NntpConnection disposes the socket after SslStream.
        var networkStream = new NetworkStream(socket, ownsSocket: false);
        var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
        connection._sslStream = sslStream;

        try
        {
            var sslOptions = new SslServerAuthenticationOptions
            {
                ServerCertificateContext = lease.Context,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };

            await sslStream.AuthenticateAsServerAsync(sslOptions, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.CompleteAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        connection.StartPumps();
        logger.LogDebug("TLS handshake completed for {Remote}.", remote);
        return connection;
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
                // Observed below via pump logging; swallow for complete.
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
                // Observed below via pump logging; swallow for complete.
            }
        }

        CloseSocketAndTls();
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

    private void StartPumps()
    {
        _receiveTask = IsTls ? ReceiveTlsAsync() : ReceiveSocketAsync();
        _sendTask = IsTls ? SendTlsAsync() : SendSocketAsync();
        _ = ObservePumpAsync(_receiveTask, "receive");
        _ = ObservePumpAsync(_sendTask, "send");
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
            _ = CompleteAsync(error);
        }
    }

    private async Task ReceiveSocketAsync()
    {
        var socket = _socket ?? throw new InvalidOperationException("Socket missing.");
        var writer = _inputPipe.Writer;
        var token = _connectionCts.Token;

        while (!token.IsCancellationRequested)
        {
            var memory = writer.GetMemory(NntpPipeOptions.MinimumSegmentSize);
            var bytes = await socket
                .ReceiveAsync(memory, SocketFlags.None, token)
                .ConfigureAwait(false);
            if (bytes == 0)
            {
                break;
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

    private async Task SendSocketAsync()
    {
        var socket = _socket ?? throw new InvalidOperationException("Socket missing.");
        var reader = _outputPipe.Reader;
        var token = _connectionCts.Token;

        while (!token.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(token).ConfigureAwait(false);
            var buffer = result.Buffer;
            // Advance only what was fully transmitted; on failure leave bytes unconsumed.
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

                    await SocketPayloadSender.SendAllAsync(socket, segment, token).ConfigureAwait(false);
                }

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
        try
        {
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
            // Peer may already be gone.
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }
    }

    private async Task ReceiveTlsAsync()
    {
        var ssl = _sslStream ?? throw new InvalidOperationException("SslStream missing.");
        var writer = _inputPipe.Writer;
        var token = _connectionCts.Token;

        while (!token.IsCancellationRequested)
        {
            var memory = writer.GetMemory(NntpPipeOptions.MinimumSegmentSize);
            var bytes = await ssl.ReadAsync(memory, token).ConfigureAwait(false);
            if (bytes == 0)
            {
                break;
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

    private async Task SendTlsAsync()
    {
        var ssl = _sslStream ?? throw new InvalidOperationException("SslStream missing.");
        var reader = _outputPipe.Reader;
        var token = _connectionCts.Token;

        while (!token.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(token).ConfigureAwait(false);
            var buffer = result.Buffer;
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

                    // WriteAsync encrypts and writes ciphertext to the inner NetworkStream.
                    // NetworkStream does not buffer (Flush/FlushAsync are no-ops), so an explicit
                    // SslStream.FlushAsync after each pipe batch is redundant for this stack.
                    await ssl.WriteAsync(segment, token).ConfigureAwait(false);
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.End);
            }

            if (result.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync().ConfigureAwait(false);
    }

    private void CloseSocketAndTls()
    {
        lock (_completeGate)
        {
            var ssl = _sslStream;
            _sslStream = null;
            if (ssl is not null)
            {
                try
                {
                    ssl.Dispose();
                }
                catch
                {
                    // Best-effort.
                }
            }

            var socket = _socket;
            _socket = null;
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
    }

    private static void ConfigureAcceptedSocket(Socket socket)
    {
        try
        {
            socket.NoDelay = true;
        }
        catch (SocketException)
        {
            // Some platforms may reject; proceed.
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
