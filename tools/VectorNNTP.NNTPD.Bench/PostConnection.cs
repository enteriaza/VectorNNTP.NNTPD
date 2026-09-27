using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// One serialized POST TCP connection: greeting, AUTHINFO, then POST → 340 → article → 240.
/// Does not pipeline. Does not send MODE STREAM.
/// </summary>
internal sealed class PostConnection : IAsyncDisposable
{
    private const int ReceiveBufferSize = 1024 * 1024;
    private const int SendBufferSize = 256 * 1024;
    private static readonly byte[] PostCommand = "POST\r\n"u8.ToArray();

    private readonly int _id;
    private readonly string _host;
    private readonly int _port;
    private readonly byte[] _articleTemplate;
    private readonly string _authUser;
    private readonly string _authPassword;
    private readonly TimeSpan _duration;
    private readonly TimeSpan _warmup;

    private Socket? _socket;
    private NetworkStream? _stream;
    private byte[]? _recvBuffer;
    private int _recvLen;
    private int _recvPos;
    private long _sequence;

    public PostConnection(
        int id,
        string host,
        int port,
        byte[] articleTemplate,
        string authUser,
        string authPassword,
        TimeSpan duration,
        TimeSpan warmup)
    {
        ArgumentNullException.ThrowIfNull(articleTemplate);
        ArgumentException.ThrowIfNullOrWhiteSpace(authUser);
        ArgumentNullException.ThrowIfNull(authPassword);

        _id = id;
        _host = host;
        _port = port;
        _articleTemplate = articleTemplate;
        _authUser = authUser;
        _authPassword = authPassword;
        _duration = duration;
        _warmup = warmup;
    }

    public long Sent { get; private set; }
    public long Accepted240 { get; private set; }
    public long Rejected441 { get; private set; }
    public long Rejected440 { get; private set; }
    public long Temporary400 { get; private set; }
    public long ProtocolErrors { get; private set; }
    public long ConnectionErrors { get; private set; }
    public long BytesSent { get; private set; }
    public long ArticleBytesSent { get; private set; }
    public int WireCommandBytes { get; private set; }
    public int MaxOutstanding { get; private set; }
    public long Instance { get; private set; }
    public double MeasureElapsedSeconds { get; private set; }
    public Exception? Fault { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ConnectAndNegotiateAsync(cancellationToken).ConfigureAwait(false);

            if (_warmup > TimeSpan.Zero)
            {
                await RunWindowAsync(_warmup, record: false, cancellationToken).ConfigureAwait(false);
                ResetMeasureCounters();
            }

            var measureSw = Stopwatch.StartNew();
            await RunWindowAsync(_duration, record: true, cancellationToken).ConfigureAwait(false);
            measureSw.Stop();
            MeasureElapsedSeconds = measureSw.Elapsed.TotalSeconds;
        }
        catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException or ObjectDisposedException)
        {
            ConnectionErrors++;
            Fault ??= ex;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProtocolErrors++;
            Fault ??= ex;
        }
    }

    public async Task ConnectAndNegotiateAsync(CancellationToken cancellationToken)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            ReceiveBufferSize = ReceiveBufferSize,
            SendBufferSize = SendBufferSize,
        };
        await _socket.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
        _stream = new NetworkStream(_socket, ownsSocket: true);
        _recvBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        _recvLen = 0;
        _recvPos = 0;

        var greeting = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (greeting is null ||
            !(greeting.StartsWith("200 ", StringComparison.Ordinal) ||
              greeting.StartsWith("201 ", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Connection {_id}: unexpected greeting '{greeting}'.");
        }

        await SendAllAsync(Encoding.ASCII.GetBytes("AUTHINFO USER " + _authUser + "\r\n"), cancellationToken)
            .ConfigureAwait(false);
        var userReply = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!IsStatus(userReply, "381 "))
        {
            throw new InvalidOperationException($"Connection {_id}: AUTHINFO USER failed: '{userReply}'.");
        }

        await SendAllAsync(Encoding.ASCII.GetBytes("AUTHINFO PASS " + _authPassword + "\r\n"), cancellationToken)
            .ConfigureAwait(false);
        var passReply = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!IsStatus(passReply, "281 "))
        {
            throw new InvalidOperationException($"Connection {_id}: AUTHINFO PASS failed: '{passReply}'.");
        }
    }

    public void ResetMeasureCounters()
    {
        Sent = 0;
        Accepted240 = 0;
        Rejected441 = 0;
        Rejected440 = 0;
        Temporary400 = 0;
        BytesSent = 0;
        ArticleBytesSent = 0;
        MaxOutstanding = 0;
    }

    public async Task RunWindowAsync(TimeSpan duration, bool record, CancellationToken cancellationToken)
    {
        if (_socket is null || _stream is null)
        {
            throw new InvalidOperationException($"Connection {_id} is not connected.");
        }

        var ids = new PostMessageIdBuffer(_id);
        Instance = ids.Instance;
        WireCommandBytes = PostCommand.Length;
        var article = (byte[])_articleTemplate.Clone();
        var started = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested
               && Stopwatch.GetElapsedTime(started) < duration)
        {
            _sequence++;
            TakeThisArticlePayload.WriteMessageId(article, ids.Format(_sequence));

            await SendAllAsync(PostCommand, cancellationToken).ConfigureAwait(false);
            var offer = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!IsStatus(offer, "340 "))
            {
                RecordUnexpectedOffer(offer, record);
                break;
            }

            await SendAllAsync(article, cancellationToken).ConfigureAwait(false);
            var result = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!IsStatus(result, "240 "))
            {
                RecordUnexpectedResult(result, record);
                if (IsStatus(result, "441 ") || IsStatus(result, "440 ") || IsStatus(result, "400 "))
                {
                    continue;
                }

                break;
            }

            if (record)
            {
                Sent++;
                Accepted240++;
                ArticleBytesSent += article.Length;
                BytesSent += PostCommand.Length + article.Length;
                MaxOutstanding = 1;
            }
        }
    }

    private void RecordUnexpectedOffer(string? offer, bool record)
    {
        Fault ??= new InvalidOperationException($"Connection {_id}: expected 340 after POST, got '{offer}'.");
        if (IsStatus(offer, "440 "))
        {
            if (record)
            {
                Sent++;
                Rejected440++;
            }

            return;
        }

        if (IsStatus(offer, "400 "))
        {
            Temporary400++;
            return;
        }

        ProtocolErrors++;
    }

    private void RecordUnexpectedResult(string? result, bool record)
    {
        Fault ??= new InvalidOperationException($"Connection {_id}: expected 240 after article, got '{result}'.");
        if (!record)
        {
            if (IsStatus(result, "441 "))
            {
                return;
            }

            ProtocolErrors++;
            return;
        }

        Sent++;
        if (IsStatus(result, "441 "))
        {
            Rejected441++;
            return;
        }

        if (IsStatus(result, "440 "))
        {
            Rejected440++;
            return;
        }

        if (IsStatus(result, "400 "))
        {
            Temporary400++;
            return;
        }

        ProtocolErrors++;
    }

    private static bool IsStatus(string? line, string prefix) =>
        line is not null && line.StartsWith(prefix, StringComparison.Ordinal);

    private async Task SendAllAsync(byte[] data, CancellationToken cancellationToken) =>
        await SendAllAsync(new ArraySegment<byte>(data), cancellationToken).ConfigureAwait(false);

    private async Task SendAllAsync(ArraySegment<byte> data, CancellationToken cancellationToken)
    {
        var remaining = data;
        while (remaining.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sent = await _socket!.SendAsync(remaining, SocketFlags.None).ConfigureAwait(false);
            if (sent <= 0)
            {
                throw new EndOfStreamException($"Connection {_id}: send returned {sent}.");
            }

            remaining = remaining.Slice(sent);
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(64);
        while (true)
        {
            if (_recvPos >= _recvLen)
            {
                await FillRecvAsync(cancellationToken).ConfigureAwait(false);
            }

            var b = _recvBuffer![_recvPos++];
            if (b == (byte)'\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r')
                {
                    sb.Length--;
                }

                return sb.ToString();
            }

            sb.Append((char)b);
            if (sb.Length > 8 * 1024)
            {
                throw new InvalidOperationException($"Connection {_id}: line too long.");
            }
        }
    }

    private async Task FillRecvAsync(CancellationToken cancellationToken)
    {
        if (_recvPos > 0 && _recvPos == _recvLen)
        {
            _recvPos = 0;
            _recvLen = 0;
        }
        else if (_recvPos > 0 && _recvPos < _recvLen)
        {
            var remaining = _recvLen - _recvPos;
            Buffer.BlockCopy(_recvBuffer!, _recvPos, _recvBuffer!, 0, remaining);
            _recvPos = 0;
            _recvLen = remaining;
        }

        var read = await _stream!.ReadAsync(
            _recvBuffer.AsMemory(_recvLen, _recvBuffer!.Length - _recvLen),
            cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw new EndOfStreamException($"Connection {_id}: closed while reading NNTP responses.");
        }

        _recvLen += read;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // best-effort
        }

        if (_recvBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_recvBuffer);
            _recvBuffer = null;
        }

        _stream = null;
        _socket = null;
    }
}
