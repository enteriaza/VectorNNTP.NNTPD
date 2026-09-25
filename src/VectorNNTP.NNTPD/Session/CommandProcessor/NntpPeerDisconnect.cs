using System.Net.Sockets;
using VectorNNTP.NNTPD.Networking.Transport;

namespace VectorNNTP.NNTPD.Session.CommandProcessor;

/// <summary>
/// Classifies transport failures that indicate the peer has already gone away.
/// </summary>
/// <remarks>
/// Used only by the QUIT termination path. Callers must restrict use to that path so
/// unrelated command I/O failures are not reinterpreted as normal disconnects.
/// </remarks>
internal static class NntpPeerDisconnect
{
    /// <summary>
    /// Returns whether <paramref name="exception"/> indicates the peer disconnected or the
    /// connection became terminal in a way expected during QUIT response/close.
    /// </summary>
    public static bool IsPeerDisconnect(Exception exception, INntpConnection connection)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(connection);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SocketException socket when IsResetLike(socket.SocketErrorCode):
                    return true;
                case IOException { InnerException: SocketException nested } when IsResetLike(nested.SocketErrorCode):
                    return true;
                case IOException { Message: not null } io when
                    io.Message.Contains("zero bytes", StringComparison.OrdinalIgnoreCase):
                    return true;
                case ObjectDisposedException when connection.IsCompleted
                    || connection.ConnectionClosed.IsCancellationRequested:
                    return true;
                case InvalidOperationException { Message: not null } ioe when
                    ioe.Message.Contains("writer was completed", StringComparison.OrdinalIgnoreCase)
                    || ioe.Message.Contains("pipe completed", StringComparison.OrdinalIgnoreCase):
                    // Output PipeWriter completed (peer gone / CompleteAsync raced with QUIT flush).
                    return true;
            }
        }

        return false;
    }

    private static bool IsResetLike(SocketError error) =>
        error is SocketError.ConnectionReset
            or SocketError.ConnectionAborted
            or SocketError.Shutdown
            or SocketError.NotConnected
            or SocketError.NetworkReset
            // EPIPE (32) — not always present as SocketError.BrokenPipe on Windows TFMs.
            or (SocketError)32;
}
