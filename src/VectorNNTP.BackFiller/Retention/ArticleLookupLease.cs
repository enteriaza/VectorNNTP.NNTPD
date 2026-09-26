namespace VectorNNTP.BackFiller.Retention;

/// <summary>
/// Read lease over one retained payload. Dispose releases the lease; it does not dispose authority storage.
/// </summary>
public sealed class ArticleLookupLease : IDisposable
{
    private readonly Action? _release;
    private int _disposed;

    internal ArticleLookupLease(
        ArticleIdentity identity,
        string cacheUri,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset insertedUtc,
        DateTimeOffset expiresUtc,
        long generation,
        Action release)
    {
        Identity = identity;
        CacheUri = cacheUri;
        Payload = payload;
        InsertedUtc = insertedUtc;
        ExpiresUtc = expiresUtc;
        Generation = generation;
        _release = release;
    }

    /// <summary>Gets the retained identity.</summary>
    public ArticleIdentity Identity { get; }

    /// <summary>Gets the cache URI for this entry.</summary>
    public string CacheUri { get; }

    /// <summary>Gets the retained payload. Independent of provider sessions.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Gets the insertion timestamp.</summary>
    public DateTimeOffset InsertedUtc { get; }

    /// <summary>Gets the absolute expiry timestamp.</summary>
    public DateTimeOffset ExpiresUtc { get; }

    /// <summary>Gets the entry generation used to ignore stale sweeps.</summary>
    public long Generation { get; }

    /// <summary>Gets the payload length.</summary>
    public int Length => Payload.Length;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _release?.Invoke();
    }
}

/// <summary>Result of one lookup attempt.</summary>
/// <param name="Kind">Lookup classification.</param>
/// <param name="Lease">Lease when <see cref="ArticleLookupKind.Found"/>.</param>
public readonly record struct ArticleLookupResult(ArticleLookupKind Kind, ArticleLookupLease? Lease) : IDisposable
{
    /// <summary>Creates a found result.</summary>
    public static ArticleLookupResult Found(ArticleLookupLease lease) =>
        new(ArticleLookupKind.Found, lease);

    /// <summary>Creates a miss.</summary>
    public static ArticleLookupResult Missing() => new(ArticleLookupKind.Missing, null);

    /// <summary>Creates an expired miss.</summary>
    public static ArticleLookupResult Expired() => new(ArticleLookupKind.Expired, null);

    /// <inheritdoc />
    public void Dispose() => Lease?.Dispose();
}
