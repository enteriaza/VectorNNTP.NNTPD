using System.Net.Security;

namespace VectorNNTP.NNTPD.Networking.Transport;

/// <summary>
/// Single owner of byte-stream I/O under <see cref="NntpConnection"/> pumps.
/// </summary>
/// <remarks>
/// <para>
/// Pumps call <see cref="ReadAsync"/> / <see cref="WriteAsync"/> only. They never touch the socket
/// or <see cref="SslStream"/> directly. The active stream may be replaced during an in-place TLS
/// upgrade after <see cref="QuiesceAsync"/> guarantees no outstanding stream I/O.
/// </para>
/// <para>
/// Quiescence blocks new application reads/writes at a gate, cancels in-flight <em>reads</em>
/// (freeing the socket for TLS handshake or DEFLATE wrap), and waits for in-flight <em>writes</em>
/// to finish under the caller token. Admission of an I/O operation and registration of that
/// operation as outstanding are atomic with respect to the quiescence state transition under
/// <c>_gate</c>.
/// </para>
/// </remarks>
internal sealed class ConnectionByteTransport : IAsyncDisposable
{
    private const int StateActive = 0;
    private const int StateQuiesced = 1;
    private const int StateAbandoned = 2;
    private const int StateDisposed = 3;

    private readonly object _gate = new();
    private Stream _stream;
    private CancellationTokenSource _readCts = new();
    private TaskCompletionSource _resumeTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _idleTcs;
    private int _activeReads;
    private int _activeWrites;
    private int _state;
    private bool _isTls;
    private bool _isCompressed;

    public ConnectionByteTransport(Stream stream, bool isTls)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _isTls = isTls;
        _resumeTcs.SetResult();
    }

    public bool IsTls
    {
        get
        {
            lock (_gate)
            {
                return _isTls;
            }
        }
    }

    public bool IsCompressed
    {
        get
        {
            lock (_gate)
            {
                return _isCompressed;
            }
        }
    }

    /// <summary>
    /// Test-only: awaited after <see cref="WaitUntilActiveAsync"/> returns and before admission under
    /// <c>_gate</c>. Used to reproduce the historical Active-observed / not-yet-counted window.
    /// </summary>
    internal Func<ValueTask>? AfterActiveBeforeAdmitProbe { get; set; }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitUntilActiveAsync(cancellationToken).ConfigureAwait(false);

            if (AfterActiveBeforeAdmitProbe is { } probe)
            {
                await probe().ConfigureAwait(false);
            }

            Stream stream;
            CancellationToken readToken;
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_state != StateActive)
                {
                    continue;
                }

                _activeReads++;
                stream = _stream;
                readToken = _readCts.Token;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(readToken, cancellationToken);
            try
            {
                return await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            finally
            {
                CompleteOperation(isRead: true);
            }
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitUntilActiveAsync(cancellationToken).ConfigureAwait(false);

            if (AfterActiveBeforeAdmitProbe is { } probe)
            {
                await probe().ConfigureAwait(false);
            }

            Stream stream;
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_state != StateActive)
                {
                    continue;
                }

                _activeWrites++;
                stream = _stream;
            }

            try
            {
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                return;
            }
            finally
            {
                CompleteOperation(isRead: false);
            }
        }
    }

    /// <summary>
    /// Flushes the active stream (required for DEFLATE sync-flush so compressed bytes reach the peer).
    /// </summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitUntilActiveAsync(cancellationToken).ConfigureAwait(false);

            if (AfterActiveBeforeAdmitProbe is { } probe)
            {
                await probe().ConfigureAwait(false);
            }

            Stream stream;
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_state != StateActive)
                {
                    continue;
                }

                _activeWrites++;
                stream = _stream;
            }

            try
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            finally
            {
                CompleteOperation(isRead: false);
            }
        }
    }

    /// <summary>
    /// Stops application stream I/O and waits until the underlying stream has no outstanding operations.
    /// </summary>
    public async Task QuiesceAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource idle;
        CancellationTokenSource oldReadCts;
        bool alreadyIdle;
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_state == StateQuiesced)
            {
                throw new InvalidOperationException("Transport is already quiesced.");
            }

            // Close admission before inspecting outstanding counts so no new I/O can be admitted
            // without being represented in those counts.
            _state = StateQuiesced;
            _resumeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _idleTcs = idle;
            oldReadCts = _readCts;
            _readCts = new CancellationTokenSource();
            alreadyIdle = _activeReads == 0 && _activeWrites == 0;
        }

        await oldReadCts.CancelAsync().ConfigureAwait(false);
        oldReadCts.Dispose();

        if (alreadyIdle)
        {
            idle.TrySetResult();
        }

        await idle.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the quiesced plain stream so the caller can wrap it in <see cref="SslStream"/>.
    /// </summary>
    public Stream TakeQuiescedStreamForTlsWrap()
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_state != StateQuiesced)
            {
                throw new InvalidOperationException("Transport must be quiesced before TLS wrap.");
            }

            if (_isTls)
            {
                throw new InvalidOperationException("Transport is already TLS-protected.");
            }

            if (_isCompressed)
            {
                throw new InvalidOperationException("Cannot negotiate TLS after DEFLATE is active.");
            }

            return _stream;
        }
    }

    /// <summary>
    /// Returns the quiesced stream (plain or TLS) so the caller can wrap it in <see cref="NntpDeflateStream"/>.
    /// </summary>
    public Stream TakeQuiescedStreamForDeflateWrap()
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_state != StateQuiesced)
            {
                throw new InvalidOperationException("Transport must be quiesced before DEFLATE wrap.");
            }

            if (_isCompressed)
            {
                throw new InvalidOperationException("Transport is already DEFLATE-compressed.");
            }

            return _stream;
        }
    }

    /// <summary>
    /// Publishes an authenticated <see cref="SslStream"/> and resumes pump I/O.
    /// </summary>
    public void PublishTlsAndResume(SslStream sslStream)
    {
        ArgumentNullException.ThrowIfNull(sslStream);
        TaskCompletionSource resume;
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_state != StateQuiesced)
            {
                throw new InvalidOperationException("Transport must be quiesced before publishing TLS.");
            }

            if (_isCompressed)
            {
                throw new InvalidOperationException("Cannot publish TLS after DEFLATE is active.");
            }

            _stream = sslStream;
            _isTls = true;
            _state = StateActive;
            _idleTcs = null;
            resume = _resumeTcs;
        }

        resume.TrySetResult();
    }

    /// <summary>
    /// Publishes a bidirectional raw-DEFLATE stream and resumes pump I/O.
    /// </summary>
    public void PublishDeflateAndResume(NntpDeflateStream deflateStream)
    {
        ArgumentNullException.ThrowIfNull(deflateStream);
        TaskCompletionSource resume;
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_state != StateQuiesced)
            {
                throw new InvalidOperationException("Transport must be quiesced before publishing DEFLATE.");
            }

            if (_isCompressed)
            {
                throw new InvalidOperationException("Transport is already DEFLATE-compressed.");
            }

            _stream = deflateStream;
            _isCompressed = true;
            _state = StateActive;
            _idleTcs = null;
            resume = _resumeTcs;
        }

        resume.TrySetResult();
    }

    /// <summary>
    /// Marks the transport abandoned after a failed upgrade and wakes waiters for connection teardown.
    /// Does not restore plaintext I/O.
    /// </summary>
    public void AbortQuiesceForConnectionTeardown()
    {
        TaskCompletionSource? resume;
        TaskCompletionSource? idle;
        lock (_gate)
        {
            if (_state != StateQuiesced)
            {
                return;
            }

            _state = StateAbandoned;
            resume = _resumeTcs;
            idle = _idleTcs;
            _idleTcs = null;
        }

        idle?.TrySetResult();
        resume.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        Stream stream;
        CancellationTokenSource readCts;
        TaskCompletionSource resume;
        TaskCompletionSource? idle;
        lock (_gate)
        {
            if (_state == StateDisposed)
            {
                return;
            }

            _state = StateDisposed;
            stream = _stream;
            readCts = _readCts;
            resume = _resumeTcs;
            idle = _idleTcs;
            _idleTcs = null;
        }

        idle?.TrySetResult();
        resume.TrySetResult();
        await readCts.CancelAsync().ConfigureAwait(false);
        readCts.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfUnavailable()
    {
        if (_state == StateAbandoned)
        {
            throw new ObjectDisposedException(nameof(ConnectionByteTransport), "Transport abandoned after failed TLS upgrade.");
        }

        ObjectDisposedException.ThrowIf(_state == StateDisposed, this);
    }

    private void CompleteOperation(bool isRead)
    {
        TaskCompletionSource? idle = null;
        lock (_gate)
        {
            if (isRead)
            {
                _activeReads--;
            }
            else
            {
                _activeWrites--;
            }

            if (_state == StateQuiesced
                && _idleTcs is not null
                && _activeReads == 0
                && _activeWrites == 0)
            {
                idle = _idleTcs;
            }
        }

        idle?.TrySetResult();
    }

    private async Task WaitUntilActiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_state == StateActive)
                {
                    return;
                }

                wait = _resumeTcs.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
