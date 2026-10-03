namespace VectorNNTP.BackFiller.Listener
{
    /// <summary>
    /// One established connected transport owned by a cache Listener session.
    /// </summary>
    /// <remarks>
    /// The session does not accept or bind sockets. Implementations own read and write completion
    /// semantics, including how much of each buffer one call transfers. Disposal is
    /// <see cref="IAsyncDisposable"/>.
    /// </remarks>
    internal interface ICacheListenerTransport : IAsyncDisposable
    {
        /// <summary>Reads bytes into <paramref name="buffer"/>.</summary>
        /// <param name="buffer">Destination for the next read.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The number of bytes written to <paramref name="buffer"/>. Zero means the peer closed.</returns>
        ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

        /// <summary>Writes bytes from <paramref name="buffer"/>.</summary>
        /// <param name="buffer">Bytes to write. The implementation decides whether an empty buffer is a no-op.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>How many bytes were accepted by this call.</returns>
        ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
    }

    /// <summary>Stream adapter that applies the configured I/O no-progress timeout to each read or write.</summary>
    /// <remarks>
    /// Each read or non-empty write links the caller token when that token can be canceled, then cancels
    /// the linked source after the configured no-progress timeout. A timeout with the caller token still
    /// active becomes <see cref="TimeoutException"/>. Caller cancellation propagates as
    /// <see cref="OperationCanceledException"/>. An empty write returns zero and does not start a timeout.
    /// This type does not interpret VATP frames.
    /// </remarks>
    internal sealed class StreamCacheListenerTransport : ICacheListenerTransport
    {
        /// <summary>Connected stream. Disposed only when <see cref="_leaveInnerStreamOpen"/> is false.</summary>
        private readonly Stream _stream;

        /// <summary>No-progress limit applied independently to each read or non-empty write.</summary>
        private readonly TimeSpan _ioProgressTimeout;

        /// <summary>When true, <see cref="DisposeAsync"/> leaves <see cref="_stream"/> open.</summary>
        private readonly bool _leaveInnerStreamOpen;

        /// <summary>Zero until the first <see cref="DisposeAsync"/>; then one. Disposal is idempotent.</summary>
        private int _disposed;

        /// <summary>Initializes a stream-backed transport.</summary>
        /// <param name="stream">Connected stream used for reads and writes.</param>
        /// <param name="ioProgressTimeout">Positive no-progress limit for each read or non-empty write.</param>
        /// <param name="leaveInnerStreamOpen">
        /// When <see langword="true"/>, disposing this transport does not dispose <paramref name="stream"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="ioProgressTimeout"/> is not positive.</exception>
        internal StreamCacheListenerTransport(Stream stream, TimeSpan ioProgressTimeout, bool leaveInnerStreamOpen = false)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ioProgressTimeout, TimeSpan.Zero);
            _stream = stream;
            _ioProgressTimeout = ioProgressTimeout;
            _leaveInnerStreamOpen = leaveInnerStreamOpen;
        }

        /// <summary>Reads the next bytes, failing when no bytes arrive within the no-progress timeout.</summary>
        /// <param name="buffer">Destination passed to <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/>.</param>
        /// <param name="cancellationToken">Cancels the read. When already canceled, the timeout is not reported.</param>
        /// <returns>The count returned by the stream. Zero means the peer closed.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when this transport is already disposed.</exception>
        /// <exception cref="TimeoutException">
        /// Thrown when the no-progress timeout elapses and <paramref name="cancellationToken"/> is not canceled.
        /// </exception>
        /// <remarks>Caller cancellation propagates as <see cref="OperationCanceledException"/>.</remarks>
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

        /// <summary>
        /// Writes the whole buffer or returns zero when it is empty. The successful return is the buffer length, not a partial count.
        /// </summary>
        /// <param name="buffer">Bytes passed to <see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.</param>
        /// <param name="cancellationToken">Cancels a non-empty write. When already canceled, the timeout is not reported.</param>
        /// <returns>Zero when <paramref name="buffer"/> is empty; otherwise <c>buffer.Length</c> after the stream write completes.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when this transport is already disposed.</exception>
        /// <exception cref="TimeoutException">
        /// Thrown when a non-empty write exceeds the no-progress timeout and <paramref name="cancellationToken"/> is not canceled.
        /// </exception>
        /// <remarks>Caller cancellation propagates as <see cref="OperationCanceledException"/>.</remarks>
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

        /// <summary>Disposes the inner stream once, unless construction asked to leave it open.</summary>
        /// <returns>A task that completes when the inner stream disposal finishes, or immediately on a later call.</returns>
        /// <remarks>A second call is a no-op. Timeout does not dispose this transport.</remarks>
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
}
