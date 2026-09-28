namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// Yields a fixed prefix from memory before delegating reads to an inner listener transport.
/// </summary>
public sealed class PrefixedCacheListenerTransport : ICacheListenerTransport
{
    private readonly ICacheListenerTransport _inner;
    private byte[]? _prefix;
    private int _prefixOffset;
    private int _disposed;

    /// <summary>Initializes a transport that serves <paramref name="leftover"/> before <paramref name="inner"/>.</summary>
    public PrefixedCacheListenerTransport(ICacheListenerTransport inner, ReadOnlyMemory<byte> leftover)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        if (!leftover.IsEmpty)
        {
            _prefix = leftover.ToArray();
        }
    }

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (_prefix is { } prefix && _prefixOffset < prefix.Length)
        {
            var available = prefix.Length - _prefixOffset;
            var take = Math.Min(available, buffer.Length);
            prefix.AsSpan(_prefixOffset, take).CopyTo(buffer.Span);
            _prefixOffset += take;
            if (_prefixOffset >= prefix.Length)
            {
                _prefix = null;
            }

            return ValueTask.FromResult(take);
        }

        return _inner.ReadAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
