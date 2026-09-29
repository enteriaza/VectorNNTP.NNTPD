using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.Engine;

/// <summary>Logical article state in the durable article index.</summary>
/// <remarks>
/// Evicted is a logical tombstone only; it does not reclaim or rewrite segment bytes.
/// Physical reclamation is a separate future concern. Invalid marks indexed articles whose
/// physical bytes failed integrity validation or are otherwise unusable.
/// Incomplete accept work must not be modelled by additional values here — it lives in the journal.
/// </remarks>
public enum ArticleStorageState : byte
{
    /// <summary>Article is indexed as present and eligible for integrity-proven reads.</summary>
    Present = 1,

    /// <summary>Article is logically tombstoned; physical bytes may still exist in segments.</summary>
    Evicted = 2,

    /// <summary>Article is indexed but must not be served (integrity/unavailable).</summary>
    Invalid = 3,
}

/// <summary>Segment lifecycle for append-oriented immutable segment files.</summary>
/// <remarks>
/// Closed segments are immutable. Retired segments may become physically reclaimable only after
/// durable index relocation for every live article that previously referenced them has committed.
/// Physical reclamation itself is out of scope for Phase 1.5.
/// </remarks>
public enum SegmentState : byte
{
    /// <summary>Segment is the active append target.</summary>
    Active = 1,

    /// <summary>Segment is closed and immutable; still may hold live article ranges.</summary>
    Closed = 2,

    /// <summary>
    /// Segment is retired after durable compaction relocation; bytes may later be reclaimed.
    /// </summary>
    Retired = 3,
}

/// <summary>Journal / write-path pressure derived from <see cref="IArticleJournal.OutstandingRecoverableBytes"/>.</summary>
public enum StorageWritePressure : byte
{
    /// <summary>Outstanding recoverable bytes are below the soft limit.</summary>
    Normal = 0,

    /// <summary>At or above soft limit, below hard limit.</summary>
    Elevated = 1,

    /// <summary>At or above hard limit; Accept must reject writes.</summary>
    Critical = 2,
}

/// <summary>Outcome of <see cref="IArticleStorageEngine.AcceptAsync"/>.</summary>
public enum ArticleAcceptOutcome : byte
{
    /// <summary>
    /// Journal Accept (including ArtData) is durable; caller may proceed without waiting for SATA.
    /// </summary>
    Accepted = 1,

    /// <summary>Identical ArtId + ArtHash + ArtSize already exists or is outstanding.</summary>
    Duplicate = 2,

    /// <summary>Same ArtId with conflicting ArtHash/ArtSize; existing article was not overwritten.</summary>
    Conflict = 3,

    /// <summary>Rejected because journal pressure is critical.</summary>
    RejectedPressure = 4,

    /// <summary>Rejected because the record is not CanonicalV1 or fails basic validation.</summary>
    RejectedInvalid = 5,
}

/// <summary>Result of appending a staged journal event after Accept.</summary>
public enum JournalAppendOutcome : byte
{
    /// <summary>Event appended (or was already present identically — idempotent success).</summary>
    Applied = 1,

    /// <summary>Same sequence already has the same PhysicalWritten location (no-op success).</summary>
    IdempotentNoOp = 2,

    /// <summary>Same sequence already has a different PhysicalWritten location (fail-closed).</summary>
    Conflict = 3,

    /// <summary>Prerequisite missing (e.g. IndexCommitted without PhysicalWritten, unknown sequence).</summary>
    Rejected = 4,
}

/// <summary>Result of <see cref="IArticleIndex.TryRelocate"/>.</summary>
public enum ArticleRelocateOutcome : byte
{
    /// <summary>Authoritative location updated to <c>newLocation</c>.</summary>
    Relocated = 1,

    /// <summary>Already at <c>newLocation</c> with matching identity (idempotent).</summary>
    IdempotentNoOp = 2,

    /// <summary>Current location did not match <c>expectedLocation</c>.</summary>
    ExpectedLocationMismatch = 3,

    /// <summary>ArtHash/ArtSize did not match the Present entry.</summary>
    IdentityMismatch = 4,

    /// <summary>Article missing or not Present.</summary>
    NotPresent = 5,
}

/// <summary>Opaque segment identity.</summary>
/// <param name="Value">Monotonic or generated segment number.</param>
public readonly record struct SegmentId(ulong Value)
{
    /// <inheritdoc />
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Physical location of article bytes inside a segment.</summary>
/// <param name="SegmentId">Owning segment.</param>
/// <param name="Offset">Byte offset within the segment.</param>
/// <param name="Length">
/// Physical span length at <paramref name="Offset"/>. For the filesystem segment store this is the
/// full on-disk record length; for the memory fake it equals ArtSize. Must be ≥ ArtSize.
/// </param>
public readonly record struct StoredArticleLocation(SegmentId SegmentId, long Offset, int Length);

/// <summary>
/// Authoritative logical metadata for one stored article identity.
/// </summary>
/// <param name="ArtId">Canonical Vector article identity.</param>
/// <param name="ArtHash">XXH3-64 of <see cref="ArticleRecord.ArtData"/>.</param>
/// <param name="ArtSize">Canonical ArtData length.</param>
/// <param name="Location">Physical segment location when known.</param>
/// <param name="State">Logical article state.</param>
/// <param name="LastAccessUtc">
/// Soft access hint. May lag; must not be required for crash recovery or correctness.
/// </param>
public readonly record struct StoredArticleMetadata(
    ArticleId ArtId,
    ulong ArtHash,
    int ArtSize,
    StoredArticleLocation Location,
    ArticleStorageState State,
    DateTimeOffset LastAccessUtc);

/// <summary>
/// Segment catalogue entry with live/dead accounting and retirement fencing.
/// </summary>
/// <param name="SegmentId">Segment identity.</param>
/// <param name="State">Active, closed, or retired.</param>
/// <param name="Generation">Monotonic catalogue generation for fencing.</param>
/// <param name="SizeBytes">Total bytes written to the segment.</param>
/// <param name="LiveBytes">Bytes still referenced by Present index entries.</param>
/// <param name="DeadBytes">Bytes logically dead; not yet reclaimed.</param>
/// <param name="CreatedUtc">Segment creation time (UTC).</param>
/// <param name="ClosedUtc">Segment close time when closed/retired; null while active.</param>
public readonly record struct SegmentInfo(
    SegmentId SegmentId,
    SegmentState State,
    ulong Generation,
    long SizeBytes,
    long LiveBytes,
    long DeadBytes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ClosedUtc);

/// <summary>Result of an Accept attempt after journal Accept durability.</summary>
/// <param name="Outcome">Accept outcome.</param>
/// <param name="ArtId">Article identity from the request.</param>
/// <param name="Sequence">Journal sequence when <see cref="ArticleAcceptOutcome.Accepted"/>; otherwise 0.</param>
/// <param name="Reason">Optional ASCII-safe reason (no secrets).</param>
public readonly record struct ArticleAcceptResult(
    ArticleAcceptOutcome Outcome,
    ArticleId ArtId,
    ulong Sequence = 0,
    string? Reason = null)
{
    /// <summary>Creates an Accepted result (journal Accept durable, including ArtData).</summary>
    public static ArticleAcceptResult Accepted(ArticleId artId, ulong sequence) =>
        new(ArticleAcceptOutcome.Accepted, artId, sequence);

    /// <summary>Creates a Duplicate result.</summary>
    public static ArticleAcceptResult Duplicate(ArticleId artId) =>
        new(ArticleAcceptOutcome.Duplicate, artId, Reason: "duplicate");

    /// <summary>Creates a Conflict result.</summary>
    public static ArticleAcceptResult Conflict(ArticleId artId) =>
        new(ArticleAcceptOutcome.Conflict, artId, Reason: "conflict");

    /// <summary>Creates a pressure rejection.</summary>
    public static ArticleAcceptResult RejectedPressure(ArticleId artId) =>
        new(ArticleAcceptOutcome.RejectedPressure, artId, Reason: "journal-pressure");

    /// <summary>Creates an invalid-record rejection.</summary>
    public static ArticleAcceptResult RejectedInvalid(ArticleId artId, string reason) =>
        new(ArticleAcceptOutcome.RejectedInvalid, artId, Reason: reason);
}

/// <summary>Result of a proven article read.</summary>
/// <param name="Metadata">Index metadata (LastAccess may be a soft hint).</param>
/// <param name="ArtData">Canonical article bytes.</param>
public readonly record struct ArticleReadResult(
    StoredArticleMetadata Metadata,
    ReadOnlyMemory<byte> ArtData);

/// <summary>
/// Model A journal Accept record: identity plus canonical ArtData.
/// </summary>
/// <remarks>
/// Once Accept is durably appended, the article bytes and identity are reconstructible from
/// the journal alone without SATA. Schema version is <c>1</c>.
/// </remarks>
public sealed class JournalAcceptRecord
{
    /// <summary>Initializes an Accept record. Copies <paramref name="artData"/>.</summary>
    /// <param name="version">Wire/schema version. Current is <c>1</c>.</param>
    /// <param name="sequence">Monotonic journal sequence owning this accept.</param>
    /// <param name="artId">Accepted article identity.</param>
    /// <param name="artHash">Accepted ArtHash.</param>
    /// <param name="artSize">Accepted ArtSize (must equal <paramref name="artData"/> length).</param>
    /// <param name="acceptedUtc">Journal acceptance timestamp (UTC).</param>
    /// <param name="artData">Canonical article bytes (≤ 5 MiB).</param>
    public JournalAcceptRecord(
        int version,
        ulong sequence,
        ArticleId artId,
        ulong artHash,
        int artSize,
        DateTimeOffset acceptedUtc,
        ReadOnlyMemory<byte> artData)
    {
        if (artSize != artData.Length)
        {
            throw new ArgumentException("ArtSize must equal ArtData.Length.", nameof(artSize));
        }

        Version = version;
        Sequence = sequence;
        ArtId = artId;
        ArtHash = artHash;
        ArtSize = artSize;
        AcceptedUtc = acceptedUtc;
        ArtData = artData.ToArray();
    }

    /// <summary>Gets the schema version.</summary>
    public int Version { get; }

    /// <summary>Gets the journal sequence.</summary>
    public ulong Sequence { get; }

    /// <summary>Gets the article identity.</summary>
    public ArticleId ArtId { get; }

    /// <summary>Gets the ArtHash.</summary>
    public ulong ArtHash { get; }

    /// <summary>Gets the ArtSize.</summary>
    public int ArtSize { get; }

    /// <summary>Gets the accept timestamp.</summary>
    public DateTimeOffset AcceptedUtc { get; }

    /// <summary>Gets the durable canonical ArtData copy.</summary>
    public ReadOnlyMemory<byte> ArtData { get; }
}

/// <summary>Durable journal event: SATA location recorded for a sequence.</summary>
/// <param name="Version">Schema version. Current is <c>1</c>.</param>
/// <param name="Sequence">Owning Accept sequence.</param>
/// <param name="Location">Authoritative physical location for this sequence.</param>
public readonly record struct JournalPhysicalWrittenRecord(
    int Version,
    ulong Sequence,
    StoredArticleLocation Location);

/// <summary>Durable journal event: index Present commit completed for a sequence.</summary>
/// <param name="Version">Schema version. Current is <c>1</c>.</param>
/// <param name="Sequence">Owning Accept sequence.</param>
public readonly record struct JournalIndexCommittedRecord(int Version, ulong Sequence);

/// <summary>
/// Snapshot of one Accept sequence that has not yet received durable <c>IndexCommitted</c>.
/// </summary>
/// <param name="Accept">Durable Accept (includes ArtData).</param>
/// <param name="PhysicalWritten">PhysicalWritten when present; otherwise null.</param>
public readonly record struct JournalIncompleteSequence(
    JournalAcceptRecord Accept,
    JournalPhysicalWrittenRecord? PhysicalWritten);

/// <summary>
/// Intended durable compaction protocol (not implemented in Phase 1.5).
/// </summary>
/// <remarks>
/// <para>
/// CompactionBegin → copy live bytes into new segment(s) → validate → apply individual
/// <see cref="IArticleIndex.TryRelocate"/> calls → durable CompactionIndexCommitted →
/// durable CompactionRetired → only then may source segments become reclaimable.
/// </para>
/// <para>
/// CompactionIndexCommitted is the durable statement that the complete relocation set has
/// successfully been applied. It does not make a batch of TryRelocate calls magically atomic;
/// each TryRelocate remains individually correct/idempotent. Source segments stay valid until
/// CompactionRetired.
/// </para>
/// </remarks>
public static class ArticleCompactionProtocol
{
    /// <summary>Documentation anchor for CompactionBegin.</summary>
    public const string Begin = "CompactionBegin";

    /// <summary>Documentation anchor for CompactionIndexCommitted.</summary>
    public const string IndexCommitted = "CompactionIndexCommitted";

    /// <summary>Documentation anchor for CompactionRetired.</summary>
    public const string Retired = "CompactionRetired";
}
