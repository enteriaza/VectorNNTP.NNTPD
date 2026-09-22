using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// Writes NNTP single-line and multi-line responses to a <see cref="PipeWriter"/> through a
/// single ordered outbound pump.
/// </summary>
/// <remarks>
/// <para>
/// All writes are serialized onto one pump so response bytes remain ordered. Ordinary
/// <see cref="WriteLineAsync"/> awaits pump flush completion (existing command semantics).
/// <see cref="EnqueueLineAsync"/> only waits until the line is accepted by the ordered queue,
/// so TAKETHIS can continue reading the next pipelined article without waiting for network
/// delivery of the prior 239/439.
/// </para>
/// </remarks>
public sealed class NntpResponseWriter : IAsyncDisposable
{
    private static readonly byte[] DotCrlf = ".\r\n"u8.ToArray();

    private readonly PipeWriter _output;
    private readonly Channel<WriteRequest> _channel;
    private readonly Task _pump;
    private readonly CancellationTokenSource _pumpCts = new();
    private int _disposed;

    private readonly struct WriteRequest
    {
        public WriteRequest(byte[] payload, TaskCompletionSource? completed)
        {
            Payload = payload;
            Completed = completed;
        }

        public byte[] Payload { get; }

        public TaskCompletionSource? Completed { get; }
    }

    /// <summary>Initializes a new instance of the <see cref="NntpResponseWriter"/> class.</summary>
    public NntpResponseWriter(PipeWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _channel = Channel.CreateBounded<WriteRequest>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _pump = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Writes a single-line response (<c>code text</c>), waiting until the ordered pump has
    /// flushed it into the outbound pipe (subject to pipe backpressure).
    /// </summary>
    public ValueTask WriteLineAsync(int code, string text, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(code);
        ArgumentNullException.ThrowIfNull(text);
        return WriteAndAwaitFlushAsync(EncodeLine(code, text), cancellationToken);
    }

    /// <summary>
    /// Enqueues a single-line response without waiting for outbound pipe/network flush.
    /// </summary>
    /// <remarks>
    /// Ordering is preserved. Returns when the ordered queue accepts the line (may wait if the
    /// response queue is saturated). Used by TAKETHIS so receive can proceed immediately.
    /// </remarks>
    public ValueTask EnqueueLineAsync(int code, string text, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(code);
        ArgumentNullException.ThrowIfNull(text);
        return EnqueueAsync(new WriteRequest(EncodeLine(code, text), completed: null), cancellationToken);
    }

    /// <summary>Begins a multi-line response and writes the initial status line.</summary>
    public async ValueTask WriteMultilineStartAsync(
        int code,
        string text,
        CancellationToken cancellationToken = default)
    {
        await WriteLineAsync(code, text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes one multi-line body line (applies dot-stuffing) without terminating the block.</summary>
    public ValueTask WriteMultilineDataAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.StartsWith('.'))
        {
            line = "." + line;
        }

        return WriteAndAwaitFlushAsync(EncodeAscii(line + "\r\n"), cancellationToken);
    }

    /// <summary>Terminates a multi-line response with <c>.CRLF</c> and flushes.</summary>
    public ValueTask WriteMultilineEndAsync(CancellationToken cancellationToken = default) =>
        WriteAndAwaitFlushAsync(DotCrlf.ToArray(), cancellationToken);

    /// <summary>
    /// Writes a precomputed byte payload to the session output pipe and flushes (honoring pipe backpressure).
    /// </summary>
    /// <remarks>
    /// Intended for BENCHIT reuse of an immutable wire buffer. Still uses the production
    /// ordered writer path; does not bypass transport pumps.
    /// </remarks>
    public ValueTask WriteBytesAndFlushAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) =>
        WriteAndAwaitFlushAsync(payload.ToArray(), cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _channel.Writer.TryComplete();
        await _pumpCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose.
        }
        finally
        {
            _pumpCts.Dispose();
        }
    }

    private async ValueTask WriteAndAwaitFlushAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueAsync(new WriteRequest(payload, completed), cancellationToken).ConfigureAwait(false);
        using var registration = cancellationToken.Register(static state =>
        {
            ((TaskCompletionSource)state!).TrySetCanceled();
        }, completed);
        await completed.Task.ConfigureAwait(false);
    }

    private async ValueTask EnqueueAsync(WriteRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        try
        {
            await _channel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            throw new ObjectDisposedException(nameof(NntpResponseWriter), ex);
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await WritePayloadToPipeAsync(request.Payload, cancellationToken).ConfigureAwait(false);
                    request.Completed?.TrySetResult();
                }
                catch (Exception ex)
                {
                    request.Completed?.TrySetException(ex);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Dispose path.
        }
        finally
        {
            while (_channel.Reader.TryRead(out var leftover))
            {
                leftover.Completed?.TrySetCanceled();
            }
        }
    }

    private async ValueTask WritePayloadToPipeAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var remaining = payload.AsMemory();
        while (!remaining.IsEmpty)
        {
            var memory = _output.GetMemory(Math.Min(remaining.Length, 64 * 1024));
            var toCopy = Math.Min(memory.Length, remaining.Length);
            remaining.Span[..toCopy].CopyTo(memory.Span);
            _output.Advance(toCopy);
            remaining = remaining[toCopy..];

            var flush = _output.FlushAsync(cancellationToken);
            FlushResult result;
            if (flush.IsCompletedSuccessfully)
            {
                result = flush.Result;
            }
            else
            {
                result = await flush.ConfigureAwait(false);
            }

            if (result.IsCanceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (result.IsCompleted)
            {
                throw new InvalidOperationException("NNTP output pipe completed while writing.");
            }
        }
    }

    private static byte[] EncodeLine(int code, string text) =>
        EncodeAscii($"{code} {text}\r\n");

    private static byte[] EncodeAscii(string text)
    {
        var byteCount = Encoding.ASCII.GetByteCount(text);
        var bytes = new byte[byteCount];
        Encoding.ASCII.GetBytes(text, bytes);
        return bytes;
    }
}
