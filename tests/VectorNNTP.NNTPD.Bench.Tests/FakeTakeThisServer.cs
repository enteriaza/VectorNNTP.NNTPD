using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VectorNNTP.NNTPD.Bench.Tests;

/// <summary>
/// Loopback NNTP stand-in for TAKETHIS client tests. Not the production server.
/// </summary>
internal sealed class FakeTakeThisServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _clients = [];
    private readonly object _gate = new();

    public FakeTakeThisServer(FakeTakeThisBehavior behavior)
    {
        Behavior = behavior;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        AcceptLoop = AcceptAsync();
    }

    public FakeTakeThisBehavior Behavior { get; }
    public int Port { get; }
    public Task AcceptLoop { get; }
    public int ArticlesReceived => _articlesReceived;
    public int MaxUnreadArticles { get; private set; }
    public int Replies239 => _replies239;
    public int Replies439 => _replies439;
    private int _articlesReceived;
    private int _replies239;
    private int _replies439;

    private async Task AcceptAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    _clients.Add(ServeAsync(client));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Listener stopped.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        await using var stream = client.GetStream();
        var unread = 0;
        try
        {
            await WriteLineAsync(stream, "201 Fake NNTP ready").ConfigureAwait(false);

            while (!_cts.IsCancellationRequested)
            {
                var line = await ReadLineAsync(stream, _cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (line.StartsWith("MODE STREAM", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteLineAsync(stream, "203 Streaming permitted").ConfigureAwait(false);
                    continue;
                }

                if (line.StartsWith("TAKETHIS ", StringComparison.OrdinalIgnoreCase))
                {
                    await ReadUntilTerminatorAsync(stream, _cts.Token).ConfigureAwait(false);
                    var received = Interlocked.Increment(ref _articlesReceived);
                    unread++;
                    if (unread > MaxUnreadArticles)
                    {
                        MaxUnreadArticles = unread;
                    }

                    if (Behavior.HoldUntil is int hold && received < hold)
                    {
                        continue;
                    }

                    var reply = Behavior.ReplyFor(received, line);
                    await WriteLineAsync(stream, reply).ConfigureAwait(false);
                    if (reply.StartsWith("239 ", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _replies239);
                    }
                    else if (reply.StartsWith("439 ", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _replies439);
                    }

                    unread = Math.Max(0, unread - 1);
                    continue;
                }

                await WriteLineAsync(stream, "500 Unknown command").ConfigureAwait(false);
            }
        }
        catch (Exception) when (!_cts.IsCancellationRequested)
        {
            // Client closed or test ended.
        }
    }

    private static async Task WriteLineAsync(NetworkStream stream, string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(64);
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.Count == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());
            }

            if (one[0] == (byte)'\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r')
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }

                return Encoding.ASCII.GetString(buffer.ToArray());
            }

            buffer.Add(one[0]);
        }
    }

    private static async Task ReadUntilTerminatorAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var window = new byte[5];
        var filled = 0;
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Closed before article terminator.");
            }

            if (filled < window.Length)
            {
                window[filled++] = one[0];
            }
            else
            {
                Buffer.BlockCopy(window, 1, window, 0, window.Length - 1);
                window[^1] = one[0];
            }

            if (filled == window.Length
                && window[0] == (byte)'\r'
                && window[1] == (byte)'\n'
                && window[2] == (byte)'.'
                && window[3] == (byte)'\r'
                && window[4] == (byte)'\n')
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await AcceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        Task[] clients;
        lock (_gate)
        {
            clients = _clients.ToArray();
        }

        try
        {
            await Task.WhenAll(clients).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }

        _cts.Dispose();
    }
}

internal sealed class FakeTakeThisBehavior
{
    public int? HoldUntil { get; init; }
    public Func<int, string, string> ReplyFor { get; init; } = static (_, command) =>
    {
        var idStart = command.IndexOf('<');
        var id = idStart >= 0 ? command[idStart..] : "<missing@vectornntp.local>";
        return "239 " + id;
    };

    public static FakeTakeThisBehavior AcceptAll { get; } = new();

    public static FakeTakeThisBehavior RejectAll { get; } = new()
    {
        ReplyFor = static (_, command) =>
        {
            var idStart = command.IndexOf('<');
            var id = idStart >= 0 ? command[idStart..] : "<missing@vectornntp.local>";
            return "439 " + id;
        },
    };

    public static FakeTakeThisBehavior ProtocolError { get; } = new()
    {
        ReplyFor = static (_, _) => "500 Unexpected",
    };
}
