using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VectorNNTP.NNTPD.Bench.Tests;

/// <summary>Loopback NNTP stand-in for POST client tests. Not the production server.</summary>
internal sealed class FakePostServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _clients = [];
    private readonly object _gate = new();

    public FakePostServer(FakePostBehavior behavior)
    {
        Behavior = behavior;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        AcceptLoop = AcceptAsync();
    }

    public FakePostBehavior Behavior { get; }
    public int Port { get; }
    public Task AcceptLoop { get; }
    public int Posts { get; private set; }
    public int ArticlesReceived { get; private set; }
    public int Replies240 { get; private set; }
    public int Replies441 { get; private set; }
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

                if (line.StartsWith("AUTHINFO USER", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteLineAsync(stream, "381 Password required").ConfigureAwait(false);
                    continue;
                }

                if (line.StartsWith("AUTHINFO PASS", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteLineAsync(stream, "281 Authentication accepted").ConfigureAwait(false);
                    continue;
                }

                if (line.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    Posts++;
                    await WriteLineAsync(stream, "340 Input article; end with <CR-LF>.<CR-LF>")
                        .ConfigureAwait(false);
                    var article = await ReadArticleAsync(stream, _cts.Token).ConfigureAwait(false);
                    ArticlesReceived++;
                    LastMessageId = ExtractMessageId(article);
                    if (Behavior == FakePostBehavior.RejectAll)
                    {
                        Replies441++;
                        await WriteLineAsync(stream, "441 Posting failed").ConfigureAwait(false);
                    }
                    else
                    {
                        Replies240++;
                        await WriteLineAsync(stream, "240 Article received OK").ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client or listener stopped.
        }
    }

    private static string? ExtractMessageId(byte[] article)
    {
        var text = Encoding.ASCII.GetString(article);
        const string prefix = "Message-ID: ";
        var start = text.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += prefix.Length;
        var end = text.IndexOf("\r\n", start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static async Task<byte[]> ReadArticleAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(4096);
        var window = new byte[5];
        var filled = 0;
        var scratch = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(scratch, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("Fake POST server: client closed during article.");
            }

            buffer.Add(scratch[0]);
            if (filled < window.Length)
            {
                window[filled++] = scratch[0];
            }
            else
            {
                Buffer.BlockCopy(window, 1, window, 0, window.Length - 1);
                window[^1] = scratch[0];
            }

            if (filled == window.Length && window.AsSpan().SequenceEqual("\r\n.\r\n"u8))
            {
                return buffer.ToArray();
            }
        }
    }

    private static async Task WriteLineAsync(NetworkStream stream, string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var scratch = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(scratch, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return sb.Length == 0 ? null : sb.ToString();
            }

            if (scratch[0] == (byte)'\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r')
                {
                    sb.Length--;
                }

                return sb.ToString();
            }

            sb.Append((char)scratch[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        Task[] clients;
        lock (_gate)
        {
            clients = [.. _clients];
        }

        try
        {
            await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }

        _cts.Dispose();
    }
}

internal enum FakePostBehavior
{
    AcceptAll,
    RejectAll,
}
