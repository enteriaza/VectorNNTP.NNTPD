using VectorNNTP.NNTPD.SessionState.RateLimiting;

namespace VectorNNTP.NNTPD.Tests.SessionState.RateLimiting;

public sealed class AccountRateFormulaTests
{
    [Fact]
    public void OneMbps_Is125000BytesPerSecond()
    {
        Assert.Equal(125_000, AccountRateFormula.BytesPerSecondPerMbps);
        Assert.Equal(125_000, AccountRateFormula.AccountBytesPerSecond(1));
        Assert.Equal(1_250_000, AccountRateFormula.AccountBytesPerSecond(10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ZeroOrNegativeMbps_IsUnlimitedSentinel(int rateMbps)
    {
        Assert.Equal(0, AccountRateFormula.AccountBytesPerSecond(rateMbps));
        Assert.Equal(0, AccountRateFormula.PerSessionBytesPerSecond(rateMbps, 1));
        Assert.True(AccountRateFormula.AggregateDoesNotExceed(rateMbps, 10));
    }

    [Fact]
    public void ZeroActiveSessions_HasNoAllocation()
    {
        Assert.Equal(0, AccountRateFormula.PerSessionBytesPerSecond(10, 0));
        Assert.True(AccountRateFormula.AggregateDoesNotExceed(10, 0));
    }

    [Theory]
    [InlineData(10, 1, 1_250_000)]
    [InlineData(10, 2, 625_000)]
    [InlineData(10, 5, 250_000)]
    [InlineData(10, 10, 125_000)]
    [InlineData(10, 7, 178_571)]
    [InlineData(10, 8, 156_250)]
    [InlineData(10, 3, 416_666)]
    public void PerSession_FloorsAccountBytesByActiveCount(int rateMbps, int sessions, long expected)
    {
        Assert.Equal(expected, AccountRateFormula.PerSessionBytesPerSecond(rateMbps, sessions));
        Assert.True(AccountRateFormula.AggregateDoesNotExceed(rateMbps, sessions));
        Assert.True(
            expected * sessions <= AccountRateFormula.AccountBytesPerSecond(rateMbps));
    }

    [Fact]
    public void UsesActiveCount_NotConfiguredSessionLimit()
    {
        Assert.Equal(625_000, AccountRateFormula.PerSessionBytesPerSecond(10, activeSessions: 2));
        Assert.NotEqual(125_000, AccountRateFormula.PerSessionBytesPerSecond(10, activeSessions: 2));
    }

    [Fact]
    public void AggregateNeverExceeds_ForEverySessionCountThroughLimit()
    {
        for (var sessions = 1; sessions <= 64; sessions++)
        {
            Assert.True(AccountRateFormula.AggregateDoesNotExceed(10, sessions));
        }
    }

    [Fact]
    public void ConservativeJoin_IsUnusedRemainderAfterPreviousSplit()
    {
        Assert.Equal(3, AccountRateFormula.ConservativeJoinBytesPerSecond(10, 7));
        Assert.Equal(0, AccountRateFormula.ConservativeJoinBytesPerSecond(10, 8));
        Assert.Equal(0, AccountRateFormula.ConservativeJoinBytesPerSecond(10, 10));
        Assert.Equal(0, AccountRateFormula.ConservativeJoinBytesPerSecond(0, 7));
    }

    [Fact]
    public void AccountBytesPerSecond_OverflowSafeForIntMaxMbps()
    {
        Assert.Equal((long)int.MaxValue * 125_000, AccountRateFormula.AccountBytesPerSecond(int.MaxValue));
    }

    [Fact]
    public void EqualShareCap_FloorZero_IsBlockedNotUnlimited()
    {
        Assert.Equal(0, AccountRateFormula.PerSessionBytesPerSecond(1, 200_000));
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, AccountRateFormula.EqualShareCap(1, 200_000));
        Assert.Equal(0, AccountRateFormula.EqualShareCap(0, 1));
        Assert.Equal(125_000, AccountRateFormula.EqualShareCap(1, 1));
    }
}
