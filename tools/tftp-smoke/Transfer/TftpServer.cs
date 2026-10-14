using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VectorNNTP.TftpSmoke.Protocol;
using VectorNNTP.TftpSmoke.Transport;

namespace VectorNNTP.TftpSmoke.Transfer;

/// <summary>Settings for an isolated TFTP server. It is not an Internet-facing file service.</summary>
public sealed class TftpServerOptions
{
    /// <summary>Directory that receives published articles. The process creates it.</summary>
    public required string RootDirectory { get; init; }

    /// <summary>Local address. The benchmark binds the loopback address.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>UDP port. Zero lets the operating system choose.</summary>
    public int Port { get; init; } = 6969;

    /// <summary>Maximum WRQ and RRQ transfers in the data phase. Linger does not consume a slot.</summary>
    public int MaxConcurrentTransfers { get; init; } = 128;

    /// <summary>Maximum transfer sockets, including final-ACK linger. Further completions close without lingering.</summary>
    public int MaxOpenExchanges { get; init; } = 2048;

    /// <summary>Largest article the server will accept or serve.</summary>
    public long MaxArticleBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Largest blksize the server will agree to, from 8 through 65464.</summary>
    public int MaxBlockSize { get; init; } = TftpLimits.MaxBlockSize;

    /// <summary>Largest windowsize the server will agree to. Values above 64 are outside the prototype's tested window.</summary>
    public int MaxWindowSize { get; init; } = 64;

    /// <summary>Retransmission timing shared with the peer when both are this prototype.</summary>
    public TftpTiming Timing { get; init; } = new();

    /// <summary>
    /// Invoked after a WRQ is flushed and hashed, and before the final ACK.
    /// Publish the article to accept it. Leave it unpublished to reject it.
    /// A null callback publishes immediately and writes a sibling .sha256 file.
    /// </summary>
    public Func<ReceivedArticle, CancellationToken, ValueTask>? OnReceived { get; init; }

    /// <summary>Transfer log. Packet traces are not written.</summary>
    public ITftpLog Log { get; init; } = NullTftpLog.Instance;

    /// <summary>
    /// When true, accepted articles are hashed and acknowledged without creating a file.
    /// This is a transport measurement. It is not the end-to-end file result.
    /// </summary>
    public bool DiscardBody { get; init; }

    /// <summary>Optional aggregated timers. Null leaves the hot path untimed.</summary>
    public TransferDiagnostics? Diagnostics { get; init; }
}

/// <summary>
/// RFC 1350 server with RFC 2347 options, limited to octet RRQ and WRQ.
/// Each transfer binds a new UDP port. The listen port is not reused as a transfer identifier.
/// </summary>
public sealed class TftpServer : IAsyncDisposable
{
    private readonly TftpServerOptions _options;
    private readonly IDatagramTransportFactory _factory;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<RequestKey, TftpExchange> _sessions = new();
    private readonly ConcurrentDictionary<string, byte> _reserved = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _tasks = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _root;
    private readonly string _incoming;
    private int _openExchanges;
    private int _disposed;

    /// <summary>Creates a server. <see cref="RunAsync"/> binds the socket.</summary>
    public TftpServer(TftpServerOptions options, IDatagramTransportFactory factory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        if (options.MaxConcurrentTransfers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.MaxArticleBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _slots = new SemaphoreSlim(options.MaxConcurrentTransfers, options.MaxConcurrentTransfers);
        _root = Path.GetFullPath(options.RootDirectory);
        _incoming = Path.Combine(_root, ".incoming");
        Counters = new TftpEndpointCounters();
    }

    /// <summary>Completes when the listen socket is bound.</summary>
    public Task Started => _started.Task;

    /// <summary>Listen endpoint. Available after <see cref="Started"/>.</summary>
    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <summary>Aggregate counters for transfers this process accepted.</summary>
    public TftpEndpointCounters Counters { get; }

    /// <summary>Serves until <paramref name="cancellationToken"/> is cancelled, then waits for active transfers to stop.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_incoming);
        var listen = _factory.Bind(new IPEndPoint(_options.BindAddress, _options.Port));
        LocalEndPoint = listen.LocalEndPoint;
        _started.TrySetResult();
        _options.Log.Write("info", "server_started", $"address={LocalEndPoint} root={_root}");
        var buffer = new byte[65536];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DatagramReceive received;
                try
                {
                    received = await listen.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (IsTransport(ex))
                {
                    break;
                }

                var length = received.Count;
                if (length <= 0 || length > TftpLimits.MaxRequestLength)
                {
                    await SendStandaloneErrorAsync(received.Remote, TftpErrorCode.IllegalOperation, "request is empty or longer than 2048 bytes", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var packet = buffer.AsSpan(0, length).ToArray();
                try
                {
                    await DispatchAsync(packet, received.Remote, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (IsTransport(ex))
                {
                    _options.Log.Write("warn", "request_failed", ex.Message);
                }
            }
        }
        finally
        {
            if (_tasks.Count > 0)
            {
                await Task.WhenAll(_tasks.Keys).ConfigureAwait(false);
            }

            await listen.DisposeAsync().ConfigureAwait(false);
            _options.Log.Write("info", "server_stopped", $"address={LocalEndPoint}");
        }
    }

    /// <summary>Stops the server if <see cref="RunAsync"/> is still running and releases the concurrency gate.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _slots.Dispose();
        await Task.CompletedTask;
    }

    private async Task DispatchAsync(byte[] packet, IPEndPoint remote, CancellationToken cancellationToken)
    {
        string fileName;
        ushort opcode;
        TftpOption[] options;
        string? reject = null;
        ushort rejectCode = 0;
        {
            if (!TftpCodec.TryRead(packet, out var view, out var error))
            {
                reject = error;
                rejectCode = TftpErrorCode.IllegalOperation;
                fileName = "";
                opcode = 0;
                options = [];
            }
            else if (view.Opcode is not (TftpOpcode.ReadRequest or TftpOpcode.WriteRequest))
            {
                reject = "expected RRQ or WRQ";
                rejectCode = TftpErrorCode.IllegalOperation;
                fileName = "";
                opcode = view.Opcode;
                options = [];
            }
            else if (!view.Mode.Equals(TftpLimits.OctetMode, StringComparison.OrdinalIgnoreCase))
            {
                reject = "only octet mode is supported";
                rejectCode = TftpErrorCode.IllegalOperation;
                fileName = view.FileName;
                opcode = view.Opcode;
                options = [];
            }
            else if (!TftpCodec.IsSafeFileName(view.FileName))
            {
                reject = "file name is not a single safe segment";
                rejectCode = TftpErrorCode.AccessViolation;
                fileName = "";
                opcode = view.Opcode;
                options = [];
            }
            else
            {
                fileName = view.FileName;
                opcode = view.Opcode;
                options = view.Options.ToArray();
            }
        }

        if (reject is not null)
        {
            await SendStandaloneErrorAsync(remote, rejectCode, reject, cancellationToken).ConfigureAwait(false);
            return;
        }

        var key = new RequestKey(remote.Address, remote.Port, opcode, fileName);
        if (_sessions.TryGetValue(key, out var existing))
        {
            await existing.RetransmitHandshakeAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TftpNegotiation.TryAccept(options, _options.MaxBlockSize, _options.MaxWindowSize, out var negotiated, out var optionCode, out var optionError))
        {
            await SendStandaloneErrorAsync(remote, optionCode, optionError, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? finalPath = null;
        if (opcode == TftpOpcode.WriteRequest)
        {
            if (!TryReserve(fileName, out finalPath))
            {
                await SendStandaloneErrorAsync(remote, TftpErrorCode.FileExists, "file already exists", cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            finalPath = CombineInsideRoot(fileName);
            if (!File.Exists(finalPath))
            {
                await SendStandaloneErrorAsync(remote, TftpErrorCode.FileNotFound, "file not found", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (!await _slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            if (opcode == TftpOpcode.WriteRequest)
            {
                ReleaseReservation(fileName);
            }

            await SendStandaloneErrorAsync(remote, TftpErrorCode.NotDefined, "server busy", cancellationToken).ConfigureAwait(false);
            return;
        }

        var task = opcode == TftpOpcode.WriteRequest
            ? ServeWriteAsync(key, remote, fileName, finalPath!, negotiated, cancellationToken)
            : ServeReadAsync(key, remote, fileName, finalPath!, negotiated, cancellationToken);
        Track(task);
    }

    private async Task ServeWriteAsync(RequestKey key, IPEndPoint remote, string fileName, string finalPath, NegotiatedTransfer negotiated, CancellationToken cancellationToken)
    {
        var transport = _factory.Bind(new IPEndPoint(_options.BindAddress, 0));
        var meter = new TransferMeter();
        var exchange = new TftpExchange(transport, remote, _options.Timing, meter, remoteIsTransferId: true, _options.Diagnostics);
        _sessions[key] = exchange;
        Interlocked.Increment(ref _openExchanges);
        Counters.Enter();
        Counters.AcceptWrite();
        var discard = _options.DiscardBody;
        var tempPath = discard ? "" : Path.Combine(_incoming, Guid.NewGuid().ToString("N") + ".partial");
        ReceivedArticle? article = null;
        var published = false;
        ushort finalBlock = 0;
        try
        {
            var opening = negotiated.Oack.Length > 0
                ? negotiated.Oack
                : EncodeAck(0);
            exchange.ArmHandshake(opening);
            await exchange.SendArmedHandshakeAsync(cancellationToken).ConfigureAwait(false);
            await using Stream stream = discard
                ? Stream.Null
                : new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var received = await exchange.ReceiveBlocksAsync(
                negotiated.BlockSize,
                negotiated.WindowSize,
                _options.MaxArticleBytes,
                stream,
                hash,
                ReadOnlyMemory<byte>.Empty,
                0,
                false,
                cancellationToken).ConfigureAwait(false);
            if (!received.Completed)
            {
                Counters.Fail();
                _options.Log.Write("warn", "transfer_failed", $"file={fileName} error={received.ErrorMessage} retries={meter.Retries}");
                return;
            }

            var commitStart = _options.Diagnostics is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            if (!discard)
            {
                var flushStart = _options.Diagnostics is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                if (_options.Diagnostics is not null)
                {
                    _options.Diagnostics.AddFlush(System.Diagnostics.Stopwatch.GetTimestamp() - flushStart);
                }
            }

            var sha = hash.GetHashAndReset();
            article = new ReceivedArticle(fileName, tempPath, finalPath, received.PayloadBytes, sha);
            if (_options.OnReceived is null)
            {
                if (discard)
                {
                    article.AcceptVerifiedHash();
                }
                else
                {
                    article.Publish();
                    await File.WriteAllTextAsync(finalPath + ".sha256", Convert.ToHexString(sha).ToLowerInvariant() + "\n", cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await _options.OnReceived(article, cancellationToken).ConfigureAwait(false);
            }

            if (!article.Published)
            {
                await exchange.SendTransferErrorAsync(TftpErrorCode.NotDefined, "verification failed", cancellationToken).ConfigureAwait(false);
                Counters.Fail();
                _options.Log.Write("warn", "verification_failed", $"file={fileName} bytes={received.PayloadBytes}");
                return;
            }

            if (_options.Diagnostics is not null)
            {
                _options.Diagnostics.AddCommit(System.Diagnostics.Stopwatch.GetTimestamp() - commitStart);
            }

            published = true;
            finalBlock = received.FinalBlock;
            await exchange.SendAckAsync(finalBlock, cancellationToken).ConfigureAwait(false);
            Counters.CompleteWrite();
            _options.Log.Write("debug", "transfer_completed", $"file={fileName} bytes={received.PayloadBytes} retries={meter.Retries}");
        }
        catch (OperationCanceledException)
        {
            Counters.Fail();
        }
        catch (Exception ex) when (IsTransport(ex) || ex is IOException)
        {
            Counters.Fail();
            _options.Log.Write("warn", "transfer_failed", $"file={fileName} error={ex.Message}");
        }
        finally
        {
            article?.Dispose();
            if (article is null)
            {
                TryDelete(tempPath);
            }

            ReleaseReservation(fileName);
            _sessions.TryRemove(key, out _);
            Counters.Add(meter);
            Counters.Exit();
            ReleaseSlot();
            if (published)
            {
                await LingerOrCloseAsync(exchange, finalBlock, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await exchange.DisposeAsync().ConfigureAwait(false);
                Interlocked.Decrement(ref _openExchanges);
            }
        }
    }

    private async Task ServeReadAsync(RequestKey key, IPEndPoint remote, string fileName, string finalPath, NegotiatedTransfer negotiated, CancellationToken cancellationToken)
    {
        var transport = _factory.Bind(new IPEndPoint(_options.BindAddress, 0));
        var meter = new TransferMeter();
        var exchange = new TftpExchange(transport, remote, _options.Timing, meter, remoteIsTransferId: true, _options.Diagnostics);
        _sessions[key] = exchange;
        Interlocked.Increment(ref _openExchanges);
        Counters.Enter();
        try
        {
            var info = new FileInfo(finalPath);
            if (!info.Exists || info.Length > _options.MaxArticleBytes)
            {
                var code = info.Exists ? TftpErrorCode.DiskFull : TftpErrorCode.FileNotFound;
                var message = info.Exists ? "article exceeds the configured maximum" : "file not found";
                await exchange.SendTransferErrorAsync(code, message, cancellationToken).ConfigureAwait(false);
                Counters.Fail();
                return;
            }

            var payload = await File.ReadAllBytesAsync(finalPath, cancellationToken).ConfigureAwait(false);
            if (negotiated.Oack.Length > 0)
            {
                exchange.ArmHandshake(negotiated.Oack);
                await exchange.SendArmedHandshakeAsync(cancellationToken).ConfigureAwait(false);
                var acked = await exchange.WaitForBlockAckAsync(0, negotiated.Oack, cancellationToken).ConfigureAwait(false);
                if (!acked)
                {
                    Counters.Fail();
                    return;
                }
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = await exchange.SendBlocksAsync(payload, negotiated.BlockSize, negotiated.WindowSize, started, cancellationToken).ConfigureAwait(false);
            if (result.Status != TftpTransferStatus.Succeeded)
            {
                Counters.Fail();
                _options.Log.Write("warn", "transfer_failed", $"file={fileName} error={result.ErrorMessage}");
            }
            else
            {
                _options.Log.Write("debug", "transfer_completed", $"file={fileName} bytes={payload.Length} retries={meter.Retries}");
            }
        }
        catch (OperationCanceledException)
        {
            Counters.Fail();
        }
        catch (Exception ex) when (IsTransport(ex) || ex is IOException)
        {
            Counters.Fail();
            _options.Log.Write("warn", "transfer_failed", $"file={fileName} error={ex.Message}");
        }
        finally
        {
            _sessions.TryRemove(key, out _);
            Counters.Add(meter);
            Counters.Exit();
            ReleaseSlot();
            await exchange.DisposeAsync().ConfigureAwait(false);
            Interlocked.Decrement(ref _openExchanges);
        }
    }

    private async Task LingerOrCloseAsync(TftpExchange exchange, ushort finalBlock, CancellationToken cancellationToken)
    {
        var open = Volatile.Read(ref _openExchanges);
        if (open > _options.MaxOpenExchanges || _options.Timing.FinalAckLinger <= TimeSpan.Zero)
        {
            Counters.SkipLinger();
            await exchange.DisposeAsync().ConfigureAwait(false);
            Interlocked.Decrement(ref _openExchanges);
            return;
        }

        try
        {
            await exchange.LingerAsync(finalBlock, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransport(ex) || ex is OperationCanceledException)
        {
        }
        finally
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
            Interlocked.Decrement(ref _openExchanges);
        }
    }

    private bool TryReserve(string fileName, out string finalPath)
    {
        finalPath = CombineInsideRoot(fileName);
        lock (_reserved)
        {
            if (_reserved.ContainsKey(fileName) || File.Exists(finalPath))
            {
                return false;
            }

            _reserved[fileName] = 0;
            return true;
        }
    }

    private void ReleaseReservation(string fileName) => _reserved.TryRemove(fileName, out _);

    private string CombineInsideRoot(string fileName)
    {
        var full = Path.GetFullPath(Path.Combine(_root, fileName));
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("File name escaped the server root.");
        }

        return full;
    }

    private async Task SendStandaloneErrorAsync(IPEndPoint remote, ushort code, string message, CancellationToken cancellationToken)
    {
        var transport = _factory.Bind(new IPEndPoint(_options.BindAddress, 0));
        try
        {
            var buffer = new byte[512];
            var length = TftpCodec.EncodeError(buffer, code, message);
            await transport.SendToAsync(buffer.AsMemory(0, length), remote, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
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

    private void Track(Task task)
    {
        _tasks[task] = 0;
        _ = task.ContinueWith(
            completed =>
            {
                _tasks.TryRemove(completed, out _);
                if (completed.IsFaulted)
                {
                    _options.Log.Write("warn", "transfer_fault", completed.Exception?.GetBaseException().Message ?? "faulted");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static byte[] EncodeAck(ushort block)
    {
        var buffer = new byte[4];
        TftpCodec.EncodeAck(buffer, block);
        return buffer;
    }

    private static void TryDelete(string path)
    {
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

    private static bool IsTransport(Exception exception)
    {
        return exception is SocketException or IOException or ObjectDisposedException or InvalidOperationException;
    }
}
