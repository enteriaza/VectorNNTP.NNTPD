using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;

namespace VectorNNTP.StorageServer.Storage.Engine.Policy;

/// <summary>
/// Deterministic, read-only selection of compaction and reclamation victims from catalogue
/// snapshots (Phase 5A / 5F.2). Does not mutate storage, journal, index, or cache.
/// </summary>
/// <remarks>
/// <para>
/// Ordinary compaction eligibility:
/// <c>State == Closed</c> AND <c>SizeBytes &gt; 0</c> AND extent accounting is complete AND
/// <c>DeadBytes &gt;= MinimumDeadBytes</c> AND
/// <c>DeadBytes * 100 &gt;= SizeBytes * MinimumDeadRatio</c>.
/// <c>MinimumDeadRatio</c> is an integer percent from 0 to 100.
/// </para>
/// <para>
/// Normal compaction victim ordering among eligible segments:
/// highest dead ratio, then highest <see cref="SegmentInfo.DeadBytes"/>, then lowest
/// <see cref="SegmentId"/>.
/// </para>
/// <para>
/// Under admission pressure, ordering prefers highest <see cref="SegmentInfo.SizeBytes"/>
/// (physical recovery potential after retire+reclaim), then highest DeadBytes, then lowest
/// SegmentId — only among candidates whose entire <see cref="SegmentInfo.LiveBytes"/> fits
/// under MaxUtil + CompactionHeadroom. Zero-live Closed segments are feasible without
/// destination slack.
/// </para>
/// <para>
/// Reclamation victims are Retired segments only, ordered by lowest <see cref="SegmentId"/>.
/// Present-reference and physical-file checks remain the execution layer's responsibility.
/// </para>
/// </remarks>
public sealed class ArticleSegmentPolicy
{
    private readonly long _minimumDeadBytes;
    private readonly int _minimumDeadRatio;

    /// <summary>Creates a policy from bindable compaction options.</summary>
    /// <param name="options">Compaction thresholds; must not be null.</param>
    public ArticleSegmentPolicy(ArticleCompactionPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MinimumDeadBytes < 0 || options.MinimumDeadRatio is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _minimumDeadBytes = options.MinimumDeadBytes;
        _minimumDeadRatio = options.MinimumDeadRatio;
    }

    /// <summary>Creates a policy from explicit thresholds.</summary>
    /// <param name="minimumDeadBytes">Minimum absolute dead bytes (≥ 0).</param>
    /// <param name="minimumDeadRatio">Minimum dead percentage in <c>0..100</c>.</param>
    public ArticleSegmentPolicy(long minimumDeadBytes, int minimumDeadRatio)
    {
        if (minimumDeadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumDeadBytes));
        }

        if (minimumDeadRatio is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumDeadRatio));
        }

        _minimumDeadBytes = minimumDeadBytes;
        _minimumDeadRatio = minimumDeadRatio;
    }

    /// <summary>Configured minimum absolute dead bytes.</summary>
    public long MinimumDeadBytes => _minimumDeadBytes;

    /// <summary>Configured minimum dead percentage, from 0 to 100.</summary>
    public int MinimumDeadRatio => _minimumDeadRatio;

    /// <summary>
    /// Evaluates compaction eligibility for one segment without selecting among peers.
    /// </summary>
    public CompactionEligibility EvaluateCompaction(in SegmentInfo segment)
    {
        var deadRatio = ComputeDeadRatio(segment.SizeBytes, segment.DeadBytes);

        if (segment.State != SegmentState.Closed)
        {
            return new CompactionEligibility(
                false,
                CompactionEligibilityReason.NotClosed,
                segment.SegmentId,
                segment.State,
                segment.SizeBytes,
                segment.LiveBytes,
                segment.DeadBytes,
                deadRatio,
                _minimumDeadBytes,
                _minimumDeadRatio);
        }

        if (segment.SizeBytes <= 0)
        {
            return new CompactionEligibility(
                false,
                CompactionEligibilityReason.ZeroSize,
                segment.SegmentId,
                segment.State,
                segment.SizeBytes,
                segment.LiveBytes,
                segment.DeadBytes,
                deadRatio,
                _minimumDeadBytes,
                _minimumDeadRatio);
        }

        if (!segment.ExtentAccountingComplete)
        {
            return new CompactionEligibility(
                false,
                CompactionEligibilityReason.AccountingIncomplete,
                segment.SegmentId,
                segment.State,
                segment.SizeBytes,
                segment.LiveBytes,
                segment.DeadBytes,
                deadRatio,
                _minimumDeadBytes,
                _minimumDeadRatio);
        }

        if (segment.DeadBytes < _minimumDeadBytes)
        {
            return new CompactionEligibility(
                false,
                CompactionEligibilityReason.InsufficientDeadBytes,
                segment.SegmentId,
                segment.State,
                segment.SizeBytes,
                segment.LiveBytes,
                segment.DeadBytes,
                deadRatio,
                _minimumDeadBytes,
                _minimumDeadRatio);
        }

        if (!MeetsDeadRatio(segment.DeadBytes, segment.SizeBytes, _minimumDeadRatio))
        {
            return new CompactionEligibility(
                false,
                CompactionEligibilityReason.InsufficientDeadRatio,
                segment.SegmentId,
                segment.State,
                segment.SizeBytes,
                segment.LiveBytes,
                segment.DeadBytes,
                deadRatio,
                _minimumDeadBytes,
                _minimumDeadRatio);
        }

        return new CompactionEligibility(
            true,
            CompactionEligibilityReason.Eligible,
            segment.SegmentId,
            segment.State,
            segment.SizeBytes,
            segment.LiveBytes,
            segment.DeadBytes,
            deadRatio,
            _minimumDeadBytes,
            _minimumDeadRatio);
    }

    /// <summary>
    /// Selects at most one Closed compaction victim from a coherent catalogue snapshot.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when an eligible victim was selected; otherwise
    /// <see langword="false"/> with <paramref name="victim"/> left default.
    /// </returns>
    public bool TrySelectCompactionVictim(IReadOnlyList<SegmentInfo> snapshot, out SegmentInfo victim)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var selection = SelectCompactionVictim(snapshot);
        victim = selection.Victim;
        return selection.Selected;
    }

    /// <summary>
    /// Selects at most one Closed compaction victim using <see cref="ISegmentCatalogue.Snapshot"/>.
    /// </summary>
    public bool TrySelectCompactionVictim(ISegmentCatalogue catalogue, out SegmentInfo victim)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        return TrySelectCompactionVictim(catalogue.Snapshot(), out victim);
    }

    /// <summary>Selects a compaction victim and returns eligibility diagnostics for the winner.</summary>
    public CompactionVictimSelection SelectCompactionVictim(IReadOnlyList<SegmentInfo> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        SegmentInfo? best = null;
        CompactionEligibility bestEligibility = default;

        foreach (var entry in snapshot)
        {
            var eligibility = EvaluateCompaction(in entry);
            if (!eligibility.IsEligible)
            {
                continue;
            }

            if (best is null || CompareCompactionCandidates(entry, best.Value) < 0)
            {
                best = entry;
                bestEligibility = eligibility;
            }
        }

        if (best is null)
        {
            return new CompactionVictimSelection(Selected: false, Victim: default, Eligibility: default);
        }

        return new CompactionVictimSelection(Selected: true, Victim: best.Value, Eligibility: bestEligibility);
    }

    /// <summary>
    /// Selects a Closed compaction victim under admission pressure: eligible + compaction-headroom
    /// feasible, ordered by physical recovery potential (<see cref="SegmentInfo.SizeBytes"/>).
    /// </summary>
    public bool TrySelectPressureReliefCompactionVictim(
        IReadOnlyList<SegmentInfo> snapshot,
        in CapacityAdmissionPressureSnapshot pressure,
        out SegmentInfo victim)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var selection = SelectPressureReliefCompactionVictim(snapshot, in pressure);
        victim = selection.Victim;
        return selection.Selected;
    }

    /// <summary>
    /// Selects a Closed compaction victim under admission pressure using
    /// <see cref="ISegmentCatalogue.Snapshot"/>.
    /// </summary>
    public bool TrySelectPressureReliefCompactionVictim(
        ISegmentCatalogue catalogue,
        in CapacityAdmissionPressureSnapshot pressure,
        out SegmentInfo victim)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        return TrySelectPressureReliefCompactionVictim(catalogue.Snapshot(), in pressure, out victim);
    }

    /// <summary>
    /// Pressure-aware compaction victim selection with feasibility against MaxUtil + Headroom.
    /// </summary>
    public CompactionVictimSelection SelectPressureReliefCompactionVictim(
        IReadOnlyList<SegmentInfo> snapshot,
        in CapacityAdmissionPressureSnapshot pressure)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        SegmentInfo? best = null;
        CompactionEligibility bestEligibility = default;

        foreach (var entry in snapshot)
        {
            var eligibility = EvaluateCompaction(in entry);
            if (!eligibility.IsEligible)
            {
                continue;
            }

            if (!IsCompactionFeasibleUnderHeadroom(in entry, in pressure))
            {
                continue;
            }

            if (best is null || ComparePressureReliefCandidates(entry, best.Value) < 0)
            {
                best = entry;
                bestEligibility = eligibility;
            }
        }

        if (best is null)
        {
            return new CompactionVictimSelection(Selected: false, Victim: default, Eligibility: default);
        }

        return new CompactionVictimSelection(Selected: true, Victim: best.Value, Eligibility: bestEligibility);
    }

    /// <summary>
    /// Selects a Closed segment whose dead bytes can be physically reclaimed under usage pressure.
    /// Ignores ordinary <c>MinimumDeadBytes</c> and <c>MinimumDeadRatio</c> eligibility. Still requires
    /// the segment's live bytes to fit under MaximumUtilization + CompactionHeadroom.
    /// </summary>
    public bool TrySelectUsagePressureCompactionVictim(
        IReadOnlyList<SegmentInfo> snapshot,
        in CapacityAdmissionPressureSnapshot pressure,
        out SegmentInfo victim)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SegmentInfo? best = null;
        foreach (var entry in snapshot)
        {
            if (entry.State != SegmentState.Closed || entry.SizeBytes <= 0)
            {
                continue;
            }

            if (entry.DeadBytes <= 0 && entry.LiveBytes > 0)
            {
                continue;
            }

            if (!IsCompactionFeasibleUnderHeadroom(in entry, in pressure))
            {
                continue;
            }

            if (best is null
                || entry.DeadBytes > best.Value.DeadBytes
                || (entry.DeadBytes == best.Value.DeadBytes
                    && entry.SegmentId.Value < best.Value.SegmentId.Value))
            {
                best = entry;
            }
        }

        victim = best ?? default;
        return best is not null;
    }

    /// <summary>
    /// Catalogue-only preflight: whether this Closed segment's complete
    /// <see cref="SegmentInfo.LiveBytes"/> fits under MaxUtil + CompactionHeadroom
    /// (Phase 5F.2 / 5F.9), including <see cref="CapacityAdmissionPressureSnapshot.CheckpointReservedBytes"/>.
    /// Does not reserve bytes and does not read SATA.
    /// Zero-live Closed segments are immediately feasible (commit/retire/reclaim only).
    /// Finish requires every Present article to be copied before the source file can be
    /// reclaimed, so a single minimum record fitting is not sufficient.
    /// </summary>
    public static bool IsCompactionFeasibleUnderHeadroom(
        in SegmentInfo segment,
        in CapacityAdmissionPressureSnapshot pressure)
    {
        if (!pressure.CapacityAdmissionEnabled)
        {
            return true;
        }

        if (segment.LiveBytes <= 0)
        {
            return true;
        }

        var ceilingUtilization = pressure.MaximumUtilization + pressure.CompactionHeadroom;
        return ProcessLocalCapacityLedger.WouldFit(
            pressure.UsedBytes,
            pressure.ArticleReservedBytes,
            pressure.CompactionReservedBytes,
            pressure.TotalBytes,
            segment.LiveBytes,
            ceilingUtilization,
            pressure.CheckpointReservedBytes,
            pressure.JournalReservedBytes,
            pressure.IndexReservedBytes,
            pressure.CompactionJournalReservedBytes);
    }

    /// <summary>
    /// Selects at most one Retired reclamation victim from a coherent catalogue snapshot
    /// (lowest <see cref="SegmentId"/>).
    /// </summary>
    public bool TrySelectReclamationVictim(IReadOnlyList<SegmentInfo> snapshot, out SegmentInfo victim)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var selection = SelectReclamationVictim(snapshot);
        victim = selection.Victim;
        return selection.Selected;
    }

    /// <summary>
    /// Selects at most one Retired reclamation victim using <see cref="ISegmentCatalogue.Snapshot"/>.
    /// </summary>
    public bool TrySelectReclamationVictim(ISegmentCatalogue catalogue, out SegmentInfo victim)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        return TrySelectReclamationVictim(catalogue.Snapshot(), out victim);
    }

    /// <summary>Selects a reclamation victim (Retired, lowest SegmentId).</summary>
    public ReclamationVictimSelection SelectReclamationVictim(IReadOnlyList<SegmentInfo> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        SegmentInfo? best = null;
        foreach (var entry in snapshot)
        {
            if (entry.State != SegmentState.Retired)
            {
                continue;
            }

            if (best is null || entry.SegmentId.Value < best.Value.SegmentId.Value)
            {
                best = entry;
            }
        }

        return best is null
            ? new ReclamationVictimSelection(Selected: false, Victim: default)
            : new ReclamationVictimSelection(Selected: true, Victim: best.Value);
    }

    /// <summary>
    /// Dead ratio as <c>DeadBytes / SizeBytes</c>, or <c>0</c> when size is non-positive.
    /// </summary>
    public static double ComputeDeadRatio(long sizeBytes, long deadBytes)
    {
        if (sizeBytes <= 0)
        {
            return 0d;
        }

        return (double)deadBytes / sizeBytes;
    }

    /// <summary>
    /// Percent comparison: <c>deadBytes * 100 &gt;= sizeBytes * minimumDeadPercent</c>.
    /// <paramref name="minimumDeadPercent"/> is an integer from 0 to 100.
    /// Uses checked <see cref="long"/> multiplication and falls back to <see cref="decimal"/>
    /// when that product overflows.
    /// </summary>
    public static bool MeetsDeadRatio(long deadBytes, long sizeBytes, int minimumDeadPercent)
    {
        if (sizeBytes <= 0)
        {
            return false;
        }

        if (minimumDeadPercent <= 0)
        {
            return true;
        }

        if (deadBytes < 0 || minimumDeadPercent > 100)
        {
            return false;
        }

        try
        {
            return checked(deadBytes * 100) >= checked(sizeBytes * minimumDeadPercent);
        }
        catch (OverflowException)
        {
            return (decimal)deadBytes * 100 >= (decimal)sizeBytes * minimumDeadPercent;
        }
    }

    /// <summary>
    /// Ordering for compaction victims: higher dead ratio, then higher DeadBytes, then lower
    /// SegmentId. Returns negative when <paramref name="left"/> should win.
    /// </summary>
    internal static int CompareCompactionCandidates(in SegmentInfo left, in SegmentInfo right)
    {
        // higher dead ratio first: deadL/sizeL ? deadR/sizeR  <=> deadL*sizeR ? deadR*sizeL
        var leftProduct = (decimal)left.DeadBytes * right.SizeBytes;
        var rightProduct = (decimal)right.DeadBytes * left.SizeBytes;
        var ratioCmp = rightProduct.CompareTo(leftProduct);
        if (ratioCmp != 0)
        {
            return ratioCmp;
        }

        var deadCmp = right.DeadBytes.CompareTo(left.DeadBytes);
        if (deadCmp != 0)
        {
            return deadCmp;
        }

        return left.SegmentId.Value.CompareTo(right.SegmentId.Value);
    }

    /// <summary>
    /// Pressure-relief ordering: higher SizeBytes (physical reclaim potential), then higher
    /// DeadBytes, then lower SegmentId. Returns negative when <paramref name="left"/> should win.
    /// </summary>
    internal static int ComparePressureReliefCandidates(in SegmentInfo left, in SegmentInfo right)
    {
        var sizeCmp = right.SizeBytes.CompareTo(left.SizeBytes);
        if (sizeCmp != 0)
        {
            return sizeCmp;
        }

        var deadCmp = right.DeadBytes.CompareTo(left.DeadBytes);
        if (deadCmp != 0)
        {
            return deadCmp;
        }

        return left.SegmentId.Value.CompareTo(right.SegmentId.Value);
    }
}
