using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Memory;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Policy;

/// <summary>Phase 5A: deterministic compaction/reclamation victim policy (selection only).</summary>
public sealed class ArticleSegmentPolicyTests
{
    private static readonly DateTimeOffset Utc = new(2024, 8, 23, 7, 30, 10, TimeSpan.Zero);

    [Fact]
    public void A_EmptyCatalogue_NoVictim()
    {
        var policy = EnabledPolicy();
        Assert.False(policy.TrySelectCompactionVictim(Array.Empty<SegmentInfo>(), out _));
        Assert.False(policy.TrySelectReclamationVictim(Array.Empty<SegmentInfo>(), out _));
    }

    [Fact]
    public void B_ActiveNeverSelectedForCompaction()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0);
        var snapshot = new[]
        {
            Seg(1, SegmentState.Active, size: 100, live: 50, dead: 50),
        };
        Assert.False(policy.TrySelectCompactionVictim(snapshot, out _));
        Assert.Equal(
            CompactionEligibilityReason.NotClosed,
            policy.EvaluateCompaction(snapshot[0]).Reason);
    }

    [Fact]
    public void C_RetiredNeverSelectedForCompaction()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0);
        var snapshot = new[]
        {
            Seg(2, SegmentState.Retired, size: 100, live: 0, dead: 100),
        };
        Assert.False(policy.TrySelectCompactionVictim(snapshot, out _));
        Assert.Equal(
            CompactionEligibilityReason.NotClosed,
            policy.EvaluateCompaction(snapshot[0]).Reason);
    }

    [Fact]
    public void D_ClosedBelowMinimumDeadBytes_Rejected()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 1_000, minimumDeadRatio: 0);
        var segment = Seg(3, SegmentState.Closed, size: 10_000, live: 9_500, dead: 500);
        var eligibility = policy.EvaluateCompaction(segment);
        Assert.False(eligibility.IsEligible);
        Assert.Equal(CompactionEligibilityReason.InsufficientDeadBytes, eligibility.Reason);
        Assert.False(policy.TrySelectCompactionVictim([segment], out _));
    }

    [Fact]
    public void E_ClosedBelowMinimumDeadRatio_Rejected()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0.50);
        var segment = Seg(4, SegmentState.Closed, size: 10_000, live: 8_000, dead: 2_000);
        var eligibility = policy.EvaluateCompaction(segment);
        Assert.False(eligibility.IsEligible);
        Assert.Equal(CompactionEligibilityReason.InsufficientDeadRatio, eligibility.Reason);
        Assert.False(policy.TrySelectCompactionVictim([segment], out _));
    }

    [Fact]
    public void F_ClosedMeetingBothThresholds_Selected()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 1_000, minimumDeadRatio: 0.10);
        var segment = Seg(5, SegmentState.Closed, size: 10_000, live: 5_000, dead: 5_000);
        Assert.True(policy.TrySelectCompactionVictim([segment], out var victim));
        Assert.Equal(5UL, victim.SegmentId.Value);
        var eligibility = policy.EvaluateCompaction(segment);
        Assert.True(eligibility.IsEligible);
        Assert.Equal(CompactionEligibilityReason.Eligible, eligibility.Reason);
        Assert.Equal(0.5, eligibility.DeadRatio);
    }

    [Fact]
    public void G_H_MultipleEligible_HighestDeadRatioWins()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0);
        var lowRatio = Seg(10, SegmentState.Closed, size: 100, live: 70, dead: 30);
        var highRatio = Seg(20, SegmentState.Closed, size: 100, live: 20, dead: 80);
        Assert.True(policy.TrySelectCompactionVictim([lowRatio, highRatio], out var victim));
        Assert.Equal(20UL, victim.SegmentId.Value);
    }

    [Fact]
    public void I_DeadBytesTieBreak_WhenRatiosEqual()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0);
        // Same ratio 0.5; higher DeadBytes wins.
        var smaller = Seg(1, SegmentState.Closed, size: 100, live: 50, dead: 50);
        var larger = Seg(2, SegmentState.Closed, size: 200, live: 100, dead: 100);
        Assert.True(policy.TrySelectCompactionVictim([smaller, larger], out var victim));
        Assert.Equal(2UL, victim.SegmentId.Value);
    }

    [Fact]
    public void J_SegmentIdFinalTieBreak()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0);
        var a = Seg(30, SegmentState.Closed, size: 100, live: 50, dead: 50);
        var b = Seg(10, SegmentState.Closed, size: 100, live: 50, dead: 50);
        Assert.True(policy.TrySelectCompactionVictim([a, b], out var victim));
        Assert.Equal(10UL, victim.SegmentId.Value);
    }

    [Fact]
    public void K_CompletelyDeadClosed_SelectedViaNormalLifecycle()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 1, minimumDeadRatio: 0.01);
        var segment = Seg(7, SegmentState.Closed, size: 1000, live: 0, dead: 1000);
        Assert.True(policy.TrySelectCompactionVictim([segment], out var victim));
        Assert.Equal(7UL, victim.SegmentId.Value);
        Assert.Equal(0, victim.LiveBytes);
        Assert.Equal(victim.SizeBytes, victim.DeadBytes);
    }

    [Fact]
    public void L_Z_ZeroSize_Rejected_NoNaN()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0);
        var segment = Seg(8, SegmentState.Closed, size: 0, live: 0, dead: 0);
        var eligibility = policy.EvaluateCompaction(segment);
        Assert.False(eligibility.IsEligible);
        Assert.Equal(CompactionEligibilityReason.ZeroSize, eligibility.Reason);
        Assert.Equal(0d, eligibility.DeadRatio);
        Assert.False(double.IsNaN(eligibility.DeadRatio));
        Assert.False(double.IsInfinity(eligibility.DeadRatio));
        Assert.False(policy.TrySelectCompactionVictim([segment], out _));
    }

    [Fact]
    public void M_ZeroDead_NormallyIneligible()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 1, minimumDeadRatio: 0);
        var segment = Seg(9, SegmentState.Closed, size: 1000, live: 1000, dead: 0);
        Assert.Equal(
            CompactionEligibilityReason.InsufficientDeadBytes,
            policy.EvaluateCompaction(segment).Reason);
        Assert.False(policy.TrySelectCompactionVictim([segment], out _));
    }

    [Fact]
    public void N_MinimumDeadBytes_BoundaryInclusive()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 1000, minimumDeadRatio: 0);
        var below = Seg(1, SegmentState.Closed, size: 10_000, live: 9001, dead: 999);
        var exact = Seg(2, SegmentState.Closed, size: 10_000, live: 9000, dead: 1000);
        Assert.False(policy.EvaluateCompaction(below).IsEligible);
        Assert.True(policy.EvaluateCompaction(exact).IsEligible);
        Assert.True(policy.TrySelectCompactionVictim([below, exact], out var victim));
        Assert.Equal(2UL, victim.SegmentId.Value);
    }

    [Fact]
    public void O_MinimumDeadRatio_BoundaryInclusive()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0.10);
        var below = Seg(1, SegmentState.Closed, size: 1000, live: 901, dead: 99);
        var exact = Seg(2, SegmentState.Closed, size: 1000, live: 900, dead: 100);
        Assert.False(policy.EvaluateCompaction(below).IsEligible);
        Assert.Equal(CompactionEligibilityReason.InsufficientDeadRatio, policy.EvaluateCompaction(below).Reason);
        Assert.True(policy.EvaluateCompaction(exact).IsEligible);
        Assert.True(ArticleSegmentPolicy.MeetsDeadRatio(100, 1000, 0.10));
        Assert.False(ArticleSegmentPolicy.MeetsDeadRatio(99, 1000, 0.10));
    }

    [Fact]
    public void P_DisabledPolicy_NoVictim_EvenWhenEligibleAccounting()
    {
        var policy = new ArticleSegmentPolicy(enabled: false, minimumDeadBytes: 0, minimumDeadRatio: 0);
        var segment = Seg(1, SegmentState.Closed, size: 1000, live: 0, dead: 1000);
        Assert.Equal(CompactionEligibilityReason.PolicyDisabled, policy.EvaluateCompaction(segment).Reason);
        Assert.False(policy.TrySelectCompactionVictim([segment], out _));
    }

    [Fact]
    public void P_ZeroThresholds_Enabled_IncludesZeroDead()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0);
        var zeroDead = Seg(1, SegmentState.Closed, size: 1000, live: 1000, dead: 0);
        Assert.True(policy.EvaluateCompaction(zeroDead).IsEligible);
        Assert.True(policy.TrySelectCompactionVictim([zeroDead], out var victim));
        Assert.Equal(1UL, victim.SegmentId.Value);
    }

    [Fact]
    public void U_AccountingIncomplete_RejectsClosedSegment()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0);
        var segment = Seg(1, SegmentState.Closed, size: 1000, live: 0, dead: 1000) with
        {
            ExtentAccountingComplete = false,
        };
        Assert.Equal(CompactionEligibilityReason.AccountingIncomplete, policy.EvaluateCompaction(segment).Reason);
        Assert.False(policy.TrySelectCompactionVictim([segment], out _));
    }

    [Fact]
    public void Q_R_S_ReclamationSelectsOnlyRetired()
    {
        var policy = EnabledPolicy();
        var snapshot = new[]
        {
            Seg(1, SegmentState.Active, size: 100, live: 50, dead: 50),
            Seg(2, SegmentState.Closed, size: 100, live: 0, dead: 100),
            Seg(5, SegmentState.Retired, size: 100, live: 0, dead: 100),
            Seg(3, SegmentState.Retired, size: 50, live: 0, dead: 50),
        };
        Assert.True(policy.TrySelectReclamationVictim(snapshot, out var victim));
        Assert.Equal(3UL, victim.SegmentId.Value);
        Assert.Equal(SegmentState.Retired, victim.State);
    }

    [Fact]
    public void T_MultipleRetired_LowestSegmentId()
    {
        var policy = EnabledPolicy();
        var snapshot = new[]
        {
            Seg(40, SegmentState.Retired, size: 10, live: 0, dead: 10),
            Seg(12, SegmentState.Retired, size: 10, live: 0, dead: 10),
            Seg(25, SegmentState.Retired, size: 10, live: 0, dead: 10),
        };
        Assert.True(policy.TrySelectReclamationVictim(snapshot, out var victim));
        Assert.Equal(12UL, victim.SegmentId.Value);
    }

    [Fact]
    public void U_PolicyIsReadOnly_CatalogueUnchanged()
    {
        var catalogue = new MemorySegmentCatalogue();
        var closed = Seg(1, SegmentState.Closed, size: 1000, live: 100, dead: 900);
        var retired = Seg(2, SegmentState.Retired, size: 500, live: 0, dead: 500);
        catalogue.Upsert(closed);
        catalogue.Upsert(retired);
        var before = catalogue.Snapshot();

        var policy = EnabledPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0);
        Assert.True(policy.TrySelectCompactionVictim(catalogue, out _));
        Assert.True(policy.TrySelectReclamationVictim(catalogue, out _));

        var after = catalogue.Snapshot();
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[1], after[1]);
    }

    [Fact]
    public void V_W_SnapshotConsistency_RepeatedSelectionIdentical()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 100, minimumDeadRatio: 0.05);
        var snapshot = new[]
        {
            Seg(1, SegmentState.Active, size: 1000, live: 500, dead: 500),
            Seg(2, SegmentState.Closed, size: 1000, live: 400, dead: 600),
            Seg(3, SegmentState.Closed, size: 1000, live: 200, dead: 800),
            Seg(4, SegmentState.Retired, size: 1000, live: 0, dead: 1000),
        };

        Assert.True(policy.TrySelectCompactionVictim(snapshot, out var first));
        Assert.True(policy.TrySelectCompactionVictim(snapshot, out var second));
        Assert.Equal(first, second);
        Assert.Equal(3UL, first.SegmentId.Value);

        Assert.True(policy.TrySelectReclamationVictim(snapshot, out var r1));
        Assert.True(policy.TrySelectReclamationVictim(snapshot, out var r2));
        Assert.Equal(r1, r2);
        Assert.Equal(4UL, r1.SegmentId.Value);
    }

    [Fact]
    public void X_ChangingNonWinningSegment_DoesNotChangeWinner()
    {
        var policy = EnabledPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0);
        var winner = Seg(10, SegmentState.Closed, size: 100, live: 10, dead: 90);
        var other = Seg(20, SegmentState.Closed, size: 100, live: 80, dead: 20);
        Assert.True(policy.TrySelectCompactionVictim([winner, other], out var first));
        Assert.Equal(10UL, first.SegmentId.Value);

        var otherMutated = Seg(20, SegmentState.Closed, size: 100, live: 70, dead: 30);
        Assert.True(policy.TrySelectCompactionVictim([winner, otherMutated], out var second));
        Assert.Equal(10UL, second.SegmentId.Value);
    }

    [Fact]
    public void Y_ConstructorRejectsInvalidThresholds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ArticleSegmentPolicy(true, minimumDeadBytes: -1, minimumDeadRatio: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ArticleSegmentPolicy(true, minimumDeadBytes: 0, minimumDeadRatio: -0.01));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ArticleSegmentPolicy(true, minimumDeadBytes: 0, minimumDeadRatio: 1.01));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ArticleSegmentPolicy(true, minimumDeadBytes: 0, minimumDeadRatio: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ArticleSegmentPolicy(true, minimumDeadBytes: 0, minimumDeadRatio: double.PositiveInfinity));
    }

    [Fact]
    public void OptionsCtor_UsesBindableValues()
    {
        var options = new ArticleCompactionPolicyOptions
        {
            Enabled = true,
            MinimumDeadBytes = 42,
            MinimumDeadRatio = 0.25,
        };
        var policy = new ArticleSegmentPolicy(options);
        Assert.True(policy.Enabled);
        Assert.Equal(42, policy.MinimumDeadBytes);
        Assert.Equal(0.25, policy.MinimumDeadRatio);
    }

    private static ArticleSegmentPolicy EnabledPolicy(
        long minimumDeadBytes = ArticleCompactionPolicyOptions.DefaultMinimumDeadBytes,
        double minimumDeadRatio = ArticleCompactionPolicyOptions.DefaultMinimumDeadRatio) =>
        new(enabled: true, minimumDeadBytes, minimumDeadRatio);

    private static SegmentInfo Seg(
        ulong id,
        SegmentState state,
        long size,
        long live,
        long dead) =>
        new(
            new SegmentId(id),
            state,
            Generation: id,
            SizeBytes: size,
            LiveBytes: live,
            DeadBytes: dead,
            CreatedUtc: Utc,
            ClosedUtc: state == SegmentState.Active ? null : Utc,
            ExtentAccountingComplete: state == SegmentState.Closed);
}
