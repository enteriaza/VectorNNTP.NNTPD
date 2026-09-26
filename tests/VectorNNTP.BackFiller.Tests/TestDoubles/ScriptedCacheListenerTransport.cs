using System.Threading.Channels;
using VectorNNTP.BackFiller.Listener;

namespace VectorNNTP.BackFiller.Tests.TestDoubles;

internal sealed class ScriptedCacheListenerTransport : ICacheListenerTransport
{
    private readonly Channel<byte> _inbound = Channel.CreateUnbounded<byte>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly List<byte> _written = [];
    private readonly object _gate = new();
    private int _disposed;

    public TaskCompletionSource? ReadStarted { get; set; }

    public TaskCompletionSource? BlockRead { get; set; }

    public TaskCompletionSource? WriteStarted { get; set; }

    public TaskCompletionSource? BlockWrite { get; set; }

    public Exception? ReadException { get; set; }

    public Exception? WriteException { get; set; }

    public byte[] Written
    {
        get
        {
            lock (_gate)
            {
                return [.. _written];
            }
        }
    }

    public async Task EnqueueAsync(ReadOnlyMemory<byte> bytes)
    {
        var copy = bytes.ToArray();
        foreach (var value in copy)
        {
            await _inbound.Writer.WriteAsync(value).ConfigureAwait(false);
        }
    }

    public void CompleteInbound() => _inbound.Writer.TryComplete();

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        ReadStarted?.TrySetResult();
        if (BlockRead is not null)
        {
            await BlockRead.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (ReadException is not null)
        {
            throw ReadException;
        }

        var count = 0;
        while (count < buffer.Length && _inbound.Reader.TryRead(out var value))
        {
            buffer.Span[count++] = value;
        }

        if (count > 0)
        {
            return count;
        }

        try
        {
            if (!await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
        }
        catch (ChannelClosedException)
        {
            return 0;
        }

        while (count < buffer.Length && _inbound.Reader.TryRead(out var value))
        {
            buffer.Span[count++] = value;
        }

        return count;
    }

    public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        WriteStarted?.TrySetResult();
        if (BlockWrite is not null)
        {
            await BlockWrite.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (WriteException is not null)
        {
            throw WriteException;
        }

        lock (_gate)
        {
            _written.AddRange(buffer.ToArray());
        }

        return buffer.Length;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
