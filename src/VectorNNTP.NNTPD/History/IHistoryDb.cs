namespace VectorNNTP.NNTPD.History;

/// <summary>
/// Shared HistoryDB used by CHECK: local memory first, then Redis.
/// </summary>
public interface IHistoryDb
{
    /// <summary>
    /// Looks up <paramref name="messageId"/> wire octets. Memory hits do not touch Redis.
    /// </summary>
    ValueTask<HistoryLookupResult> LookupAsync(
        ReadOnlyMemory<byte> messageId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns whether the digest is currently present in the local store (tests).</summary>
    bool ContainsLocal(in HistoryDigest digest);
}
