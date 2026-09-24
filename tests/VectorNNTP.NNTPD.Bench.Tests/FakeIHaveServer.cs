using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VectorNNTP.NNTPD.Bench.Tests;

/// <summary>
/// Loopback NNTP stand-in for IHAVE client tests. Not the production server.
/// </summary>
internal sealed class FakeIHaveServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _clients = [];
    private readonly object _gate = new();
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);

    public FakeIHaveServer(FakeIHaveBehavior behavior)
    {
        Behavior = behavior;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        AcceptLoop = AcceptAsync();
    }

    public FakeIHaveBehavior Behavior { get; }
    public int Port { get; }
    public Task AcceptLoop { get; }
    public int IhaveCommands { get; private set; }
    public int ArticlesReceived { get; private set; }
    public int Replies335 { get; private set; }
    public int Replies235 { get; private set; }
    public int Replies435 { get; private set; }
    public byte[]? LastArticle { get; private set; }
    public string? LastMessageId { get; private set; }

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

                if (!line.StartsWith("IHAVE ", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteLineAsync(stream, "500 Unknown command").ConfigureAwait(false);
                    continue;
                }

                IhaveCommands++;
                LastMessageId = ExtractMessageId(line);
                if (Behavior.RejectDuplicates
                    && LastMessageId is not null
                    && !_seenIds.Add(LastMessageId))
                {
                    Replies435++;
                    await WriteLineAsync(stream, "435 Article not wanted").ConfigureAwait(false);
                    continue;
                }

                if (Behavior.FirstResponse is not null)
                {
                    if (Behavior.FirstResponse.StartsWith("435 ", StringComparison.Ordinal))
                    {
                        Replies435++;
                    }

                    await WriteLineAsync(stream, Behavior.FirstResponse).ConfigureAwait(false);
                    if (!Behavior.FirstResponse.StartsWith("335 ", StringComparison.Ordinal))
                    {
                        continue;
                    }
                }
                else
                {
                    Replies335++;
                    await WriteLineAsync(stream, "335 Send article to be transferred").ConfigureAwait(false);
                }

                LastArticle = await ReadUntilTerminatorAsync(stream, _cts.Token).ConfigureAwait(false);
                ArticlesReceived++;

                var second = Behavior.SecondResponse ?? "235 Article transferred OK";
                if (second.StartsWith("235 ", StringComparison.Ordinal))
                {
                    Replies235++;
                }

                await WriteLineAsync(stream, second).ConfigureAwait(false);
            }
        }
        catch (Exception) when (!_cts.IsCancellationRequested)
        {
            // Client closed or test ended.
        }
    }

    private static string? ExtractMessageId(string command)
    {
        var start = command.IndexOf('<');
        if (start < 0)
        {
            return null;
        }

        var end = command.IndexOf('>', start);
        return end < 0 ? null : command[start..(end + 1)];
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

    private static async Task<byte[]> ReadUntilTerminatorAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var article = new List<byte>(256);
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

            article.Add(one[0]);
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
                return article.ToArray();
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

internal sealed class FakeIHaveBehavior
{
    public string? FirstResponse { get; init; }

    public string? SecondResponse { get; init; }

    public bool RejectDuplicates { get; init; }

    public static FakeIHaveBehavior AcceptAll { get; } = new();

    public static FakeIHaveBehavior RejectNotWanted { get; } = new()
    {
        FirstResponse = "435 Article not wanted",
    };

    public static FakeIHaveBehavior RejectDuplicatesAs435 { get; } = new()
    {
        RejectDuplicates = true,
    };
}
