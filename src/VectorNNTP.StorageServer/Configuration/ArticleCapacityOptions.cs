using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Process-local capacity admission under <c>StorageServer:Storage:Capacity</c>
/// (Phase 5E.1 / 5E.2).
/// </summary>
/// <remarks>
/// <para>
/// Reservations are process-local only. They are not kernel, cross-process, or cross-host
/// filesystem reservations. External writers and OS free-space races remain possible.
/// </para>
/// <para>
/// When <see cref="Enabled"/> is <see langword="false"/> (default), Accept, compaction destination
/// admission, and checkpoints do not consult capacity and do not resolve volumes.
/// When enabled, <c>SegmentDir</c> and <c>ControlDir</c> resolve to physical volumes.
/// The same volume shares one reader and one ledger. Different volumes use a segment ledger
/// and a control ledger. An unresolvable root fails closed.
/// </para>
/// <list type="bullet">
/// <item>
/// Article Accept reserves two independent amounts before the durable journal Accept.
/// The segment ledger reserves one segment copy of
/// <c>SegmentRecordCodec.RecordLengthForArtSize(ArtSize)</c>.
/// Each later physical append reserves another copy of that size before it writes.
/// Those reservations stay after durable PhysicalWritten until a later reclamation phase.
/// The control ledger reserves the journal sequence <c>ArtSize + 132</c>
/// (Accept + PhysicalWritten + IndexCommitted). That reservation stays through
/// IndexCommitted and is released only after a successful journal checkpoint installs a
/// replacement that omits that sequence.
/// The control ledger also reserves 88 bytes before every durable index frame is appended.
/// That includes Present, Evicted, Invalid, and a relocation's new Present frame. Each
/// reservation stays through IndexCommitted and logical state changes, and is released only
/// when an index checkpoint replacement retires that physical frame. Two physical frames for
/// one article reserve 176 bytes while both remain.
/// Each durable compaction-journal frame also reserves its exact length on the control ledger
/// before the append, admitted under MaximumUtilization + CompactionHeadroom:
/// CompactionBegin 36, RelocationIntent 92, RelocationWritten 48, CompactionCommitted 20,
/// and CompactionRetired 36. Those reservations stay until a journal checkpoint replacement
/// omits that entire compaction.
/// On a shared volume these amounts share one ledger. Admission is
/// <c>(Used + ArticleReserved + JournalReserved + IndexReserved + CompactionReserved + CompactionJournalReserved + CheckpointReserved + Required) ≤ MaximumUtilization × Total</c>
/// </item>
/// <item>
/// Compaction relocation destination append, on the segment ledger:
/// <c>(Used + ArticleReserved + CompactionReserved + CheckpointReserved + Required) ≤ (MaximumUtilization + CompactionHeadroom) × Total</c>
/// </item>
/// <item>
/// Checkpoint temporary files reserve their exact serialized length on the control ledger before
/// the temp file is created, and release that reservation once the extra file is gone.
/// Those reservations are checked against <see cref="MaximumUtilization"/>.
/// On a shared volume the control ledger is the segment ledger.
/// </item>
/// </list>
/// <para>
/// <see cref="CompactionHeadroom"/> is a utilization <em>delta</em> (temporary compaction admission
/// headroom), not a permanently reserved fraction of the volume.
/// </para>
/// </remarks>
public sealed class ArticleCapacityOptions
{
    /// <summary>Default maximum utilisation of the cache volume for article admission (80%).</summary>
    public const double DefaultMaximumUtilization = 0.80;

    /// <summary>
    /// Default additional utilisation delta available only to compaction destination appends (10%).
    /// </summary>
    public const double DefaultCompactionHeadroom = 0.10;

    /// <summary>
    /// Gets or sets whether process-local capacity admission is enabled.
    /// </summary>
    /// <remarks>Default <see langword="false"/> so existing deployments are unchanged.</remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum allowed utilisation for article Accept, including process-local
    /// article and compaction reservations and the candidate article's physical record length.
    /// </summary>
    /// <remarks>Must be finite and in the open interval <c>(0, 1)</c>.</remarks>
    [Range(double.Epsilon, 1.0 - double.Epsilon)]
    public double MaximumUtilization { get; set; } = DefaultMaximumUtilization;

    /// <summary>
    /// Gets or sets the utilisation delta added to <see cref="MaximumUtilization"/> for
    /// compaction destination append admission only.
    /// </summary>
    /// <remarks>
    /// Must be finite and <c>&gt; 0</c>, and
    /// <c>MaximumUtilization + CompactionHeadroom &lt; 1</c>.
    /// Default <see cref="DefaultCompactionHeadroom"/>.
    /// </remarks>
    public double CompactionHeadroom { get; set; } = DefaultCompactionHeadroom;
}
