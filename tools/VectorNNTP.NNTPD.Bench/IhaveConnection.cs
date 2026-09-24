using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// One serialized IHAVE TCP connection: greeting, then IHAVE → 335 → article → 235.
/// Does not pipeline. Does not send MODE STREAM.
/// </summary>
internal sealed class IhaveConnection : IAsyncDisposable
{
    private const int ReceiveBufferSize = 1024 * 1024;
    private const int SendBufferSize = 256 * 1024;

    private readonly int _id;
    private readonly string _host;
    private readonly int _port;
    private readonly IhavePreparedArticles _articles;
    private readonly TimeSpan _duration;
    private readonly TimeSpan _warmup;
    private readonly bool _uniqueMessageIds;
    private readonly int? _timingSamples;

    private Socket? _socket;
    private NetworkStream? _stream;
    private byte[]? _recvBuffer;
    private int _recvLen;
    private int _recvPos;
    private long _sequence;
    private int _articleIndex;

    public IhaveConnection(
        int id,
        string host,
        int port,
        IhavePreparedArticles articles,
        TimeSpan duration,
        TimeSpan warmup,
        bool uniqueMessageIds = true,
        int? timingSamples = null)
    {
        ArgumentNullException.ThrowIfNull(articles);
        if (timingSamples is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timingSamples));
        }

        _id = id;
        _host = host;
        _port = port;
        _articles = articles;
        _duration = duration;
        _warmup = warmup;
        _uniqueMessageIds = uniqueMessageIds;
        _timingSamples = timingSamples;
    }

    public long Sent { get; private set; }
    public long Accepted235 { get; private set; }
    public long Rejected435 { get; private set; }
    public long Rejected436 { get; private set; }
    public long Rejected437 { get; private set; }
    public long ProtocolErrors { get; private set; }
    public long ConnectionErrors { get; private set; }
    public long BytesSent { get; private set; }
    public long ArticleBytesSent { get; private set; }
    public int WireCommandBytes { get; private set; }
    public int MaxOutstanding { get; private set; }
    public Exception? Fault { get; private set; }
    public IhaveTimingSample[]? TimingSamples { get; private set; }

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

            if (_timingSamples is int sampleCount)
            {
                await RunSamplesAsync(sampleCount, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunWindowAsync(_duration, record: true, cancellationToken).ConfigureAwait(false);
            }
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
    }

    public void ResetMeasureCounters()
    {
        Sent = 0;
        Accepted235 = 0;
        Rejected435 = 0;
        Rejected436 = 0;
        Rejected437 = 0;
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

        var command = new IhaveCommandBuffer(_id);
        WireCommandBytes = command.Length;
        var started = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested
               && Stopwatch.GetElapsedTime(started) < duration)
        {
            _sequence++;
            command.SetSequence(_uniqueMessageIds ? _sequence : 1);
            var article = _articles.Next(ref _articleIndex);

            await SendAllAsync(command.Segment, cancellationToken).ConfigureAwait(false);
            var offer = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!IsStatus(offer, "335 "))
            {
                RecordUnexpectedOffer(offer, record);
                break;
            }

            await SendAllAsync(article, cancellationToken).ConfigureAwait(false);
            var result = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!IsStatus(result, "235 "))
            {
                RecordUnexpectedResult(result, record);
                break;
            }

            if (record)
            {
                Sent++;
                Accepted235++;
                ArticleBytesSent += article.Length;
                BytesSent += command.Length + article.Length;
                MaxOutstanding = 1;
            }
        }
    }

    public async Task RunSamplesAsync(int sampleCount, CancellationToken cancellationToken)
    {
        if (_socket is null || _stream is null)
        {
            throw new InvalidOperationException($"Connection {_id} is not connected.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        var command = new IhaveCommandBuffer(_id);
        WireCommandBytes = command.Length;
        var samples = new IhaveTimingSample[sampleCount];

        for (var i = 0; i < sampleCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sequence++;
            command.SetSequence(_uniqueMessageIds ? _sequence : 1);
            var article = _articles.Next(ref _articleIndex);

            var t0 = Stopwatch.GetTimestamp();
            await SendAllAsync(command.Segment, cancellationToken).ConfigureAwait(false);
            var tCommand = Stopwatch.GetTimestamp();
            var offer = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var t335 = Stopwatch.GetTimestamp();
            if (!IsStatus(offer, "335 "))
            {
                RecordUnexpectedOffer(offer, record: true);
                TimingSamples = samples[..i];
                return;
            }

            await SendAllAsync(article, cancellationToken).ConfigureAwait(false);
            var tArticle = Stopwatch.GetTimestamp();
            var result = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var t235 = Stopwatch.GetTimestamp();
            if (!IsStatus(result, "235 "))
            {
                RecordUnexpectedResult(result, record: true);
                TimingSamples = samples[..i];
                return;
            }

            samples[i] = new IhaveTimingSample(
                article.Length,
                ToMicroseconds(t0, tCommand),
                ToMicroseconds(tCommand, t335),
                ToMicroseconds(t335, tArticle),
                ToMicroseconds(tArticle, t235),
                ToMicroseconds(t0, t235));

            Sent++;
            Accepted235++;
            ArticleBytesSent += article.Length;
            BytesSent += command.Length + article.Length;
            MaxOutstanding = 1;
        }

        TimingSamples = samples;
    }

    private static long ToMicroseconds(long start, long end) =>
        (long)Stopwatch.GetElapsedTime(start, end).TotalMicroseconds;

    private void RecordUnexpectedOffer(string? offer, bool record)
    {
        var message = $"Connection {_id}: expected 335 after IHAVE, got '{offer}'.";
        Fault ??= new InvalidOperationException(message);
        if (!record)
        {
            ProtocolErrors++;
            return;
        }

        Sent++;
        if (IsStatus(offer, "435 "))
        {
            Rejected435++;
        }
        else if (IsStatus(offer, "436 "))
        {
            Rejected436++;
        }
        else
        {
            ProtocolErrors++;
        }
    }

    private void RecordUnexpectedResult(string? result, bool record)
    {
        var message = $"Connection {_id}: expected 235 after article, got '{result}'.";
        Fault ??= new InvalidOperationException(message);
        if (!record)
        {
            ProtocolErrors++;
            return;
        }

        Sent++;
        if (IsStatus(result, "436 "))
        {
            Rejected436++;
        }
        else if (IsStatus(result, "437 "))
        {
            Rejected437++;
        }
        else
        {
            ProtocolErrors++;
        }
    }

    private static bool IsStatus(string? line, string prefix) =>
        line is not null && line.StartsWith(prefix, StringComparison.Ordinal);

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
