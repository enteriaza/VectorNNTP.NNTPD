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
/// <para>
/// States: <see cref="Active"/> (writable) → <see cref="Closed"/> (immutable) →
/// <see cref="Retired"/> (terminal fencing after durable live relocation). Physical deletion
/// of retired segments is a later phase. There is no separate “Reclaiming” catalogue state —
/// eligibility is a predicate over Closed + live accounting (see
/// <see cref="SegmentLifecycle.IsReclaimable"/>).
/// </para>
/// <para>
/// Closed segments are immutable. Retired segments must never become writable again.
/// A segment that still has live Present articles must not be physically deleted.
/// </para>
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

/// <summary>Segment reclamation eligibility helpers (Phase 4A foundation; no physical delete).</summary>
public static class SegmentLifecycle
{
    /// <summary>
    /// Returns whether <paramref name="info"/> is eligible for physical reclamation work.
    /// </summary>
    /// <remarks>
    /// Only a <see cref="SegmentState.Closed"/> segment with <see cref="SegmentInfo.LiveBytes"/>
    /// equal to zero is reclaimable. Active segments are never reclaimable. Retired segments
    /// are already past rewrite fencing; their physical deletion is a later phase and is not
    /// expressed by this predicate. A positive LiveBytes value means Present articles still
    /// reference the segment.
    /// </remarks>
    public static bool IsReclaimable(in SegmentInfo info) =>
        info.State == SegmentState.Closed && info.LiveBytes == 0;
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

/// <summary>
/// Outcome of a single-article live relocation (<c>RelocateArticleAsync</c>).
/// </summary>
public enum ArticleRelocationOutcome : byte
{
    /// <summary>Index Present at destination; source physical record remains (now dead).</summary>
    Relocated = 1,

    /// <summary>Already Present at the recorded destination (idempotent success).</summary>
    IdempotentNoOp = 2,

    /// <summary>
    /// Relocation abandoned after durable Written (Evicted/Invalid/mismatch). Destination is dead.
    /// </summary>
    Abandoned = 3,

    /// <summary>Source segment missing from the catalogue.</summary>
    RejectedSourceMissing = 4,

    /// <summary>Source segment is not Closed.</summary>
    RejectedSourceNotClosed = 5,

    /// <summary>Source catalogue generation does not match the request / CompactionBegin fence.</summary>
    RejectedGenerationMismatch = 6,

    /// <summary>Article missing, not Present, or not located on the source segment.</summary>
    RejectedNotPresent = 7,

    /// <summary>Source physical record failed integrity proof.</summary>
    RejectedSourceCorrupt = 8,

    /// <summary>CompactionBegin missing, retired, or source/generation conflict.</summary>
    RejectedCompaction = 9,

    /// <summary>Journal append conflict (fail-closed body mismatch).</summary>
    Conflict = 10,
}

/// <summary>Result of <c>FileArticleStorageEngine.RelocateArticleAsync</c>.</summary>
/// <param name="Outcome">Relocation outcome.</param>
/// <param name="ArtId">Article identity.</param>
/// <param name="DestinationLocation">Destination when Written/relocated; otherwise null.</param>
/// <param name="Reason">Optional diagnostic reason.</param>
public readonly record struct ArticleRelocationResult(
    ArticleRelocationOutcome Outcome,
    ArticleId ArtId,
    StoredArticleLocation? DestinationLocation = null,
    string? Reason = null);

/// <summary>
/// Outcome of single-segment compaction orchestration (<c>CompactClosedSegmentAsync</c>).
/// </summary>
public enum ArticleCompactionOutcome : byte
{
    /// <summary>No Present index entry references the source; CompactionCommitted is durable.</summary>
    Committed = 1,

    /// <summary>
    /// Work finished without logical exhaustion (Present source references remain).
    /// CompactionCommitted was not appended.
    /// </summary>
    Incomplete = 2,

    /// <summary>Source segment missing from the catalogue.</summary>
    RejectedSourceMissing = 3,

    /// <summary>Source is Active or Retired (must be Closed).</summary>
    RejectedSourceNotClosed = 4,

    /// <summary>Multiple uncommitted open compactions already target this source.</summary>
    CompetingOpenCompaction = 5,

    /// <summary>Protocol/storage failure; CompactionCommitted was not appended.</summary>
    Failed = 6,
}

/// <summary>Result of <c>FileArticleStorageEngine.CompactClosedSegmentAsync</c>.</summary>
/// <param name="Outcome">Compaction outcome.</param>
/// <param name="CompactionId">Compaction transaction id (0 when rejected before Begin).</param>
/// <param name="SourceSegmentId">Source segment.</param>
/// <param name="SourceGeneration">Durable <c>CompactionBegin.SourceGeneration</c> (0 when rejected before Begin).</param>
/// <param name="InitialCandidateCount">Present@source count in the post-Begin worklist.</param>
/// <param name="RelocatedCount">Relocated + IdempotentNoOp outcomes.</param>
/// <param name="AbandonedCount">Abandoned / concurrent not-present outcomes.</param>
/// <param name="RemainingPresentOnSource">Present@source count after the worklist.</param>
/// <param name="CompactionCommittedAppended">True when this call appended CompactionCommitted.</param>
/// <param name="Reason">Optional diagnostic reason.</param>
public readonly record struct ArticleCompactionResult(
    ArticleCompactionOutcome Outcome,
    ulong CompactionId,
    SegmentId SourceSegmentId,
    ulong SourceGeneration,
    int InitialCandidateCount,
    int RelocatedCount,
    int AbandonedCount,
    int RemainingPresentOnSource,
    bool CompactionCommittedAppended,
    string? Reason = null);

/// <summary>
/// Outcome of durable segment retirement after CompactionCommitted
/// (<c>RetireCompactedSegmentAsync</c>).
/// </summary>
public enum ArticleSegmentRetirementOutcome : byte
{
    /// <summary>CompactionRetired is durable and catalogue/file lifecycle is Retired.</summary>
    Retired = 1,

    /// <summary>Already CompactionRetired + catalogue Retired (idempotent).</summary>
    IdempotentNoOp = 2,

    /// <summary>Unknown CompactionId.</summary>
    RejectedUnknownCompaction = 3,

    /// <summary>CompactionBegin present but CompactionCommitted missing.</summary>
    RejectedNotCommitted = 4,

    /// <summary>Present ArticleIndex entries still reference the source segment.</summary>
    RejectedPresentRemain = 5,

    /// <summary>Source is Active or otherwise not Closed for first-time retirement.</summary>
    RejectedSourceNotClosed = 6,

    /// <summary>Source missing from the catalogue.</summary>
    RejectedSourceMissing = 7,

    /// <summary>Journal/catalogue conflict or protocol failure.</summary>
    Failed = 8,
}

/// <summary>Result of <c>FileArticleStorageEngine.RetireCompactedSegmentAsync</c>.</summary>
/// <param name="Outcome">Retirement outcome.</param>
/// <param name="CompactionId">Compaction identity.</param>
/// <param name="SourceSegmentId">Source segment from CompactionBegin (default when unknown).</param>
/// <param name="SourceGeneration">Durable Begin generation (0 when unknown).</param>
/// <param name="CompactionRetiredAppended">True when this call appended CompactionRetired.</param>
/// <param name="Reason">Optional diagnostic reason.</param>
public readonly record struct ArticleSegmentRetirementResult(
    ArticleSegmentRetirementOutcome Outcome,
    ulong CompactionId,
    SegmentId SourceSegmentId,
    ulong SourceGeneration,
    bool CompactionRetiredAppended,
    string? Reason = null);

/// <summary>
/// Outcome of physical reclamation of a Retired segment (<c>ReclaimRetiredSegmentAsync</c>).
/// </summary>
public enum ArticleSegmentReclamationOutcome : byte
{
    /// <summary>Physical <c>.retired</c> file deleted and catalogue entry removed.</summary>
    Reclaimed = 1,

    /// <summary>Segment already absent (prior reclamation); no mutation required.</summary>
    IdempotentAlreadyReclaimed = 2,

    /// <summary>Catalogue entry missing while an unexpected file still exists.</summary>
    RejectedMissing = 3,

    /// <summary>Segment is Active.</summary>
    RejectedActive = 4,

    /// <summary>Segment is Closed (must be Retired).</summary>
    RejectedClosed = 5,

    /// <summary>Present ArticleIndex entries still reference the segment.</summary>
    RejectedPresentRemain = 6,

    /// <summary>Unexpected physical representation (not solely the expected <c>.retired</c> file).</summary>
    RejectedUnexpectedPhysical = 7,

    /// <summary>Delete/catalogue failure.</summary>
    Failed = 8,
}

/// <summary>Result of <c>FileArticleStorageEngine.ReclaimRetiredSegmentAsync</c>.</summary>
/// <param name="Outcome">Reclamation outcome.</param>
/// <param name="SegmentId">Target segment.</param>
/// <param name="PhysicalFileDeleted">True when this call deleted the <c>.retired</c> file.</param>
/// <param name="CatalogueEntryRemoved">True when this call removed the catalogue entry.</param>
/// <param name="Reason">Optional diagnostic reason.</param>
public readonly record struct ArticleSegmentReclamationResult(
    ArticleSegmentReclamationOutcome Outcome,
    SegmentId SegmentId,
    bool PhysicalFileDeleted,
    bool CatalogueEntryRemoved,
    string? Reason = null);

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
/// <param name="SizeBytes">
/// Physical segment file extent in bytes (valid length after torn-tail repair). This is the
/// on-disk size; logical eviction does not shrink it.
/// </param>
/// <param name="LiveBytes">
/// Bytes still referenced by Present index entries (sum of
/// <see cref="StoredArticleLocation.Length"/>). In-process; reconstructed from the article
/// index on engine open — not a durable catalogue database.
/// </param>
/// <param name="DeadBytes">
/// Bytes for Evicted/Invalid index entries on this segment (sum of location Length). Same
/// reconstruction rules as LiveBytes.
/// </param>
/// <param name="CreatedUtc">Segment creation time (UTC).</param>
/// <param name="ClosedUtc">Segment close time when closed/retired; null while active.</param>
/// <remarks>
/// Invariant: <c>SizeBytes &gt;= LiveBytes + DeadBytes</c>. Equality holds when every physical
/// record extent is covered by an index entry; unindexed orphan extents leave a gap.
/// </remarks>
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
/// Intended durable compaction protocol (Phase 4B.1 design; journal frames in Phase 4B.2).
/// </summary>
/// <remarks>
/// <para>
/// CompactionBegin → RelocationIntent → durable destination append → RelocationWritten →
/// <see cref="IArticleIndex.TryRelocate"/> (via <c>RelocateArticleAsync</c>) → (repeat) →
/// CompactionCommitted → CompactionRetired (physical retirement is a later phase).
/// </para>
/// <para>
/// CompactionCommitted asserts logical source exhaustion (no Present index entry references
/// the source segment). It does not rename or delete the source file.
/// </para>
/// </remarks>
public static class ArticleCompactionProtocol
{
    /// <summary>Documentation anchor for CompactionBegin.</summary>
    public const string Begin = "CompactionBegin";

    /// <summary>Documentation anchor for RelocationIntent.</summary>
    public const string RelocationIntent = "RelocationIntent";

    /// <summary>Documentation anchor for RelocationWritten.</summary>
    public const string RelocationWritten = "RelocationWritten";

    /// <summary>Documentation anchor for CompactionCommitted (logical exhaustion).</summary>
    public const string Committed = "CompactionCommitted";

    /// <summary>Documentation anchor for CompactionRetired (physical retirement later).</summary>
    public const string Retired = "CompactionRetired";

    /// <summary>Legacy alias for <see cref="Committed"/>.</summary>
    public const string IndexCommitted = Committed;
}

/// <summary>Opens a compaction against one Closed source segment generation.</summary>
/// <param name="Version">Schema version. Current is <c>1</c>.</param>
/// <param name="CompactionId">Globally monotonic compaction identity.</param>
/// <param name="SourceSegmentId">Closed source segment.</param>
/// <param name="SourceGeneration">Catalogue generation fence for the source.</param>
public readonly record struct JournalCompactionBeginRecord(
    int Version,
    ulong CompactionId,
    SegmentId SourceSegmentId,
    ulong SourceGeneration);

/// <summary>Durable intent to relocate one Present article from an exact source location.</summary>
/// <param name="Version">Schema version. Current is <c>1</c>.</param>
/// <param name="CompactionId">Owning compaction.</param>
/// <param name="RelocationId">Unique within <paramref name="CompactionId"/>.</param>
/// <param name="ArtId">Article identity.</param>
/// <param name="ArtHash">Expected ArtHash.</param>
/// <param name="ArtSize">Expected ArtSize.</param>
/// <param name="ExpectedSourceLocation">CAS key for <see cref="IArticleIndex.TryRelocate"/>.</param>
public readonly record struct JournalRelocationIntentRecord(
    int Version,
    ulong CompactionId,
    ulong RelocationId,
    ArticleId ArtId,
    ulong ArtHash,
    int ArtSize,
    StoredArticleLocation ExpectedSourceLocation);

/// <summary>Destination physical record has been durably written (index not yet implied).</summary>
/// <param name="Version">Schema version. Current is <c>1</c>.</param>
/// <param name="CompactionId">Owning compaction.</param>
/// <param name="RelocationId">Matching intent id.</param>
/// <param name="DestinationLocation">Durable destination extent.</param>
public readonly record struct JournalRelocationWrittenRecord(
    int Version,
    ulong CompactionId,
    ulong RelocationId,
    StoredArticleLocation DestinationLocation);

/// <summary>Logical source exhaustion: no Present index entry references the source segment.</summary>
/// <param name="Version">Schema version. Current is <c>1</c>.</param>
/// <param name="CompactionId">Owning compaction.</param>
public readonly record struct JournalCompactionCommittedRecord(int Version, ulong CompactionId);

/// <summary>Source segment retired under generation fence (physical op is a later phase).</summary>
/// <param name="Version">Schema version. Current is <c>1</c>.</param>
/// <param name="CompactionId">Owning compaction.</param>
/// <param name="SourceSegmentId">Retired source segment.</param>
/// <param name="ExpectedGeneration">Catalogue generation expected at retire.</param>
public readonly record struct JournalCompactionRetiredRecord(
    int Version,
    ulong CompactionId,
    SegmentId SourceSegmentId,
    ulong ExpectedGeneration);
