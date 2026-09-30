namespace VectorNNTP.StorageServer.Storage.Engine.Policy;

/// <summary>Why a segment is or is not compaction-eligible under the configured policy.</summary>
public enum CompactionEligibilityReason : byte
{
    /// <summary>Closed, historically accounted, sized, and meets both dead-byte thresholds.</summary>
    Eligible = 1,

    /// <summary><c>StorageServer:Storage:Compaction:Enabled</c> is false.</summary>
    PolicyDisabled = 2,

    /// <summary>Segment state is not <see cref="SegmentState.Closed"/>.</summary>
    NotClosed = 3,

    /// <summary><see cref="SegmentInfo.SizeBytes"/> is ≤ 0 (ratio undefined).</summary>
    ZeroSize = 4,

    /// <summary><see cref="SegmentInfo.DeadBytes"/> is below <c>MinimumDeadBytes</c>.</summary>
    InsufficientDeadBytes = 5,

    /// <summary>Dead ratio is below <c>MinimumDeadRatio</c>.</summary>
    InsufficientDeadRatio = 6,

    /// <summary>
    /// Closed segment has not completed historical extent accounting.
    /// <see cref="SegmentInfo.DeadBytes"/> must not authorize a new compaction victim.
    /// </summary>
    AccountingIncomplete = 7,
}

/// <summary>Read-only compaction eligibility evaluation for one catalogue entry.</summary>
/// <param name="IsEligible">True when the segment may be selected as a compaction victim.</param>
/// <param name="Reason">Primary rejection/acceptance reason.</param>
/// <param name="SegmentId">Evaluated segment.</param>
/// <param name="State">Catalogue lifecycle state.</param>
/// <param name="SizeBytes">Physical valid size.</param>
/// <param name="LiveBytes">Present-referenced extents.</param>
/// <param name="DeadBytes">Evicted/Invalid-referenced extents.</param>
/// <param name="DeadRatio">
/// <c>DeadBytes / SizeBytes</c> when <paramref name="SizeBytes"/> &gt; 0; otherwise <c>0</c>.
/// </param>
/// <param name="MinimumDeadBytes">Configured absolute threshold used for this evaluation.</param>
/// <param name="MinimumDeadRatio">Configured ratio threshold used for this evaluation.</param>
public readonly record struct CompactionEligibility(
    bool IsEligible,
    CompactionEligibilityReason Reason,
    SegmentId SegmentId,
    SegmentState State,
    long SizeBytes,
    long LiveBytes,
    long DeadBytes,
    double DeadRatio,
    long MinimumDeadBytes,
    double MinimumDeadRatio);

/// <summary>Result of selecting at most one compaction victim from a catalogue snapshot.</summary>
/// <param name="Selected">True when a Closed eligible victim was chosen.</param>
/// <param name="Victim">Selected segment when <paramref name="Selected"/>; otherwise default.</param>
/// <param name="Eligibility">Eligibility detail for the selected victim when selected.</param>
public readonly record struct CompactionVictimSelection(
    bool Selected,
    SegmentInfo Victim,
    CompactionEligibility Eligibility);

/// <summary>Result of selecting at most one reclamation victim from a catalogue snapshot.</summary>
/// <param name="Selected">True when a Retired segment was chosen.</param>
/// <param name="Victim">Selected segment when <paramref name="Selected"/>; otherwise default.</param>
public readonly record struct ReclamationVictimSelection(
    bool Selected,
    SegmentInfo Victim);
