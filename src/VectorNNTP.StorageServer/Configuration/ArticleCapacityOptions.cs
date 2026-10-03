using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.StorageServer.Configuration;

/// <summary>
/// Process-local capacity admission under <c>StorageServer:Storage:Capacity</c>.
/// </summary>
/// <remarks>
/// <para>
/// Capacity management is always on. There is no switch that disables volume resolution,
/// admission ceilings, or usage-pressure recovery.
/// </para>
/// <para>
/// Percentages are integers from 0 to 100. <see cref="MaximumUsageCapacity"/> is the physical
/// usage trigger. <see cref="FreeCapacity"/> is the number of percentage points pressure
/// recovery must reclaim, so the physical recovery target is
/// <c>MaximumUsageCapacity - FreeCapacity</c>. <see cref="MaximumUtilization"/> is the hard
/// article-admission ceiling and is independent of the trigger.
/// <see cref="CompactionHeadroom"/> remains the utilisation delta added only for compaction
/// destination admission.
/// </para>
/// <para>
/// Reservations are process-local only. They are not kernel, cross-process, or cross-host
/// filesystem reservations. External writers and OS free-space races remain possible.
/// <c>SegmentDir</c> and <c>ControlDir</c> resolve to physical volumes. The same volume shares
/// one reader and one ledger. Different volumes use a segment ledger and a control ledger.
/// An unresolvable root fails closed.
/// </para>
/// <list type="bullet">
/// <item>
/// Article Accept reserves two independent amounts before the durable journal Accept.
/// The segment ledger reserves one segment copy of
/// <c>SegmentRecordCodec.RecordLengthForArtSize(ArtSize)</c>.
/// Each later physical append reserves another copy of that size before it writes.
/// A copy contributes to admission until its durable segment flush returns. After that flush
/// the written binding stays until physical reclaim and contributes zero to admission.
/// The control ledger reserves the journal sequence <c>ArtSize + 132</c>
/// (Accept + PhysicalWritten + IndexCommitted). That reservation stays through
/// IndexCommitted and is released only after a successful journal checkpoint installs a
/// replacement that omits that sequence.
/// The control ledger also reserves <c>ArticleIndexRecordCodec.RecordLength</c> bytes (96) before every durable index frame is appended.
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
/// <c>(Used + ArticleReserved + JournalReserved + IndexReserved + CompactionReserved + CompactionJournalReserved + CheckpointReserved + Required) ≤ MaximumUtilization percent of Total</c>.
/// Reaching <see cref="MaximumUsageCapacity"/> does not reject the article.
/// </item>
/// <item>
/// Compaction relocation destination append, on the segment ledger, counts a destination only
/// until its durable flush returns:
/// <c>(Used + ArticleReserved + CompactionReserved + CheckpointReserved + Required) ≤ (MaximumUtilization + CompactionHeadroom) percent of Total</c>
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
/// headroom), not a permanently reserved fraction of the volume and not the pressure-recovery amount.
/// </para>
/// <para>
/// Physical pressure uses filesystem <c>UsedBytes</c> and <c>TotalBytes</c>. Logical
/// <c>LiveBytes</c> and <c>DeadBytes</c> do not substitute for that usage. Dead bytes become
/// free space only after the segment is compacted, retired, and reclaimed.
/// </para>
/// </remarks>
public sealed class ArticleCapacityOptions
{
    /// <summary>Default hard article-admission ceiling (90 percent of the volume).</summary>
    public const int DefaultMaximumUtilization = 90;

    /// <summary>
    /// Default additional utilisation delta, in percentage points, available only to compaction
    /// destination appends.
    /// </summary>
    public const int DefaultCompactionHeadroom = 10;

    /// <summary>Default physical usage percent at which pressure recovery starts.</summary>
    public const int DefaultMaximumUsageCapacity = 80;

    /// <summary>
    /// Default percentage points of physical usage that pressure recovery must reclaim below
    /// <see cref="DefaultMaximumUsageCapacity"/>.
    /// </summary>
    public const int DefaultFreeCapacity = 5;

    /// <summary>
    /// Gets or sets the hard article-admission ceiling as a percent of volume <c>TotalBytes</c>.
    /// </summary>
    /// <remarks>
    /// A new article is not admitted when accepting it would exceed this percent. Pressure
    /// recovery is attempted first. Integer percent from 1 to 100.
    /// </remarks>
    [Range(0, 100)]
    public int MaximumUtilization { get; set; } = DefaultMaximumUtilization;

    /// <summary>
    /// Gets or sets the utilisation delta, in percentage points, added to
    /// <see cref="MaximumUtilization"/> for compaction destination append admission only.
    /// </summary>
    /// <remarks>
    /// Must be at least 1. <c>MaximumUtilization + CompactionHeadroom</c> must be at most 100.
    /// This is not the eviction target and not <see cref="FreeCapacity"/>.
    /// </remarks>
    [Range(0, 100)]
    public int CompactionHeadroom { get; set; } = DefaultCompactionHeadroom;

    /// <summary>
    /// Gets or sets the physical disk usage percent at which capacity-pressure recovery becomes active.
    /// </summary>
    /// <remarks>
    /// Compared with filesystem <c>UsedBytes / TotalBytes</c>. Must be below
    /// <see cref="MaximumUtilization"/>. Reaching this percent does not reject an article.
    /// </remarks>
    [Range(0, 100)]
    public int MaximumUsageCapacity { get; set; } = DefaultMaximumUsageCapacity;

    /// <summary>
    /// Gets or sets how many percentage points of physical usage pressure recovery must reclaim.
    /// </summary>
    /// <remarks>
    /// The physical recovery target percent is <c>MaximumUsageCapacity - FreeCapacity</c>.
    /// This is not a fraction of the article count. The articles evicted are whichever
    /// least-frequently-used Present articles contribute enough physical bytes.
    /// </remarks>
    [Range(0, 100)]
    public int FreeCapacity { get; set; } = DefaultFreeCapacity;
}
