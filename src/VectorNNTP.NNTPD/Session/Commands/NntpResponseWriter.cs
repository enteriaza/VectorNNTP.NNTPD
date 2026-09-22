using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using VectorNNTP.NNTPD.Session.Framing;

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
/// <para>
/// Large article bodies use the WriteArticleAsync APIs: restuff → bounded owned chunks →
/// the same ordered Channel (no second TX pipeline; mode-independent).
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
    private long _channelEnqueueCount;
    private long _pipeFlushCount;
    private long _ownedPayloadBytesEnqueued;

    private readonly struct WriteRequest
    {
        public WriteRequest(byte[] payload, TaskCompletionSource? completed)
        {
            Payload = payload;
            Completed = completed;
        }

        /// <summary>Owned payload; valid until the pump finishes writing this item.</summary>
        public byte[] Payload { get; }

        public TaskCompletionSource? Completed { get; }
    }

    /// <summary>Initializes a new instance of the <see cref="NntpResponseWriter"/> class.</summary>
    public NntpResponseWriter(PipeWriter output)
        : this(output, channelCapacity: 4096)
    {
    }

    /// <summary>Test/harness constructor with an explicit bounded Channel capacity.</summary>
    internal NntpResponseWriter(PipeWriter output, int channelCapacity)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacity, 1);
        _output = output;
        _channel = Channel.CreateBounded<WriteRequest>(new BoundedChannelOptions(channelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _pump = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
    }

    /// <summary>Gets the number of Channel items accepted (diagnostics / tests).</summary>
    internal long ChannelEnqueueCount => Volatile.Read(ref _channelEnqueueCount);

    /// <summary>Gets the number of <see cref="PipeWriter.FlushAsync"/> calls from the TX pump.</summary>
    internal long PipeFlushCount => Volatile.Read(ref _pipeFlushCount);

    /// <summary>Gets total owned payload bytes accepted into the Channel.</summary>
    internal long OwnedPayloadBytesEnqueued => Volatile.Read(ref _ownedPayloadBytesEnqueued);

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

    /// <summary>
    /// Writes one framed article through the shared bounded article TX path (default 64 KiB chunks).
    /// </summary>
    /// <param name="storedDestuffedArticle">
    /// Stored de-stuffed article bytes (no terminating <c>.\r\n</c>); see <see cref="ArticleWireReconstructor"/>.
    /// </param>
    /// <param name="framing">Mode-independent framing (ARTICLE or TAKETHIS header).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Materialized restuff → owned chunks → existing ordered Channel. Does not inspect session mode.
    /// Awaits each chunk's flush into the outbound Pipe (not peer socket delivery).
    /// </remarks>
    public ValueTask WriteArticleAsync(
        ReadOnlyMemory<byte> storedDestuffedArticle,
        NntpArticleTxFraming framing,
        CancellationToken cancellationToken = default) =>
        WriteArticleAsync(
            storedDestuffedArticle,
            framing,
            NntpArticleTxChunkBudget.DefaultBytes,
            cancellationToken);

    /// <summary>
    /// Writes one framed article through the shared bounded article TX path with an explicit chunk budget.
    /// </summary>
    /// <param name="storedDestuffedArticle">Stored de-stuffed article bytes.</param>
    /// <param name="framing">Mode-independent framing.</param>
    /// <param name="chunkBytes">Production chunk budget (64–256 KiB).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask WriteArticleAsync(
        ReadOnlyMemory<byte> storedDestuffedArticle,
        NntpArticleTxFraming framing,
        int chunkBytes,
        CancellationToken cancellationToken = default)
    {
        NntpArticleTxChunkBudget.ThrowIfNotProductionRange(chunkBytes);
        return WriteArticleCoreAsync(storedDestuffedArticle, framing, chunkBytes, cancellationToken);
    }

    /// <summary>
    /// Test-only article TX allowing non-production chunk sizes for boundary coverage.
    /// </summary>
    internal ValueTask WriteArticleForTestsAsync(
        ReadOnlyMemory<byte> storedDestuffedArticle,
        NntpArticleTxFraming framing,
        int chunkBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);
        return WriteArticleCoreAsync(storedDestuffedArticle, framing, chunkBytes, cancellationToken);
    }

    /// <summary>
    /// Writes a customer ARTICLE response (full stored article) via the shared chunked TX path.
    /// </summary>
    /// <param name="storedDestuffedArticle">Full de-stuffed stored article (headers + body).</param>
    /// <param name="messageId">Message-id for the 220 status line.</param>
    /// <param name="articleNumber">Article number, or 0 when selected by message-id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask WriteCustomerArticleAsync(
        ReadOnlyMemory<byte> storedDestuffedArticle,
        string messageId,
        long articleNumber = 0,
        CancellationToken cancellationToken = default) =>
        WriteArticleAsync(
            storedDestuffedArticle,
            NntpArticleTxFraming.CustomerArticle(messageId, articleNumber),
            cancellationToken);

    /// <summary>
    /// Writes a customer BODY response (body only) via the shared chunked TX path.
    /// </summary>
    /// <param name="storedDestuffedArticle">
    /// Full de-stuffed stored article; headers are stripped at the first <c>\r\n\r\n</c>.
    /// When no blank line exists, an empty body is transmitted (still with 222 + terminator).
    /// </param>
    /// <param name="messageId">Message-id for the 222 status line.</param>
    /// <param name="articleNumber">Article number, or 0 when selected by message-id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Same Channel → pump → PipeWriter path as ARTICLE. Does not create a BODY-specific TX stack.
    /// </remarks>
    public ValueTask WriteCustomerBodyAsync(
        ReadOnlyMemory<byte> storedDestuffedArticle,
        string messageId,
        long articleNumber = 0,
        CancellationToken cancellationToken = default)
    {
        _ = ArticleWireReconstructor.TrySplitHeadersAndBody(
            storedDestuffedArticle.Span,
            out _,
            out var bodySpan);
        // Copy body span to owned memory before await (span cannot cross await).
        var body = bodySpan.IsEmpty ? ReadOnlyMemory<byte>.Empty : bodySpan.ToArray();
        return WriteArticleAsync(
            body,
            NntpArticleTxFraming.CustomerBody(messageId, articleNumber),
            cancellationToken);
    }

    /// <summary>
    /// Writes a customer BODY response from an already-separated body payload via the shared TX path.
    /// </summary>
    /// <param name="storedDestuffedBody">De-stuffed body bytes only (no headers; no terminator).</param>
    /// <param name="messageId">Message-id for the 222 status line.</param>
    /// <param name="articleNumber">Article number, or 0 when selected by message-id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask WriteCustomerBodyFromBodyAsync(
        ReadOnlyMemory<byte> storedDestuffedBody,
        string messageId,
        long articleNumber = 0,
        CancellationToken cancellationToken = default) =>
        WriteArticleAsync(
            storedDestuffedBody,
            NntpArticleTxFraming.CustomerBody(messageId, articleNumber),
            cancellationToken);

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

    private async ValueTask WriteArticleCoreAsync(
        ReadOnlyMemory<byte> storedDestuffedArticle,
        NntpArticleTxFraming framing,
        int chunkBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        var header = framing.BuildHeaderBytes();
        var body = ArticleWireReconstructor.RestuffArticle(storedDestuffedArticle.Span);
        var totalLength = header.Length + body.Length;
        if (totalLength == 0)
        {
            return;
        }

        var offset = 0;
        while (offset < totalLength)
        {
            var remaining = totalLength - offset;
            var take = Math.Min(chunkBytes, remaining);
            var chunk = new byte[take];
            CopyFramedWire(header, body, offset, chunk.AsSpan());
            offset += take;

            // Ownership of `chunk` transfers to the Channel; do not mutate after enqueue.
            await WriteAndAwaitFlushAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void CopyFramedWire(byte[] header, byte[] body, int absoluteOffset, Span<byte> destination)
    {
        var written = 0;
        while (written < destination.Length)
        {
            if (absoluteOffset < header.Length)
            {
                var fromHeader = Math.Min(header.Length - absoluteOffset, destination.Length - written);
                header.AsSpan(absoluteOffset, fromHeader).CopyTo(destination[written..]);
                absoluteOffset += fromHeader;
                written += fromHeader;
                continue;
            }

            var bodyOffset = absoluteOffset - header.Length;
            var fromBody = Math.Min(body.Length - bodyOffset, destination.Length - written);
            body.AsSpan(bodyOffset, fromBody).CopyTo(destination[written..]);
            absoluteOffset += fromBody;
            written += fromBody;
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
            Interlocked.Increment(ref _channelEnqueueCount);
            Interlocked.Add(ref _ownedPayloadBytesEnqueued, request.Payload.Length);
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

            Interlocked.Increment(ref _pipeFlushCount);

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
