using System.Collections.Concurrent;
using System.Text;
using VectorNNTP.BackFiller.Retention;

namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// Dispatch result for one validated GetRequest.
/// </summary>
public enum CacheListenerDispatchKind
{
    /// <summary>Serve retained bytes.</summary>
    Found = 0,

    /// <summary>Missing or expired identity.</summary>
    NotFound = 1,

    /// <summary>Protocol error.</summary>
    Error = 2,
}

/// <summary>Handler result. Found payload remains valid until the session terminalizes the request.</summary>
public readonly record struct CacheListenerDispatchResult(
    CacheListenerDispatchKind Kind,
    ReadOnlyMemory<byte> FoundPayload,
    ListenerProtocolErrorCode ErrorCode)
{
    /// <summary>Creates a Found result.</summary>
    public static CacheListenerDispatchResult Found(ReadOnlyMemory<byte> payload) =>
        new(CacheListenerDispatchKind.Found, payload, default);

    /// <summary>Creates a NotFound result.</summary>
    public static CacheListenerDispatchResult NotFound() =>
        new(CacheListenerDispatchKind.NotFound, ReadOnlyMemory<byte>.Empty, default);

    /// <summary>Creates an Error result.</summary>
    public static CacheListenerDispatchResult Error(ListenerProtocolErrorCode errorCode) =>
        new(CacheListenerDispatchKind.Error, ReadOnlyMemory<byte>.Empty, errorCode);
}

/// <summary>Handles GetRequest using the process-wide retention authority.</summary>
public sealed class CacheListenerRetentionHandler
{
    private readonly IArticleRetentionAuthority _retention;
    private readonly ConcurrentDictionary<uint, ArticleLookupLease> _leases = new();

    /// <summary>Initializes a retention-backed handler.</summary>
    public CacheListenerRetentionHandler(IArticleRetentionAuthority retention)
    {
        ArgumentNullException.ThrowIfNull(retention);
        _retention = retention;
    }

    /// <summary>
    /// Looks up by the validated 32-byte MD5 payload. Expired and missing identities are NotFound.
    /// The lease is held until <see cref="Release"/> so payload memory stays valid through Found write and ReceiptAck.
    /// The article is not removed from retention when the lease is released.
    /// </summary>
    public CacheListenerDispatchResult HandleGetRequest(uint requestId, ReadOnlyMemory<byte> messageIdMd5Payload)
    {
        var md5Hex = Encoding.ASCII.GetString(messageIdMd5Payload.Span);
        var lookup = _retention.TryGetByMd5(md5Hex);
        if (lookup.Kind != ArticleLookupKind.Found || lookup.Lease is null)
        {
            lookup.Dispose();
            return CacheListenerDispatchResult.NotFound();
        }

        var lease = lookup.Lease;
        if (!_leases.TryAdd(requestId, lease))
        {
            lease.Dispose();
            return CacheListenerDispatchResult.Error(ListenerProtocolErrorCode.InternalError);
        }

        return CacheListenerDispatchResult.Found(lease.Payload);
    }

    /// <summary>Releases the lookup lease for <paramref name="requestId"/>. Does not evict the retained article.</summary>
    public void Release(uint requestId)
    {
        if (_leases.TryRemove(requestId, out var lease))
        {
            lease.Dispose();
        }
    }

    /// <summary>Gets how many RequestIds currently hold a lookup lease (tests).</summary>
    internal int HeldLeaseCount => _leases.Count;

    /// <summary>Gets whether <paramref name="requestId"/> currently owns a lookup lease (tests).</summary>
    internal bool HoldsLease(uint requestId) => _leases.ContainsKey(requestId);
}
