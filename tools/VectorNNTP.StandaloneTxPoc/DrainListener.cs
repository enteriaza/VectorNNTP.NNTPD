using System.Net;
using System.Net.Sockets;

namespace VectorNNTP.StandaloneTxPoc;

/// <summary>
/// Raw TCP drain bound to the SPEEDTEST endpoint. Not NNTPD. Does not speak NNTP.
/// </summary>
internal sealed class DrainListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    private DrainListener(TcpListener listener)
    {
        _listener = listener;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public static DrainListener Start(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Cannot bind {address}:{port}. If VectorNNTP.NNTPD is listening, stop it. " +
                "This POC must not send raw bytes to the NNTP server. " +
                ex.Message,
                ex);
        }

        return new DrainListener(listener);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = DrainConnectionAsync(client, cancellationToken);
        }
    }

    private static async Task DrainConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        try
        {
            await using var stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            client.Dispose();
        }
    }
}
