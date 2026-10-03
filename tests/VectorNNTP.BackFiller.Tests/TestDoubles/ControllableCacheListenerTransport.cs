using System.Threading.Channels;
using VectorNNTP.BackFiller.Listener;

namespace VectorNNTP.BackFiller.Tests.TestDoubles
{
    /// <summary>
    /// Test transport that can keep delivering inbound bytes after forcing writer-side failure.
    /// </summary>
    internal sealed class ControllableCacheListenerTransport : ICacheListenerTransport
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        private readonly object _writeGate = new();
        private readonly List<byte[]> _written = [];
        private TaskCompletionSource? _blockWrites;
        private Exception? _writeFault;
        private int _successfulWritesAllowed = int.MaxValue;
        private int _successfulWritesCompleted;
        private int _disposed;
        private int _writeCalls;
        private int _readCalls;

        public int WriteCalls => Volatile.Read(ref _writeCalls);

        public int ReadCalls => Volatile.Read(ref _readCalls);

        public IReadOnlyList<byte[]> WrittenFrames
        {
            get
            {
                lock (_writeGate)
                {
                    return _written.ToArray();
                }
            }
        }

        public void EnqueueInbound(ReadOnlyMemory<byte> bytes)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (bytes.IsEmpty)
            {
                return;
            }

            _ = _inbound.Writer.TryWrite(bytes.ToArray());
        }

        public void CompleteInbound() => _ = _inbound.Writer.TryComplete();

        /// <summary>After <paramref name="count"/> successful writes, further writes block until fail/release.</summary>
        public void AllowSuccessfulWritesThenBlock(int count)
        {
            lock (_writeGate)
            {
                _successfulWritesAllowed = count;
                _blockWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>Fails the next (or currently blocked) write with <paramref name="exception"/>.</summary>
        public void FailWrites(Exception? exception = null)
        {
            TaskCompletionSource? blocked;
            lock (_writeGate)
            {
                _writeFault = exception ?? new IOException("forced writer transport failure");
                blocked = _blockWrites;
                _blockWrites = null;
            }

            blocked?.TrySetException(_writeFault);
        }

        /// <summary>Fails reads with the supplied exception (reader-side terminal failure).</summary>
        public void FailReads(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            _ = _inbound.Writer.TryComplete(exception);
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            Interlocked.Increment(ref _readCalls);

            byte[] chunk;
            try
            {
                chunk = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }

            var copy = Math.Min(buffer.Length, chunk.Length);
            chunk.AsSpan(0, copy).CopyTo(buffer.Span);
            if (copy < chunk.Length)
            {
                // Remainder is dropped; tests enqueue whole frames sized under the read buffer.
            }

            return copy;
        }

        public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            Interlocked.Increment(ref _writeCalls);
            if (buffer.IsEmpty)
            {
                return 0;
            }

            Task? block;
            lock (_writeGate)
            {
                if (_writeFault is not null)
                {
                    throw _writeFault;
                }

                if (_successfulWritesCompleted >= _successfulWritesAllowed)
                {
                    _blockWrites ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    block = _blockWrites.Task;
                }
                else
                {
                    _successfulWritesCompleted++;
                    _written.Add(buffer.ToArray());
                    return buffer.Length;
                }
            }

            try
            {
                await block.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                lock (_writeGate)
                {
                    if (_writeFault is not null)
                    {
                        throw _writeFault;
                    }
                }

                throw;
            }

            lock (_writeGate)
            {
                if (_writeFault is not null)
                {
                    throw _writeFault;
                }

                _written.Add(buffer.ToArray());
                return buffer.Length;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return ValueTask.CompletedTask;
            }

            _ = _inbound.Writer.TryComplete();
            FailWrites(new ObjectDisposedException(nameof(ControllableCacheListenerTransport)));
            return ValueTask.CompletedTask;
        }
    }
}
