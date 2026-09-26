namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Owned destuffed article bytes. Independent of the provider session after retrieval.
/// </summary>
public sealed class RetrievedArticle : IDisposable
{
    private byte[]? _bytes;

    /// <summary>Initializes an owner for <paramref name="bytes"/>.</summary>
    /// <param name="bytes">Exact destuffed article payload. Ownership transfers to this instance.</param>
    public RetrievedArticle(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _bytes = bytes;
    }

    /// <summary>Gets the destuffed article bytes.</summary>
    /// <exception cref="ObjectDisposedException">Thrown after <see cref="Dispose"/>.</exception>
    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            var bytes = _bytes ?? throw new ObjectDisposedException(nameof(RetrievedArticle));
            return bytes;
        }
    }

    /// <summary>Gets the payload length.</summary>
    public int Length => Memory.Length;

    /// <summary>
    /// Transfers ownership of the payload to the caller. After success this instance is empty
    /// and <see cref="Dispose"/> is a no-op.
    /// </summary>
    /// <param name="payload">Detached bytes when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when this instance still owned a payload.</returns>
    public bool TryDetach(out byte[] payload)
    {
        var bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is null)
        {
            payload = [];
            return false;
        }

        payload = bytes;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _ = Interlocked.Exchange(ref _bytes, null);
    }
}
