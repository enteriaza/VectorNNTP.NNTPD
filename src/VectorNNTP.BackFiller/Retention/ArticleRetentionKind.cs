namespace VectorNNTP.BackFiller.Retention;

/// <summary>Outcome of one retention admission attempt.</summary>
public enum ArticleRetentionKind
{
    /// <summary>Ownership transferred; a new entry is retained.</summary>
    Retained = 0,

    /// <summary>
    /// The exact Message-ID is already retained. Incoming ArtData is not taken.
    /// The existing CanonicalV1 <c>ArticleRecord</c> remains authoritative (first-wins).
    /// </summary>
    AlreadyPresent = 1,

    /// <summary>
    /// Cache-URI ArticleId collides with a different retained Message-ID, or RequestId is bound
    /// to a different Message-ID. Hard reject. ArticleId here is Success URI metadata, not a transfer protocol.
    /// </summary>
    ArticleIdCollision = 2,

    /// <summary>The single ArtData buffer is larger than the entire configured capacity.</summary>
    PayloadExceedsCapacity = 3,

    /// <summary>
    /// Remaining capacity is insufficient after TTL reclaim and FIFO reclaim of entries with
    /// no openable RequestIds. Entries that still have openable Success capabilities are not
    /// FIFO-evicted to make room.
    /// </summary>
    CapacityUnavailable = 4,

    /// <summary>ArtData is empty or otherwise invalid for retention.</summary>
    InvalidPayload = 5,

    /// <summary>Admission is closed because the authority is shutting down.</summary>
    ShuttingDown = 6,

    /// <summary>
    /// The per-article openable RequestId bound was reached. Existing RequestIds are preserved;
    /// the new RequestId is not attached and must not receive ArticleWork Success.
    /// </summary>
    OpenableRequestIdLimitExceeded = 7,
}
