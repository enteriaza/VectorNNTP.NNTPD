using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace VectorNNTP.NNTPD.Bench;

internal sealed class BenchWorker : IAsyncDisposable
{
    private static readonly byte[] BenchitCommand = "BENCHIT\r\n"u8.ToArray();
    private static readonly byte[] Terminator = "\r\n.\r\n"u8.ToArray();

    private readonly int _id;
    private Socket? _socket;
    private CountingStream? _counting;
    private Stream? _app; // outermost application stream (plain / ssl / deflate)
    private byte[]? _recvBuffer;
    private int _recvLen;
    private int _recvPos;

    public BenchWorker(int id) => _id = id;

    public long Requests { get; private set; }
    public long LogicalBytes { get; private set; }
    public long WireBytesRead => _counting?.BytesReadDuringMeasure ?? 0;
    public long WireBytesWritten => _counting?.BytesWrittenDuringMeasure ?? 0;
    public LatencyHistogram Latency { get; } = new();
    public int? ValidatedWireBytes { get; private set; }
    public int? ValidatedArticleBytes { get; private set; }
    public string? TlsVersion { get; private set; }
    public string? Cipher { get; private set; }

    public async Task ConnectAndNegotiateAsync(
        string host,
        int port,
        bool useTls,
        bool useDeflate,
        string tlsHostName,
        CancellationToken cancellationToken)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            ReceiveBufferSize = 1024 * 1024,
            SendBufferSize = 256 * 1024,
        };
        await _socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);

        var network = new NetworkStream(_socket, ownsSocket: true);
        _counting = new CountingStream(network, ownsInner: true);
        Stream current = _counting;

        if (useTls)
        {
            var ssl = new SslStream(current, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = tlsHostName,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
                cancellationToken).ConfigureAwait(false);
            TlsVersion = ssl.SslProtocol.ToString();
            Cipher = $"{ssl.NegotiatedCipherSuite}";
            current = ssl;
        }

        _app = current;
        _recvBuffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        _recvLen = 0;
        _recvPos = 0;

        var greeting = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (greeting is null || !(greeting.StartsWith("200 ", StringComparison.Ordinal) ||
                                  greeting.StartsWith("201 ", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Worker {_id}: unexpected greeting '{greeting}'.");
        }

        if (useDeflate)
        {
            await WriteAllAsync("COMPRESS DEFLATE\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            var compressReply = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (compressReply is null || !compressReply.StartsWith("206 ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Worker {_id}: COMPRESS failed: '{compressReply}'.");
            }

            // Switch application stream to raw DEFLATE above current (TLS or plain counted stream).
            _app = new RawDeflateDuplex(current, leaveInnerOpen: false);
            // Discard any leftover plaintext buffered before the layer switch (should be empty).
            _recvLen = 0;
            _recvPos = 0;
        }

        // Validate one BENCHIT response before measurement (establishes wire size).
        await WriteAllAsync(BenchitCommand, cancellationToken).ConfigureAwait(false);
        var (wireBytes, articleBytes) = await ReadCompleteBenchitAsync(
            validateIdentity: true,
            cancellationToken).ConfigureAwait(false);
        ValidatedWireBytes = wireBytes;
        ValidatedArticleBytes = articleBytes;
    }

    public void ResetCounters()
    {
        Requests = 0;
        LogicalBytes = 0;
        Latency.Reset();
        _counting?.BeginMeasure();
    }

    public async Task RunAsync(TimeSpan duration, bool record, CancellationToken cancellationToken)
    {
        var startedWindow = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested
               && Stopwatch.GetElapsedTime(startedWindow) < duration)
        {
            var started = Stopwatch.GetTimestamp();
            await WriteAllAsync(BenchitCommand, cancellationToken).ConfigureAwait(false);
            // Always finish the in-flight response so the stream stays framed-aligned.
            var (wireBytes, articleBytes) = await ReadCompleteBenchitAsync(
                validateIdentity: false,
                cancellationToken).ConfigureAwait(false);

            if (record)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                Latency.Record(elapsed);
                Requests++;
                LogicalBytes += articleBytes;
                _ = wireBytes;
            }
        }
    }

    private async Task<(int WireBytes, int ArticleBytes)> ReadCompleteBenchitAsync(
        bool validateIdentity,
        CancellationToken cancellationToken)
    {
        // Scan for multiline terminator \r\n.\r\n after a 220 status line.
        var wire = 0;
        var statusSeen = false;
        var statusOk = false;
        var articleStart = -1;

        while (true)
        {
            EnsureRecvData(cancellationToken);
            if (_recvPos >= _recvLen)
            {
                await FillRecvAsync(cancellationToken).ConfigureAwait(false);
            }

            // Search terminator in buffered data.
            var span = _recvBuffer.AsSpan(_recvPos, _recvLen - _recvPos);
            if (!statusSeen)
            {
                var nl = span.IndexOf((byte)'\n');
                if (nl < 0)
                {
                    await FillRecvAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var line = span[..(nl + 1)];
                wire += line.Length;
                statusSeen = true;
                statusOk = line.Length >= 4 && line[0] == (byte)'2' && line[1] == (byte)'2' && line[2] == (byte)'0';
                if (!statusOk)
                {
                    var text = Encoding.ASCII.GetString(line);
                    throw new InvalidOperationException($"Worker {_id}: unexpected BENCHIT status: {text.Trim()}");
                }

                _recvPos += nl + 1;
                articleStart = wire;
                continue;
            }

            var termAt = IndexOfTerminator(span);
            if (termAt < 0)
            {
                // Keep a small overlap for straddling terminator; otherwise consume.
                if (span.Length > Terminator.Length)
                {
                    var keep = Terminator.Length - 1;
                    var consume = span.Length - keep;
                    wire += consume;
                    _recvPos += consume;
                }

                await FillRecvAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Include terminator in wire count.
            wire += termAt + Terminator.Length;
            _recvPos += termAt + Terminator.Length;

            // Logical article ≈ wire without status line and without terminating .\r\n,
            // minus one stuffed dot if present. For steady-state we use validated size.
            var framed = wire - articleStart; // includes terminator
            var articleBytes = ValidatedArticleBytes ?? (framed - Terminator.Length);
            if (validateIdentity)
            {
                // Exact logical size contract for this internal benchmark command.
                articleBytes = 750 * 1024;
                // Wire includes status + stuffed article + terminator; must be > article.
                if (wire <= articleBytes)
                {
                    throw new InvalidOperationException(
                        $"Worker {_id}: BENCHIT wire size {wire} is not larger than article {articleBytes}.");
                }
            }
            else if (ValidatedWireBytes is int expected && wire != expected)
            {
                throw new InvalidOperationException(
                    $"Worker {_id}: BENCHIT wire size changed ({wire} vs expected {expected}).");
            }

            return (wire, ValidatedArticleBytes ?? articleBytes);
        }
    }

    private static int IndexOfTerminator(ReadOnlySpan<byte> span)
    {
        // Look for \r\n.\r\n
        for (var i = 0; i <= span.Length - 5; i++)
        {
            if (span[i] == (byte)'\r'
                && span[i + 1] == (byte)'\n'
                && span[i + 2] == (byte)'.'
                && span[i + 3] == (byte)'\r'
                && span[i + 4] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private void EnsureRecvData(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (_recvPos > 0 && _recvPos == _recvLen)
        {
            _recvPos = 0;
            _recvLen = 0;
        }
        else if (_recvPos > 64 * 1024 && _recvPos < _recvLen)
        {
            var remaining = _recvLen - _recvPos;
            Buffer.BlockCopy(_recvBuffer!, _recvPos, _recvBuffer!, 0, remaining);
            _recvPos = 0;
            _recvLen = remaining;
        }
    }

    private async Task FillRecvAsync(CancellationToken cancellationToken)
    {
        EnsureRecvData(cancellationToken);
        if (_recvLen >= _recvBuffer!.Length)
        {
            throw new InvalidOperationException($"Worker {_id}: receive buffer exhausted (response too large / missing terminator).");
        }

        var read = await _app!.ReadAsync(_recvBuffer.AsMemory(_recvLen, _recvBuffer.Length - _recvLen), cancellationToken)
            .ConfigureAwait(false);
        if (read == 0)
        {
            throw new EndOfStreamException($"Worker {_id}: connection closed during BENCHIT response.");
        }

        _recvLen += read;
    }

    private async Task WriteAllAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _app!.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _app.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(128);
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
                throw new InvalidOperationException($"Worker {_id}: line too long.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_app is not null)
            {
                await _app.DisposeAsync().ConfigureAwait(false);
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

        _app = null;
        _counting = null;
        _socket = null;
    }
}

/// <summary>Raw RFC 1951 DEFLATE duplex (no zlib wrapper), matching server <c>NntpDeflateStream</c>.</summary>
internal sealed class RawDeflateDuplex : Stream
{
    private readonly Stream _inner;
    private readonly DeflateStream _compressor;
    private readonly DeflateStream _decompressor;
    private readonly bool _leaveInnerOpen;

    public RawDeflateDuplex(Stream inner, bool leaveInnerOpen)
    {
        _inner = inner;
        _leaveInnerOpen = leaveInnerOpen;
        _compressor = new DeflateStream(inner, CompressionLevel.Optimal, leaveOpen: true);
        _decompressor = new DeflateStream(inner, CompressionMode.Decompress, leaveOpen: true);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _compressor.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _compressor.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        _decompressor.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _decompressor.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) =>
        _compressor.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _compressor.WriteAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _compressor.Dispose();
            _decompressor.Dispose();
            if (!_leaveInnerOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _compressor.DisposeAsync().ConfigureAwait(false);
        await _decompressor.DisposeAsync().ConfigureAwait(false);
        if (!_leaveInnerOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _ownsInner;
    private long _read;
    private long _written;
    private long _readMeasureStart;
    private long _writtenMeasureStart;
    private bool _measuring;

    public CountingStream(Stream inner, bool ownsInner)
    {
        _inner = inner;
        _ownsInner = ownsInner;
    }

    public long BytesReadDuringMeasure => _measuring ? _read - _readMeasureStart : 0;
    public long BytesWrittenDuringMeasure => _measuring ? _written - _writtenMeasureStart : 0;

    public void BeginMeasure()
    {
        _readMeasureStart = _read;
        _writtenMeasureStart = _written;
        _measuring = true;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        _read += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _read += n;
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        _written += count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _written += buffer.Length;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsInner)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_ownsInner)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
