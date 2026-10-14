using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using VectorNNTP.TcpSmoke.Protocol;

namespace VectorNNTP.TcpSmoke.Transfer;

/// <summary>How one PUT or GET ended.</summary>
public enum ArticleTransferKind
{
    /// <summary>The receiver returned <see cref="ArticleStatus.Ok"/> after its configured contract.</summary>
    Succeeded,

    /// <summary>The receiver returned a non-success status. The connection can usually be reused.</summary>
    Failed,

    /// <summary>The stream closed or the operation timed out before a RESULT or a complete GET body.</summary>
    Incomplete,

    /// <summary>The caller cancelled the operation.</summary>
    Cancelled,
}

/// <summary>One finished PUT or GET.</summary>
public sealed class ArticleTransferResult
{
    /// <summary>Disposition of this attempt.</summary>
    public required ArticleTransferKind Kind { get; init; }

    /// <summary>Receiver status. <see cref="ArticleStatus.Incomplete"/> when no RESULT arrived.</summary>
    public ArticleStatus Status { get; init; }

    /// <summary>Article bytes the peer accepted. Zero unless <see cref="Kind"/> is <see cref="ArticleTransferKind.Succeeded"/>.</summary>
    public long PayloadBytes { get; init; }

    /// <summary>Monotonic time spent in this attempt, including a fresh connect when the caller measured one.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>SHA-256 returned by the peer. Empty when the peer sent none.</summary>
    public byte[] Sha256 { get; init; } = [];

    /// <summary>Article bytes from a successful GET. Empty for PUT.</summary>
    public byte[] Payload { get; init; } = [];
}

/// <summary>Opens TCP connections and exchanges one article at a time on each.</summary>
public sealed class ArticleClient
{
    private readonly IPAddress _address;
    private readonly int _port;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a client. <paramref name="timeout"/> bounds one connect and one transfer.</summary>
    public ArticleClient(IPAddress address, int port, TimeSpan timeout)
    {
        _address = address ?? throw new ArgumentNullException(nameof(address));
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _port = port;
        _timeout = timeout;
    }

    /// <summary>Connects one socket. The caller reuses it for sequential transfers.</summary>
    public async Task<ArticleConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        client.NoDelay = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await client.ConnectAsync(_address, _port, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            client.Dispose();
            throw;
        }

        return new ArticleConnection(client, _timeout, Stopwatch.GetElapsedTime(started));
    }
}

/// <summary>One connected socket. PUT and GET run one at a time. Dispose closes the socket.</summary>
public sealed class ArticleConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    internal ArticleConnection(TcpClient client, TimeSpan timeout, TimeSpan connectElapsed)
    {
        _client = client;
        _stream = client.GetStream();
        _timeout = timeout;
        ConnectElapsed = connectElapsed;
    }

    /// <summary>Time spent inside <see cref="TcpClient.ConnectAsync(IPAddress, int, CancellationToken)"/>.</summary>
    public TimeSpan ConnectElapsed { get; }

    /// <summary>False after a disconnect. Further transfers fail as incomplete.</summary>
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _client.Connected;

    /// <summary>Sends one article and waits for RESULT. A successful RESULT means the receiver finished its contract, not merely that TCP accepted the write.</summary>
    public async Task<ArticleTransferResult> PutAsync(
        ulong articleId,
        ulong transferId,
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte> sha256,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (sha256.Length != 32)
        {
            throw new ArgumentException("SHA-256 must be 32 bytes.", nameof(sha256));
        }

        if (payload.Length > ArticleProtocol.DefaultMaxArticleBytes && payload.Length > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return Done(ArticleTransferKind.Incomplete, ArticleStatus.Incomplete, 0, started, []);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            var header = new ArticleHeader(ArticleOperation.Put, ArticleStatus.Ok, (uint)payload.Length, articleId, transferId, sha256.Span);
            try
            {
                await ArticleIo.WriteHeaderAsync(_stream, header, timeout.Token).ConfigureAwait(false);
                if (payload.Length > 0)
                {
                    await _stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
                }

                var response = new byte[ArticleProtocol.HeaderLength];
                if (!await ArticleIo.ReadExactOrCleanEndAsync(_stream, response, timeout.Token).ConfigureAwait(false)
                    || !ArticleHeader.TryRead(response, out var result, out _))
                {
                    MarkClosed();
                    return Done(ArticleTransferKind.Incomplete, ArticleStatus.Incomplete, 0, started, []);
                }

                if (result.Operation != ArticleOperation.Result)
                {
                    MarkClosed();
                    return Done(ArticleTransferKind.Failed, ArticleStatus.BadOperation, 0, started, result.Sha256);
                }

                var kind = result.Status == ArticleStatus.Ok ? ArticleTransferKind.Succeeded : ArticleTransferKind.Failed;
                return Done(kind, result.Status, kind == ArticleTransferKind.Succeeded ? payload.Length : 0, started, result.Sha256);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MarkClosed();
                return Done(ArticleTransferKind.Cancelled, ArticleStatus.Incomplete, 0, started, []);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or EndOfStreamException or OperationCanceledException)
            {
                MarkClosed();
                return Done(ArticleTransferKind.Incomplete, ArticleStatus.Incomplete, 0, started, []);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Requests one published article. Transport-only servers have no articles and return not found.</summary>
    public async Task<ArticleTransferResult> GetAsync(ulong articleId, ulong transferId, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            Span<byte> zeros = stackalloc byte[32];
            var header = new ArticleHeader(ArticleOperation.Get, ArticleStatus.Ok, 0, articleId, transferId, zeros);
            try
            {
                await ArticleIo.WriteHeaderAsync(_stream, header, timeout.Token).ConfigureAwait(false);
                var response = new byte[ArticleProtocol.HeaderLength];
                if (!await ArticleIo.ReadExactOrCleanEndAsync(_stream, response, timeout.Token).ConfigureAwait(false)
                    || !ArticleHeader.TryRead(response, out var result, out _))
                {
                    MarkClosed();
                    return Done(ArticleTransferKind.Incomplete, ArticleStatus.Incomplete, 0, started, []);
                }

                if (result.Operation == ArticleOperation.Result || result.Status != ArticleStatus.Ok)
                {
                    var failed = result.Status == ArticleStatus.Ok ? ArticleStatus.BadOperation : result.Status;
                    return Done(ArticleTransferKind.Failed, failed, 0, started, result.Sha256);
                }

                var payload = new byte[result.PayloadLength];
                if (result.PayloadLength > 0
                    && !await ArticleIo.ReadExactOrCleanEndAsync(_stream, payload, timeout.Token).ConfigureAwait(false))
                {
                    MarkClosed();
                    return Done(ArticleTransferKind.Incomplete, ArticleStatus.Incomplete, 0, started, []);
                }

                return new ArticleTransferResult
                {
                    Kind = ArticleTransferKind.Succeeded,
                    Status = ArticleStatus.Ok,
                    PayloadBytes = payload.Length,
                    Elapsed = Stopwatch.GetElapsedTime(started),
                    Sha256 = result.Sha256,
                    Payload = payload,
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MarkClosed();
                return Done(ArticleTransferKind.Cancelled, ArticleStatus.Incomplete, 0, started, []);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or EndOfStreamException or OperationCanceledException)
            {
                MarkClosed();
                return Done(ArticleTransferKind.Incomplete, ArticleStatus.Incomplete, 0, started, []);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the socket.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stream.Dispose();
        _client.Dispose();
        _gate.Dispose();
        await Task.CompletedTask;
    }

    private void MarkClosed() => Volatile.Write(ref _disposed, 1);

    private static ArticleTransferResult Done(ArticleTransferKind kind, ArticleStatus status, long bytes, long started, byte[] sha256)
    {
        return new ArticleTransferResult
        {
            Kind = kind,
            Status = status,
            PayloadBytes = bytes,
            Elapsed = Stopwatch.GetElapsedTime(started),
            Sha256 = sha256,
        };
    }
}
