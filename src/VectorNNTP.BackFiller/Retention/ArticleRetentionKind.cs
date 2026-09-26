namespace VectorNNTP.BackFiller.Retention;

/// <summary>Outcome of one retention admission attempt.</summary>
public enum ArticleRetentionKind
{
    /// <summary>Ownership transferred; a new entry is retained.</summary>
    Retained = 0,

    /// <summary>
    /// The exact Message-ID is already retained. Incoming payload is not taken.
    /// The existing entry remains authoritative (old first-wins rule).
    /// </summary>
    AlreadyPresent = 1,

    /// <summary>Canonical MD5 matches a different retained Message-ID. Hard reject.</summary>
    Md5Collision = 2,

    /// <summary>The single payload is larger than the entire configured capacity.</summary>
    PayloadExceedsCapacity = 3,

    /// <summary>Remaining capacity is insufficient after TTL reclaim and FIFO pressure eviction.</summary>
    CapacityUnavailable = 4,

    /// <summary>Payload is empty or otherwise invalid for retention.</summary>
    InvalidPayload = 5,

    /// <summary>Admission is closed because the authority is shutting down.</summary>
    ShuttingDown = 6,
}

/// <summary>Outcome of one retention lookup.</summary>
public enum ArticleLookupKind
{
    /// <summary>A live, unexpired entry was leased.</summary>
    Found = 0,

    /// <summary>No entry exists for the identity.</summary>
    Missing = 1,

    /// <summary>An entry existed but its TTL had elapsed. It is not returned.</summary>
    Expired = 2,
}
