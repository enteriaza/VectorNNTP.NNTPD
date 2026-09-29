namespace VectorNNTP.StorageServer.Storage.Engine.Memory;

/// <summary>Memory-only durable journal snapshot for cold-start recovery tests.</summary>
/// <param name="SoftLimitBytes">Journal soft pressure limit.</param>
/// <param name="HardLimitBytes">Journal hard pressure limit.</param>
/// <param name="NextSequence">Next sequence allocator value.</param>
/// <param name="OutstandingRecoverableBytes">Outstanding recoverable payload bytes.</param>
/// <param name="JournalPhysicalBytes">Physical journal occupancy counter.</param>
/// <param name="Sequences">All journal sequences in ascending order.</param>
public sealed record MemoryArticleJournalSnapshot(
    long SoftLimitBytes,
    long HardLimitBytes,
    ulong NextSequence,
    long OutstandingRecoverableBytes,
    long JournalPhysicalBytes,
    IReadOnlyList<MemoryJournalSequenceSnapshot> Sequences);

/// <summary>One sequence row in a <see cref="MemoryArticleJournalSnapshot"/>.</summary>
/// <param name="Accept">Accept record including ArtData.</param>
/// <param name="PhysicalWritten">PhysicalWritten when present.</param>
/// <param name="IndexCommittedRecord">IndexCommitted record when present.</param>
/// <param name="IndexCommitted">Whether IndexCommitted was recorded.</param>
/// <param name="Checkpointed">Whether the sequence was truncated from physical occupancy.</param>
public sealed record MemoryJournalSequenceSnapshot(
    JournalAcceptRecord Accept,
    JournalPhysicalWrittenRecord? PhysicalWritten,
    JournalIndexCommittedRecord? IndexCommittedRecord,
    bool IndexCommitted,
    bool Checkpointed);

/// <summary>Memory-only segment store snapshot for cold-start recovery tests.</summary>
/// <param name="NextSegmentId">Next segment id allocator.</param>
/// <param name="ActiveSegmentId">Active segment when set.</param>
/// <param name="AppendCount">Number of append operations performed before snapshot.</param>
/// <param name="Segments">Segment id → byte contents.</param>
public sealed record MemorySegmentStoreSnapshot(
    ulong NextSegmentId,
    ulong? ActiveSegmentId,
    long AppendCount,
    IReadOnlyDictionary<ulong, byte[]> Segments);
