using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VectorNNTP.TcpSmoke.Protocol;

namespace VectorNNTP.TcpSmoke.Transfer;

/// <summary>Settings for an isolated TCP article server. The default bind is loopback.</summary>
public sealed class ArticleServerOptions
{
    /// <summary>Local address. Non-loopback addresses require <see cref="AllowRemote"/>.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>TCP port. Zero lets the operating system choose.</summary>
    public int Port { get; init; }

    /// <summary>When false, a non-loopback <see cref="BindAddress"/> is rejected.</summary>
    public bool AllowRemote { get; init; }

    /// <summary>Maximum accepted connections. Further connections are closed.</summary>
    public int MaxConcurrentClients { get; init; } = 128;

    /// <summary>Largest PUT or GET body, in bytes.</summary>
    public int MaxArticleBytes { get; init; } = ArticleProtocol.DefaultMaxArticleBytes;

    /// <summary>When true, a PUT is written under <see cref="RootDirectory"/> and published by rename before RESULT.</summary>
    public bool StoreFiles { get; init; }

    /// <summary>Directory for published articles. Required when <see cref="StoreFiles"/> is true.</summary>
    public string? RootDirectory { get; init; }

    /// <summary>When true, file mode hashes the temp file again before the rename. The default hashes the bytes as they are written.</summary>
    public bool RereadBeforeResult { get; init; }

    /// <summary>Per-read budget for one header or payload. The transfer fails when it expires.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Invoked after a file-mode hash matches and before the rename. A null callback publishes immediately.</summary>
    public Func<string, CancellationToken, ValueTask>? BeforePublish { get; init; }
}

/// <summary>
/// Accepts persistent TCP connections. Each connection handles one article at a time.
/// TCP provides delivery, order, retransmission, and congestion control. This type only frames messages.
/// </summary>
public sealed class ArticleServer : IAsyncDisposable
{
    private readonly ArticleServerOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly HashSet<ulong> _reserved = [];
    private readonly List<Task> _sessions = [];
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _root;
    private readonly string? _incoming;
    private int _disposed;
    private long _completed;
    private long _rejected;

    /// <summary>Creates a server. <see cref="RunAsync"/> binds the listener.</summary>
    public ArticleServer(ArticleServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.MaxConcurrentClients < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Max concurrent clients must be at least 1.");
        }

        if (options.MaxArticleBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Max article bytes cannot be negative.");
        }

        if (!options.AllowRemote && !IPAddress.IsLoopback(options.BindAddress))
        {
            throw new InvalidOperationException("Refusing a non-loopback bind without --allow-remote.");
        }

        if (options.StoreFiles)
        {
            if (string.IsNullOrWhiteSpace(options.RootDirectory))
            {
                throw new InvalidOperationException("File mode requires a root directory.");
            }

            _root = Path.GetFullPath(options.RootDirectory);
            _incoming = Path.Combine(_root, ".incoming");
        }

        _slots = new SemaphoreSlim(options.MaxConcurrentClients, options.MaxConcurrentClients);
    }

    /// <summary>Completes when the listener is bound.</summary>
    public Task Started => _started.Task;

    /// <summary>Bound endpoint. Available after <see cref="Started"/>.</summary>
    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <summary>PUT and GET operations that finished with <see cref="ArticleStatus.Ok"/>.</summary>
    public long CompletedOperations => Volatile.Read(ref _completed);

    /// <summary>Operations that finished with a non-success status.</summary>
    public long RejectedOperations => Volatile.Read(ref _rejected);

    /// <summary>Serves until <paramref name="cancellationToken"/> is cancelled, then waits for open connections to leave.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_incoming is not null)
        {
            Directory.CreateDirectory(_incoming);
        }

        using var listener = new TcpListener(_options.BindAddress, _options.Port);
        listener.Start();
        LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;
        _started.TrySetResult();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient accepted;
                try
                {
                    accepted = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                bool acquired;
                try
                {
                    acquired = await _slots.WaitAsync(0, CancellationToken.None).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    accepted.Dispose();
                    break;
                }

                if (!acquired)
                {
                    accepted.Dispose();
                    continue;
                }

                var session = HandleConnectionAsync(accepted, cancellationToken);
                lock (_sessions)
                {
                    _sessions.Add(session);
                }

                _ = session.ContinueWith(
                    completed =>
                    {
                        lock (_sessions)
                        {
                            _sessions.Remove(completed);
                        }

                        ReleaseSlot();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            listener.Stop();
            Task[] pending;
            lock (_sessions)
            {
                pending = _sessions.ToArray();
            }

            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
            }
        }
    }

    /// <summary>Releases the connection gate.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _slots.Dispose();
        await Task.CompletedTask;
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            var headerBuffer = new byte[ArticleProtocol.HeaderLength];
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(_options.OperationTimeout);
                    if (!await ArticleIo.ReadExactOrCleanEndAsync(stream, headerBuffer, timeout.Token).ConfigureAwait(false))
                    {
                        break;
                    }

                    if (!ArticleHeader.TryRead(headerBuffer, out var header, out var error))
                    {
                        await WriteResultAsync(stream, header.ArticleId, header.TransferId, error, [], cancellationToken).ConfigureAwait(false);
                        Interlocked.Increment(ref _rejected);
                        break;
                    }

                    if (header.Operation == ArticleOperation.Put)
                    {
                        if (!await PutAsync(stream, header, buffer, cancellationToken).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    else if (header.Operation == ArticleOperation.Get)
                    {
                        if (!await GetAsync(stream, header, buffer, cancellationToken).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    else
                    {
                        await WriteResultAsync(stream, header.ArticleId, header.TransferId, ArticleStatus.BadOperation, [], cancellationToken).ConfigureAwait(false);
                        Interlocked.Increment(ref _rejected);
                        break;
                    }
                }
            }
            catch (Exception ex) when (IsDisconnect(ex) || ex is OperationCanceledException)
            {
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private async Task<bool> PutAsync(Stream stream, ArticleHeader header, byte[] buffer, CancellationToken cancellationToken)
    {
        if (header.PayloadLength > (uint)_options.MaxArticleBytes)
        {
            await WriteResultAsync(stream, header.ArticleId, header.TransferId, ArticleStatus.TooLarge, [], cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _rejected);
            return false;
        }

        string? tempPath = null;
        string? finalPath = null;
        var reserved = false;
        try
        {
            if (_options.StoreFiles)
            {
                if (!TryReserve(header.ArticleId, out finalPath))
                {
                    await DiscardPayloadAsync(stream, header.PayloadLength, buffer, cancellationToken).ConfigureAwait(false);
                    await WriteResultAsync(stream, header.ArticleId, header.TransferId, ArticleStatus.Exists, [], cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _rejected);
                    return true;
                }

                reserved = true;
                tempPath = Path.Combine(_incoming!, Guid.NewGuid().ToString("N") + ".partial");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Stream? file = null;
            if (tempPath is not null)
            {
                file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }

            await using (file)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.OperationTimeout);
                await ArticleIo.ReadPayloadAsync(stream, header.PayloadLength, buffer, hash, file, timeout.Token).ConfigureAwait(false);
                if (file is not null)
                {
                    await file.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
            }

            var actual = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actual, header.Sha256))
            {
                DeleteIfPresent(tempPath);
                await WriteResultAsync(stream, header.ArticleId, header.TransferId, ArticleStatus.HashMismatch, actual, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _rejected);
                return true;
            }

            if (tempPath is not null && finalPath is not null)
            {
                var info = new FileInfo(tempPath);
                if (info.Length != header.PayloadLength)
                {
                    DeleteIfPresent(tempPath);
                    await WriteResultAsync(stream, header.ArticleId, header.TransferId, ArticleStatus.IoError, actual, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _rejected);
                    return true;
                }

                if (_options.RereadBeforeResult && !await RereadMatchesAsync(tempPath, actual, cancellationToken).ConfigureAwait(false))
                {
                    DeleteIfPresent(tempPath);
                    await WriteResultAsync(stream, header.ArticleId, header.TransferId, ArticleStatus.HashMismatch, [], cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _rejected);
                    return true;
                }

                if (_options.BeforePublish is not null)
                {
                    await _options.BeforePublish(tempPath, cancellationToken).ConfigureAwait(false);
                }

                Directory.CreateDirectory(_root!);
                try
                {
                    File.Move(tempPath, finalPath, overwrite: false);
                }
                catch (IOException)
                {
                    DeleteIfPresent(tempPath);
                    await WriteResultAsync(stream, header.ArticleId, header.TransferId, ArticleStatus.Exists, actual, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _rejected);
                    return true;
                }
            }

            await WriteResultAsync(stream, header.ArticleId, header.TransferId, ArticleStatus.Ok, actual, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _completed);
            return true;
        }
        catch (Exception ex) when (IsDisconnect(ex))
        {
            DeleteIfPresent(tempPath);
            Interlocked.Increment(ref _rejected);
            return false;
        }
        finally
        {
            if (reserved)
            {
                lock (_reserved)
                {
                    _reserved.Remove(header.ArticleId);
                }
            }

            // A successful publish has already renamed this path away. Anything left is incomplete.
            DeleteIfPresent(tempPath);
        }
    }

    private async Task<bool> GetAsync(Stream stream, ArticleHeader header, byte[] buffer, CancellationToken cancellationToken)
    {
        if (header.PayloadLength != 0 || _root is null)
        {
            await WriteResultAsync(stream, header.ArticleId, header.TransferId, header.PayloadLength != 0 ? ArticleStatus.BadOperation : ArticleStatus.NotFound, [], cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _rejected);
            return header.PayloadLength == 0;
        }

        var path = Path.Combine(_root, FileName(header.ArticleId));
        if (!File.Exists(path))
        {
            await WriteResultAsync(stream, header.ArticleId, header.TransferId, ArticleStatus.NotFound, [], cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _rejected);
            return true;
        }

        var payload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (payload.Length > _options.MaxArticleBytes)
        {
            await WriteResultAsync(stream, header.ArticleId, header.TransferId, ArticleStatus.TooLarge, [], cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _rejected);
            return true;
        }

        var hash = SHA256.HashData(payload);
        var response = new ArticleHeader(ArticleOperation.Get, ArticleStatus.Ok, (uint)payload.Length, header.ArticleId, header.TransferId, hash);
        await ArticleIo.WriteHeaderAsync(stream, response, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        Interlocked.Increment(ref _completed);
        _ = buffer;
        return true;
    }

    private bool TryReserve(ulong articleId, out string finalPath)
    {
        finalPath = Path.Combine(_root!, FileName(articleId));
        lock (_reserved)
        {
            if (_reserved.Contains(articleId) || File.Exists(finalPath))
            {
                return false;
            }

            _reserved.Add(articleId);
            return true;
        }
    }

    private static async Task DiscardPayloadAsync(Stream stream, uint length, byte[] buffer, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await ArticleIo.ReadPayloadAsync(stream, length, buffer, hash, destination: null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> RereadMatchesAsync(string path, byte[] expected, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, count));
            }

            return CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ValueTask WriteResultAsync(Stream stream, ulong articleId, ulong transferId, ArticleStatus status, ReadOnlySpan<byte> sha256, CancellationToken cancellationToken)
    {
        Span<byte> hash = stackalloc byte[32];
        if (sha256.Length == 32)
        {
            sha256.CopyTo(hash);
        }

        var header = new ArticleHeader(ArticleOperation.Result, status, 0, articleId, transferId, hash);
        return ArticleIo.WriteHeaderAsync(stream, header, cancellationToken);
    }

    private void ReleaseSlot()
    {
        try
        {
            _slots.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static string FileName(ulong articleId) => articleId.ToString("x16") + ".bin";

    private static void DeleteIfPresent(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsDisconnect(Exception exception)
    {
        return exception is IOException or SocketException or ObjectDisposedException or EndOfStreamException;
    }
}
