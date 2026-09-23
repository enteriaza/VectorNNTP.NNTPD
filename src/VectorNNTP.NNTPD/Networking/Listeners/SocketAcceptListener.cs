using System.Net;
using System.Net.Sockets;

namespace VectorNNTP.NNTPD.Networking.Listeners;

/// <summary>
/// Owns one listening <see cref="Socket"/> and runs an allocation-efficient asynchronous accept loop.
/// </summary>
/// <remarks>
/// A single failed accepted connection must not stop this listener. The listen socket remains open
/// until <see cref="DisposeAsync"/> / stop.
/// </remarks>
public sealed class SocketAcceptListener : IAsyncDisposable
{
    private readonly ListenBinding _binding;
    private readonly Func<Socket, CancellationToken, ValueTask> _onAccepted;
    private readonly ILogger _logger;
    private readonly Socket _listenSocket;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private int _stopped;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="SocketAcceptListener"/> class.</summary>
    public SocketAcceptListener(
        ListenBinding binding,
        Func<Socket, CancellationToken, ValueTask> onAccepted,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);
        ArgumentNullException.ThrowIfNull(logger);
        _binding = binding;
        _onAccepted = onAccepted;
        _logger = logger;

        var family = binding.Address.AddressFamily;
        _listenSocket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
        if (family == AddressFamily.InterNetworkV6)
        {
            _listenSocket.DualMode = binding.DualMode;
        }

        try
        {
            _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
        catch (SocketException)
        {
            // Best-effort; some platforms differ.
        }
    }

    /// <summary>Gets the local endpoint after <see cref="Start"/> (port may be ephemeral in tests).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listenSocket.LocalEndPoint!;

    /// <summary>Gets the planned binding.</summary>
    public ListenBinding Binding => _binding;

    /// <summary>Binds, listens, and starts the accept loop.</summary>
    public void Start(int backlog = 512)
    {
        _listenSocket.Bind(_binding.EndPoint);
        _listenSocket.Listen(backlog);
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        _logger.LogInformation(
            "NNTP listener started on {EndPoint} (dualMode={DualMode})",
            LocalEndPoint,
            _binding.DualMode);
    }

    /// <summary>Stops accepting and closes the listen socket.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        try
        {
            _listenSocket.Dispose();
        }
        catch
        {
            // Best-effort.
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Accept loop ended with an error during stop");
            }
        }

        _logger.LogInformation("NNTP listener stopped ({Address}/{Port})", _binding.Address, _binding.Port);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await _listenSocket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || _stopped != 0)
            {
                break;
            }
            catch (SocketException ex) when (cancellationToken.IsCancellationRequested || _stopped != 0)
            {
                _logger.LogDebug(ex, "Accept interrupted during listener shutdown");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Accept failed on {EndPoint}; listener continues", _binding.EndPoint);
                continue;
            }

            _ = HandleAcceptedAsync(accepted, cancellationToken);
        }
    }

    private async Task HandleAcceptedAsync(Socket accepted, CancellationToken cancellationToken)
    {
        try
        {
            await _onAccepted(accepted, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                accepted.Dispose();
            }
            catch
            {
                // Best-effort.
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Accepted connection handler failed; listener continues");
            try
            {
                accepted.Dispose();
            }
            catch
            {
                // Best-effort.
            }
        }
    }
}
