using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VectorNNTP.NNTPCancelMessage.Tests;

/// <summary>Loopback NNTP server that scripts HEAD / AUTHINFO / POST for admin integration tests.</summary>
internal sealed class ScriptedNntpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Task _run;
    private readonly string _headStatus;
    private readonly string? _expectedUsername;
    private readonly string? _expectedPassword;
    private readonly bool _requireAuthentication;
    private readonly string _postReply;

    public ScriptedNntpServer(
        TcpListener listener,
        string headStatus,
        string? expectedUsername,
        string? expectedPassword,
        bool requireAuthentication,
        string postReply)
    {
        _listener = listener;
        _headStatus = headStatus;
        _expectedUsername = expectedUsername;
        _expectedPassword = expectedPassword;
        _requireAuthentication = requireAuthentication;
        _postReply = postReply;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _run = Task.Run(ServeAsync);
    }

    public int Port { get; }

    public List<string> Commands { get; } = [];

    public string LastArticle { get; private set; } = string.Empty;

    public int PostCount { get; private set; }

    public int HeadCount { get; private set; }

    public bool Authenticated { get; private set; }

    public string? LastAuthUsername { get; private set; }

    public static Task<ScriptedNntpServer> StartAsync(
        string? headStatus = null,
        string? expectedUsername = null,
        string? expectedPassword = null,
        bool requireAuthentication = false,
        string postReply = "240 Article received OK\r\n")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new ScriptedNntpServer(
            listener,
            headStatus ??
            "221 0 <abc@example.com>\r\nFrom: poster@example.com\r\nNewsgroups: misc.test\r\nX-Trace: token\r\n.\r\n",
            expectedUsername,
            expectedPassword,
            requireAuthentication,
            postReply));
    }

    private async Task ServeAsync()
    {
        using var client = await _listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await WriteAsync(stream, "200 posting allowed\r\n");
        string? pendingUser = null;
        while (true)
        {
            var line = await ReadLineAsync(stream);
            if (line is null)
            {
                return;
            }

            if (line.StartsWith("AUTHINFO PASS ", StringComparison.OrdinalIgnoreCase))
            {
                Commands.Add("AUTHINFO PASS <redacted>");
            }
            else
            {
                Commands.Add(line);
            }

            if (line.StartsWith("AUTHINFO USER ", StringComparison.OrdinalIgnoreCase))
            {
                pendingUser = line["AUTHINFO USER ".Length..];
                LastAuthUsername = pendingUser;
                if (_expectedUsername is not null
                    && !string.Equals(pendingUser, _expectedUsername, StringComparison.Ordinal))
                {
                    await WriteAsync(stream, "481 Authentication failed\r\n");
                    continue;
                }

                await WriteAsync(stream, "381 Password required\r\n");
            }
            else if (line.StartsWith("AUTHINFO PASS ", StringComparison.OrdinalIgnoreCase))
            {
                var password = line["AUTHINFO PASS ".Length..];
                if (pendingUser is null
                    || (_expectedPassword is not null
                        && !string.Equals(password, _expectedPassword, StringComparison.Ordinal)))
                {
                    Authenticated = false;
                    await WriteAsync(stream, "481 Authentication failed\r\n");
                    continue;
                }

                Authenticated = true;
                await WriteAsync(stream, "281 Authentication accepted\r\n");
            }
            else if (line.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase))
            {
                if (_requireAuthentication && !Authenticated)
                {
                    await WriteAsync(stream, "480 Authentication required\r\n");
                    continue;
                }

                HeadCount++;
                await WriteAsync(stream, _headStatus);
            }
            else if (line.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                if (_requireAuthentication && !Authenticated)
                {
                    await WriteAsync(stream, "440 Posting not permitted\r\n");
                    continue;
                }

                PostCount++;
                await WriteAsync(stream, "340 Input article; end with <CR-LF>.<CR-LF>\r\n");
                LastArticle = await ReadUntilTerminatorAsync(stream);
                await WriteAsync(stream, _postReply);
            }
            else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAsync(stream, "205 closing\r\n");
                return;
            }
            else
            {
                await WriteAsync(stream, "500 unknown\r\n");
            }
        }
    }

    private static async Task WriteAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        var buffer = new List<byte>(128);
        var one = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var read = await stream.ReadAsync(one, cts.Token);
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

    private static async Task<string> ReadUntilTerminatorAsync(NetworkStream stream)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var line = await ReadLineAsync(stream);
            if (line is null || line == ".")
            {
                return sb.ToString();
            }

            sb.Append(line).Append("\r\n");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        try
        {
            await _run;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }
}
