using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.Engine;

/// <summary>
/// Append-only Model A durability journal on the NVMe control tier.
/// </summary>
/// <remarks>
/// <para>
/// Staged events: Accept (embeds ArtData) → PhysicalWritten → IndexCommitted.
/// Once Accept returns successfully, article bytes and identity are reconstructible from
/// the journal alone.
/// </para>
/// <para>
/// <see cref="OutstandingRecoverableBytes"/> drives pressure and counts ArtSize for Accepts
/// lacking IndexCommitted. <see cref="JournalPhysicalBytes"/> is physical journal occupancy
/// and may remain larger until checkpoint/truncation.
/// </para>
/// </remarks>
public interface IArticleJournal
{
    /// <summary>
    /// Payload bytes belonging to Accepts that do not yet have durable IndexCommitted.
    /// Pressure is based on this value.
    /// </summary>
    long OutstandingRecoverableBytes { get; }

    /// <summary>
    /// Actual bytes currently occupying journal storage (may include committed records
    /// retained until checkpoint/truncation). Must not be used as the pressure signal.
    /// </summary>
    long JournalPhysicalBytes { get; }

    /// <summary>Write pressure derived from <see cref="OutstandingRecoverableBytes"/>.</summary>
    StorageWritePressure Pressure { get; }

    /// <summary>
    /// Appends a durable Accept including ArtData. Completes only after Accept durability.
    /// </summary>
    ValueTask AppendAcceptAsync(JournalAcceptRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Appends PhysicalWritten for <paramref name="record"/>.Sequence.
    /// Same location is idempotent; a different location for the same sequence is Conflict.
    /// </summary>
    ValueTask<JournalAppendOutcome> AppendPhysicalWrittenAsync(
        JournalPhysicalWrittenRecord record,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends IndexCommitted. Requires a prior valid PhysicalWritten for the sequence.
    /// Releases outstanding recoverable pressure for that Accept's ArtSize.
    /// </summary>
    ValueTask<JournalAppendOutcome> AppendIndexCommittedAsync(
        JournalIndexCommittedRecord record,
        CancellationToken cancellationToken);

    /// <summary>Looks up an Accept that is not yet IndexCommitted.</summary>
    bool TryGetOutstanding(ArticleId artId, out JournalAcceptRecord record);

    /// <summary>
    /// Enumerates sequences whose IndexCommitted event has not been durably recorded,
    /// ordered by ascending Sequence.
    /// </summary>
    IReadOnlyList<JournalIncompleteSequence> EnumerateIncomplete();
}

/// <summary>Appendable active segment on the SATA tier.</summary>
public interface IAppendableSegment
{
    /// <summary>Gets the segment identity.</summary>
    SegmentId SegmentId { get; }

    /// <summary>Gets bytes already appended.</summary>
    long SizeBytes { get; }

    /// <summary>
    /// Appends immutable article bytes sequentially. Must not perform per-article random writes.
    /// </summary>
    ValueTask<StoredArticleLocation> AppendAsync(
        ReadOnlyMemory<byte> artData,
        CancellationToken cancellationToken);
}

/// <summary>
/// Segment store: active appender plus immutable reads of closed/active segments.
/// </summary>
/// <remarks>
/// Closed and retired segments are never mutated in place. Compaction writes new segments.
/// </remarks>
public interface ISegmentStore
{
    /// <summary>Gets or creates the current active appendable segment.</summary>
    ValueTask<IAppendableSegment> GetActiveAppenderAsync(CancellationToken cancellationToken);

    /// <summary>Closes the active segment, making it immutable.</summary>
    ValueTask CloseActiveAsync(CancellationToken cancellationToken);

    /// <summary>Reads article bytes at <paramref name="location"/>.</summary>
    bool TryRead(in StoredArticleLocation location, out ReadOnlyMemory<byte> artData);
}

/// <summary>
/// Durable logical article index. Physical database representation is deliberately undecided.
/// </summary>
public interface IArticleIndex
{
    /// <summary>Looks up metadata for <paramref name="artId"/>.</summary>
    bool TryGet(ArticleId artId, out StoredArticleMetadata metadata);

    /// <summary>
    /// Commits Present metadata after physical bytes are written and validated.
    /// Must not overwrite an existing Present article with conflicting ArtHash/ArtSize.
    /// </summary>
    bool TryCommitPresent(in StoredArticleMetadata metadata);

    /// <summary>
    /// Relocates a Present article from <paramref name="expectedLocation"/> to
    /// <paramref name="newLocation"/> when identity matches. Individual call correctness only;
    /// multi-article compaction atomicity is provided by durable compaction progress events,
    /// not by this method.
    /// </summary>
    ArticleRelocateOutcome TryRelocate(
        ArticleId artId,
        in StoredArticleLocation expectedLocation,
        in StoredArticleLocation newLocation,
        ulong artHash,
        int artSize);

    /// <summary>
    /// Applies a logical state transition to Evicted or Invalid without reclaiming bytes.
    /// </summary>
    bool TrySetState(ArticleId artId, ArticleStorageState state, DateTimeOffset utcNow);

    /// <summary>
    /// Best-effort soft LastAccess hint. Must not require a durable NVMe write.
    /// Loss across crash is acceptable; not required for recovery correctness.
    /// </summary>
    void TouchHint(ArticleId artId, DateTimeOffset utcNow);
}

/// <summary>
/// NVMe-resident segment catalogue with live/dead accounting and retirement fencing.
/// </summary>
public interface ISegmentCatalogue
{
    /// <summary>Looks up catalogue metadata for <paramref name="segmentId"/>.</summary>
    bool TryGet(SegmentId segmentId, out SegmentInfo info);

    /// <summary>
    /// Inserts or replaces a catalogue entry for a non-retired segment.
    /// </summary>
    /// <remarks>
    /// <see cref="SegmentState.Retired"/> is terminal for a <see cref="SegmentId"/>.
    /// Upsert must not resurrect a Retired segment to Active/Closed, and must not move
    /// <see cref="SegmentInfo.Generation"/> backwards. Implementations throw
    /// <see cref="InvalidOperationException"/> when those fencing rules are violated.
    /// </remarks>
    /// <param name="info">Catalogue entry to insert or update.</param>
    void Upsert(in SegmentInfo info);

    /// <summary>
    /// Marks a closed segment Retired after durable compaction relocation of its live articles.
    /// Does not physically reclaim bytes.
    /// </summary>
    bool TryRetire(SegmentId segmentId, ulong expectedGeneration, DateTimeOffset utcNow);

    /// <summary>Enumerates all known segments.</summary>
    IReadOnlyList<SegmentInfo> Snapshot();
}

/// <summary>
/// Append-oriented binary storage telemetry sink on NVMe.
/// </summary>
/// <remarks>
/// Must not participate in the Accept → PhysicalWritten → IndexCommitted durability chain.
/// </remarks>
public interface IStorageTelemetryLog
{
    /// <summary>Appends one opaque telemetry record.</summary>
    void Append(ReadOnlySpan<byte> record);

    /// <summary>Gets how many records were appended (tests / diagnostics).</summary>
    long RecordCount { get; }
}

/// <summary>
/// StorageServer-local article storage engine facade.
/// </summary>
/// <remarks>
/// Accept returns after durable journal Accept (Model A, ArtData embedded). SATA persistence
/// and index commitment happen asynchronously via staged journal events. Crash recovery is
/// engine-owned and not an article-facing API.
/// </remarks>
public interface IArticleStorageEngine
{
    /// <summary>
    /// Journal-first accept of a CanonicalV1 <see cref="ArticleRecord"/>.
    /// Returns once Accept (including ArtData) is durable in the journal.
    /// </summary>
    Task<ArticleAcceptResult> AcceptAsync(ArticleRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a Present article and proves ArtSize / ArtHash / ArtId against ArtData.
    /// Failed proof transitions the article to Invalid and returns false.
    /// Must not require a durable write solely to update LastAccess.
    /// </summary>
    bool TryRead(ArticleId artId, out ArticleReadResult result);

    /// <summary>
    /// Logical eviction (tombstone). Does not reclaim or rewrite segment data.
    /// </summary>
    bool TryEvict(ArticleId artId);

    /// <summary>
    /// Marks an article Invalid (unavailable) without reclaiming segment bytes.
    /// </summary>
    bool TryInvalidate(ArticleId artId);

    /// <summary>Current write pressure from outstanding recoverable journal bytes.</summary>
    StorageWritePressure GetWritePressure();
}

/// <summary>
/// Engine-owned crash recovery. Not an article-facing API.
/// </summary>
/// <remarks>
/// Accept only → reconstruct ArtData from journal → new SATA append → PhysicalWritten →
/// index commit → IndexCommitted. Accept+PhysicalWritten → prove → index commit →
/// IndexCommitted. IndexCommitted → no-op. Never invents PhysicalWritten from guessed SATA
/// tails; unrecovered prior appends are dead/unreferenced.
/// </remarks>
public interface IArticleStorageRecovery
{
    /// <summary>Replays incomplete journal sequences into physical + index state.</summary>
    Task RecoverAsync(CancellationToken cancellationToken);
}
