namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// One established connected transport owned by a cache Listener session.
/// </summary>
public interface ICacheListenerTransport : IAsyncDisposable
{
    /// <summary>Reads bytes. Zero means the peer closed.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Writes bytes and returns how many were accepted.</summary>
    ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>Stream adapter that applies the configured I/O no-progress timeout to each read or write.</summary>
public sealed class StreamCacheListenerTransport : ICacheListenerTransport
{
    private readonly Stream _stream;
    private readonly TimeSpan _ioProgressTimeout;
    private readonly bool _leaveInnerStreamOpen;
    private int _disposed;

    /// <summary>Initializes a stream-backed transport.</summary>
    public StreamCacheListenerTransport(Stream stream, TimeSpan ioProgressTimeout, bool leaveInnerStreamOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ioProgressTimeout, TimeSpan.Zero);
        _stream = stream;
        _ioProgressTimeout = ioProgressTimeout;
        _leaveInnerStreamOpen = leaveInnerStreamOpen;
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        using var timeoutCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        timeoutCts.CancelAfter(_ioProgressTimeout);
        try
        {
            return await _stream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Listener transport read exceeded no-progress timeout of {_ioProgressTimeout}.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        using var timeoutCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        timeoutCts.CancelAfter(_ioProgressTimeout);
        try
        {
            await _stream.WriteAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
            return buffer.Length;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Listener transport write exceeded no-progress timeout of {_ioProgressTimeout}.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (!_leaveInnerStreamOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
