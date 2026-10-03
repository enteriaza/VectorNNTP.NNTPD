namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Owned destuffed article bytes. Independent of the provider session after retrieval.
/// </summary>
internal sealed class RetrievedArticle : IDisposable
{
    private byte[]? _bytes;

    /// <summary>Initializes an owner for <paramref name="bytes"/>.</summary>
    /// <param name="bytes">Exact destuffed article payload. Ownership transfers to this instance.</param>
    internal RetrievedArticle(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _bytes = bytes;
    }

    /// <summary>Gets the destuffed article bytes.</summary>
    /// <exception cref="ObjectDisposedException">Thrown after <see cref="Dispose"/>.</exception>
    internal ReadOnlyMemory<byte> Memory
    {
        get
        {
            var bytes = _bytes ?? throw new ObjectDisposedException(nameof(RetrievedArticle));
            return bytes;
        }
    }

    /// <summary>Gets the payload length.</summary>
    internal int Length => Memory.Length;

    /// <inheritdoc />
    public void Dispose()
    {
        _ = Interlocked.Exchange(ref _bytes, null);
    }
}
