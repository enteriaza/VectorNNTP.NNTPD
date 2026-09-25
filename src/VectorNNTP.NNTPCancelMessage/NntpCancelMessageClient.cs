using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace VectorNNTP.NNTPCancelMessage;

/// <summary>Result of a HEAD command.</summary>
internal sealed class HeadExchange
{
    public required int Code { get; init; }

    public required string StatusLine { get; init; }

    public IReadOnlyList<string> Headers { get; init; } = [];

    public bool Success => Code == 221;

    public bool NotFound => Code == 430;
}

/// <summary>Result of a POST command.</summary>
internal sealed class PostExchange
{
    public required int Code { get; init; }

    public required string StatusLine { get; init; }

    public bool Success => Code == 240;
}

/// <summary>Minimal NNTP client used only by the newsmaster utility (HEAD / AUTHINFO / POST).</summary>
internal sealed class NntpCancelMessageClient : IAsyncDisposable
{
    private Stream? _stream;
    private TcpClient? _tcp;

    public async Task ConnectAsync(AdminSettings settings, CancellationToken cancellationToken)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(settings.Host, settings.Port, cancellationToken).ConfigureAwait(false);
        Stream stream = _tcp.GetStream();
        if (settings.UseTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = settings.Host },
                    cancellationToken)
                .ConfigureAwait(false);
            stream = ssl;
        }

        _stream = stream;
        var greeting = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (greeting.Length < 3 || greeting[0] is not ((byte)'2'))
        {
            throw new IOException("NNTP greeting was not a 2xx welcome: " + Encoding.ASCII.GetString(greeting));
        }
    }

    public async Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        await WriteAsciiAsync("AUTHINFO USER " + username + "\r\n", cancellationToken).ConfigureAwait(false);
        var userReply = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!StartsWithCode(userReply, 381))
        {
            throw new NntpCancelMessageAuthenticationException("AUTHINFO USER failed: " + Encoding.ASCII.GetString(userReply));
        }

        await WriteAsciiAsync("AUTHINFO PASS " + password + "\r\n", cancellationToken).ConfigureAwait(false);
        var passReply = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!StartsWithCode(passReply, 281))
        {
            throw new NntpCancelMessageAuthenticationException("AUTHINFO PASS failed.");
        }
    }

    public async Task<HeadExchange> HeadAsync(string messageId, CancellationToken cancellationToken)
    {
        await WriteAsciiAsync("HEAD " + messageId + "\r\n", cancellationToken).ConfigureAwait(false);
        var status = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var code = ParseCode(status);
        if (code != 221)
        {
            return new HeadExchange { Code = code, StatusLine = Encoding.ASCII.GetString(status) };
        }

        var headers = new List<string>();
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line.Length == 1 && line[0] == (byte)'.')
            {
                break;
            }

            if (line.Length >= 2 && line[0] == (byte)'.' && line[1] == (byte)'.')
            {
                line = line[1..];
            }

            headers.Add(Encoding.ASCII.GetString(line));
        }

        return new HeadExchange
        {
            Code = 221,
            StatusLine = Encoding.ASCII.GetString(status),
            Headers = headers,
        };
    }

    public async Task<PostExchange> PostAsync(string article, CancellationToken cancellationToken)
    {
        await WriteAsciiAsync("POST\r\n", cancellationToken).ConfigureAwait(false);
        var ready = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!StartsWithCode(ready, 340))
        {
            return new PostExchange
            {
                Code = ParseCode(ready),
                StatusLine = Encoding.ASCII.GetString(ready),
            };
        }

        await WriteAsciiAsync(article, cancellationToken).ConfigureAwait(false);
        if (!article.EndsWith("\r\n", StringComparison.Ordinal))
        {
            await WriteAsciiAsync("\r\n", cancellationToken).ConfigureAwait(false);
        }

        await WriteAsciiAsync(".\r\n", cancellationToken).ConfigureAwait(false);
        var done = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return new PostExchange
        {
            Code = ParseCode(done),
            StatusLine = Encoding.ASCII.GetString(done),
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            try
            {
                await WriteAsciiAsync("QUIT\r\n", CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }

            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _tcp?.Dispose();
    }

    private async Task WriteAsciiAsync(string text, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Not connected.");
        }

        var bytes = Encoding.ASCII.GetBytes(text);
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Not connected.");
        }

        var buffer = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            var read = await _stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Connection closed while reading an NNTP line.");
            }

            if (one[0] == (byte)'\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r')
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }

                return buffer.ToArray();
            }

            buffer.Add(one[0]);
            if (buffer.Count > 8192)
            {
                throw new IOException("NNTP line exceeded 8192 octets.");
            }
        }
    }

    private static int ParseCode(ReadOnlySpan<byte> line)
    {
        if (line.Length < 3
            || line[0] is < (byte)'0' or > (byte)'9'
            || line[1] is < (byte)'0' or > (byte)'9'
            || line[2] is < (byte)'0' or > (byte)'9')
        {
            return 0;
        }

        return ((line[0] - '0') * 100) + ((line[1] - '0') * 10) + (line[2] - '0');
    }

    private static bool StartsWithCode(ReadOnlySpan<byte> line, int code) => ParseCode(line) == code;
}

/// <summary>HEAD / POST operations used by the admin command (real or fake client).</summary>
internal interface INntpCancelMessageTransport : IAsyncDisposable
{
    Task ConnectAsync(AdminSettings settings, CancellationToken cancellationToken);

    Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken);

    Task<HeadExchange> HeadAsync(string messageId, CancellationToken cancellationToken);

    Task<PostExchange> PostAsync(string article, CancellationToken cancellationToken);
}

/// <summary>AUTHINFO failed. The message never includes the password.</summary>
internal sealed class NntpCancelMessageAuthenticationException : IOException
{
    public NntpCancelMessageAuthenticationException(string message)
        : base(message)
    {
    }
}

internal sealed class TcpNntpCancelMessageTransport : INntpCancelMessageTransport
{
    private readonly NntpCancelMessageClient _client = new();

    public Task ConnectAsync(AdminSettings settings, CancellationToken cancellationToken) =>
        _client.ConnectAsync(settings, cancellationToken);

    public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken) =>
        _client.AuthenticateAsync(username, password, cancellationToken);

    public Task<HeadExchange> HeadAsync(string messageId, CancellationToken cancellationToken) =>
        _client.HeadAsync(messageId, cancellationToken);

    public Task<PostExchange> PostAsync(string article, CancellationToken cancellationToken) =>
        _client.PostAsync(article, cancellationToken);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
