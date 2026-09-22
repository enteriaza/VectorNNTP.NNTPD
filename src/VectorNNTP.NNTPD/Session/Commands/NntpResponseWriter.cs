using System.IO.Pipelines;
using System.Text;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>Writes NNTP single-line and multi-line responses to a <see cref="PipeWriter"/>.</summary>
public sealed class NntpResponseWriter
{
    private static readonly byte[] DotCrlf = ".\r\n"u8.ToArray();

    private readonly PipeWriter _output;

    /// <summary>Initializes a new instance of the <see cref="NntpResponseWriter"/> class.</summary>
    public NntpResponseWriter(PipeWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>Writes a single-line response (<c>code text</c>) and flushes.</summary>
    public ValueTask WriteLineAsync(int code, string text, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(code);
        ArgumentNullException.ThrowIfNull(text);
        var line = $"{code} {text}\r\n";
        return WriteAsciiAndFlushAsync(line, cancellationToken);
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

        return WriteAsciiAndFlushAsync(line + "\r\n", cancellationToken);
    }

    /// <summary>Terminates a multi-line response with <c>.CRLF</c> and flushes.</summary>
    public ValueTask WriteMultilineEndAsync(CancellationToken cancellationToken = default)
    {
        var memory = _output.GetMemory(DotCrlf.Length);
        DotCrlf.CopyTo(memory.Span);
        _output.Advance(DotCrlf.Length);
        return FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes a precomputed byte payload to the session output pipe and flushes (honoring pipe backpressure).
    /// </summary>
    /// <remarks>
    /// Intended for BENCHIT reuse of an immutable wire buffer. Still uses the production
    /// <see cref="PipeWriter"/> path; does not bypass transport pumps.
    /// </remarks>
    public async ValueTask WriteBytesAndFlushAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        var remaining = payload;
        while (!remaining.IsEmpty)
        {
            var memory = _output.GetMemory(Math.Min(remaining.Length, 64 * 1024));
            var toCopy = Math.Min(memory.Length, remaining.Length);
            remaining.Span[..toCopy].CopyTo(memory.Span);
            _output.Advance(toCopy);
            remaining = remaining[toCopy..];

            var flush = _output.FlushAsync(cancellationToken);
            if (!flush.IsCompletedSuccessfully)
            {
                var result = await flush.ConfigureAwait(false);
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
    }

    private ValueTask WriteAsciiAndFlushAsync(string text, CancellationToken cancellationToken)
    {
        var byteCount = Encoding.ASCII.GetByteCount(text);
        var memory = _output.GetMemory(byteCount);
        var written = Encoding.ASCII.GetBytes(text, memory.Span);
        _output.Advance(written);
        return FlushAsync(cancellationToken);
    }

    private ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        var flush = _output.FlushAsync(cancellationToken);
        return flush.IsCompletedSuccessfully
            ? ValueTask.CompletedTask
            : new ValueTask(flush.AsTask());
    }
}
