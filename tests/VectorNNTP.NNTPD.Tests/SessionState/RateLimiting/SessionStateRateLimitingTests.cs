using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.Authentication;
using VectorNNTP.NNTPD.SessionState;
using VectorNNTP.NNTPD.SessionState.RateLimiting;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.SessionState.RateLimiting;

public sealed class SessionStateRateLimitingTests
{
    private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress V4B = IPAddress.Parse("198.51.100.20");
    private const int RateMbps = 10;

    [Fact]
    public void Policy_RateZero_IsUnlimitedAndDoesNotRequireTracking()
    {
        var policy = new NntpAccountPolicy("alice", NntpAccountType.RateLimited, 0, 0, 10, 0, "c");
        Assert.False(policy.RequiresRateTracking);
        Assert.True(policy.RequiresAdmission);
        Assert.Equal(NntpAccountType.RateLimited, NntpAccountPolicy.MapAccountType('R'));
        Assert.Equal(NntpAccountType.RateLimited, NntpAccountPolicy.MapAccountType('r'));
    }

    [Fact]
    public void Policy_ByteAccount_DoesNotTrackRateEvenWhenMbpsIsSet()
    {
        var policy = new NntpAccountPolicy("alice", NntpAccountType.ByteLimited, 10, 100, 10, 0, "c");
        Assert.False(policy.RequiresRateTracking);
        Assert.Equal(NntpAccountType.ByteLimited, NntpAccountPolicy.MapAccountType('B'));
        Assert.Equal(NntpAccountType.ByteLimited, NntpAccountPolicy.MapAccountType('b'));
        Assert.Equal(NntpAccountType.ByteLimited, NntpAccountPolicy.MapAccountType('X'));
    }

    [Fact]
    public void Policy_RateAccount_RequiresAdmissionWhenOnlyRateIsSet()
    {
        var policy = new NntpAccountPolicy("alice", NntpAccountType.RateLimited, 10, 999, 0, 0, "c");
        Assert.True(policy.RequiresRateTracking);
        Assert.True(policy.RequiresAdmission);
    }

    [Fact]
    public void Allocator_TenThenSevenThenEight_UpdatesExistingCaps()
    {
        var rates = new AccountRateAllocator();
        var caps = Register(rates, "alice", RateMbps, 10);
        AssertCaps(caps, AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 10));

        Unregister(rates, "alice", caps, 3);
        rates.ObserveClusterSessionCount("alice", 7);
        AssertCaps(caps.Take(7), AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 7));

        var extra = new FakeCap();
        rates.Register("alice", "s10", extra, RateMbps);
        rates.ObserveClusterSessionCount("alice", 8);
        Assert.Equal(AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 8), extra.MaxSendBytesPerSecond);
        AssertCaps(caps.Take(7).Append(extra), AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 8));
        Assert.True(AccountRateFormula.AggregateDoesNotExceed(RateMbps, 8));
    }

    [Fact]
    public void Allocator_OneSession_GetsFullAccountRate()
    {
        var rates = new AccountRateAllocator();
        var cap = new FakeCap();
        rates.ObserveClusterSessionCount("alice", 1);
        rates.Register("alice", "s1", cap, RateMbps);
        Assert.Equal(1_250_000, cap.MaxSendBytesPerSecond);
    }

    [Fact]
    public void Allocator_ZeroSessions_DoesNotCreatePhantomCap()
    {
        var rates = new AccountRateAllocator();
        rates.ObserveClusterSessionCount("alice", 0);
        Assert.Equal(0, rates.LocalSessionCount("alice"));
        var cap = new FakeCap();
        rates.Register("alice", "s1", cap, RateMbps);
        rates.ObserveClusterSessionCount("alice", 1);
        Assert.Equal(1_250_000, cap.MaxSendBytesPerSecond);
    }

    [Fact]
    public void Allocator_RateZero_LeavesUnlimitedCap()
    {
        var rates = new AccountRateAllocator();
        var cap = new FakeCap();
        rates.Register("alice", "s1", cap, 0);
        Assert.Equal(0, cap.MaxSendBytesPerSecond);
        Assert.Equal(0, rates.LocalSessionCount("alice"));
    }

    [Fact]
    public async Task Distributed_ExistingLocalSessionsUpdateOnJoinAndLeave()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var caps = new FakeCap[10];
        for (var i = 0; i < 10; i++)
        {
            caps[i] = new FakeCap();
            Assert.Equal(
                SessionAdmissionResult.Success,
                await node.TryAdmitAsync("alice", "s" + i, V4A, sessionLimit: 10, srcIpLimit: 0, RateMbps));
            rates.Register("alice", "s" + i, caps[i], RateMbps);
        }

        AssertCaps(caps, 125_000);

        for (var i = 7; i < 10; i++)
        {
            rates.Unregister("alice", "s" + i);
            await node.ReleaseAsync("alice", "s" + i);
        }

        AssertCaps(caps.Take(7), AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 7));

        var extra = new FakeCap();
        Assert.Equal(
            SessionAdmissionResult.Success,
            await node.TryAdmitAsync("alice", "s10", V4A, 10, 0, RateMbps));
        rates.Register("alice", "s10", extra, RateMbps);
        Assert.Equal(AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 8), extra.MaxSendBytesPerSecond);
        AssertCaps(caps.Take(7).Append(extra), AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 8));
    }

    [Fact]
    public async Task Distributed_UsesActiveCount_NotSessionLimit()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var first = new FakeCap();
        var second = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s1", V4A, 10, 0, RateMbps));
        rates.Register("alice", "s1", first, RateMbps);
        Assert.Equal(1_250_000, first.MaxSendBytesPerSecond);
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s2", V4B, 10, 0, RateMbps));
        rates.Register("alice", "s2", second, RateMbps);
        Assert.Equal(625_000, first.MaxSendBytesPerSecond);
        Assert.Equal(625_000, second.MaxSendBytesPerSecond);
    }

    [Fact]
    public async Task Distributed_SessionLimitExceeded_DoesNotChangeExistingCaps()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var caps = await RegisterAdmitted(rates, node, 10);
        Assert.Equal(
            SessionAdmissionResult.SessionLimitExceeded,
            await node.TryAdmitAsync("alice", "overflow", V4A, 10, 0, RateMbps));
        AssertCaps(caps, 125_000);
        Assert.Equal(10, node.GetLocalSessionCount("alice"));
    }

    [Fact]
    public async Task Distributed_RateOnly_TracksSessionsWhenSessionLimitIsZero()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var first = new FakeCap();
        var second = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s1", V4A, 0, 0, RateMbps));
        rates.Register("alice", "s1", first, RateMbps);
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s2", V4A, 0, 0, RateMbps));
        rates.Register("alice", "s2", second, RateMbps);
        Assert.Equal(625_000, first.MaxSendBytesPerSecond);
        Assert.Equal(625_000, second.MaxSendBytesPerSecond);
        Assert.Equal(0, node.LocalHotPathAdmits);
        Assert.Equal(2, node.DistributedAdmits);
    }

    [Fact]
    public async Task MultiNode_OtherNodeConvergesOnRenew()
    {
        var membership = new InMemorySessionStateStore();
        var ratesA = new AccountRateAllocator();
        var ratesB = new AccountRateAllocator();
        var nodeA = CreateNode(membership, "nntpd01", ratesA);
        var nodeB = CreateNode(membership, "nntpd02", ratesB);
        var a1 = new FakeCap();
        var a2 = new FakeCap();
        var a3 = new FakeCap();
        var b1 = new FakeCap();
        var b2 = new FakeCap();

        Assert.Equal(SessionAdmissionResult.Success, await nodeA.TryAdmitAsync("alice", "a1", V4A, 10, 0, RateMbps));
        ratesA.Register("alice", "a1", a1, RateMbps);
        Assert.Equal(SessionAdmissionResult.Success, await nodeA.TryAdmitAsync("alice", "a2", V4A, 10, 0, RateMbps));
        ratesA.Register("alice", "a2", a2, RateMbps);
        Assert.Equal(SessionAdmissionResult.Success, await nodeA.TryAdmitAsync("alice", "a3", V4A, 10, 0, RateMbps));
        ratesA.Register("alice", "a3", a3, RateMbps);
        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("alice", "b1", V4B, 10, 0, RateMbps));
        ratesB.Register("alice", "b1", b1, RateMbps);
        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("alice", "b2", V4B, 10, 0, RateMbps));
        ratesB.Register("alice", "b2", b2, RateMbps);

        var three = AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 3);
        var five = AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 5);
        AssertCaps([a1, a2, a3], three);
        Assert.True(Allocated(b1, b2) + Allocated(a1, a2, a3) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));
        Assert.True(Allocated(b1, b2) < five);

        await nodeA.RenewLeasesAsync();
        AssertCaps([a1, a2, a3], five);
        Assert.True(Allocated(a1, a2, a3, b1, b2) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));

        ratesB.Unregister("alice", "b2");
        await nodeB.ReleaseAsync("alice", "b2");
        var four = AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 4);
        AssertCaps([a1, a2, a3], five);
        Assert.True(Allocated(a1, a2, a3, b1) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));

        await nodeA.RenewLeasesAsync();
        AssertCaps([a1, a2, a3], five);
        Assert.True(Allocated(a1, a2, a3, b1) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));
    }

    [Fact]
    public async Task MultiNode_JoinOnOtherNode_UpdatesLocalCapsOnRenew()
    {
        var membership = new InMemorySessionStateStore();
        var ratesA = new AccountRateAllocator();
        var ratesB = new AccountRateAllocator();
        var nodeA = CreateNode(membership, "nntpd01", ratesA);
        var nodeB = CreateNode(membership, "nntpd02", ratesB);
        var a1 = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await nodeA.TryAdmitAsync("alice", "a1", V4A, 10, 0, RateMbps));
        ratesA.Register("alice", "a1", a1, RateMbps);
        Assert.Equal(1_250_000, a1.MaxSendBytesPerSecond);

        var b1 = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("alice", "b1", V4B, 10, 0, RateMbps));
        ratesB.Register("alice", "b1", b1, RateMbps);
        Assert.Equal(1_250_000, a1.MaxSendBytesPerSecond);
        Assert.True(Allocated(b1) <= 3);
        Assert.True(Allocated(a1, b1) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));

        await nodeA.RenewLeasesAsync();
        Assert.Equal(625_000, a1.MaxSendBytesPerSecond);
        Assert.True(Allocated(a1, b1) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));
    }

    [Fact]
    public async Task CrossNodeJoin_TenMbps_DoesNotExceedAggregateBeforeRemoteReconcile()
    {
        var clock = new ManualClock();
        var membership = new InMemorySessionStateStore();
        var ratesA = new AccountRateAllocator(clock);
        var ratesB = new AccountRateAllocator(clock);
        var nodeA = CreateNode(membership, "nntpd01", ratesA);
        var nodeB = CreateNode(membership, "nntpd02", ratesB);

        var aCaps = new FakeCap[7];
        for (var i = 0; i < 7; i++)
        {
            aCaps[i] = new FakeCap();
            Assert.Equal(
                SessionAdmissionResult.Success,
                await nodeA.TryAdmitAsync("alice", "a" + i, V4A, 10, 0, RateMbps));
            ratesA.Register("alice", "a" + i, aCaps[i], RateMbps);
        }

        var seven = AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 7);
        AssertCaps(aCaps, seven);

        var b1 = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("alice", "b1", V4B, 10, 0, RateMbps));
        ratesB.Register("alice", "b1", b1, RateMbps);
        AssertCaps(aCaps, seven);
        Assert.Equal(AccountRateFormula.BlockedBytesPerSecond, b1.MaxSendBytesPerSecond);
        Assert.True(Allocated(aCaps) + Allocated(b1) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));

        await nodeA.RenewLeasesAsync();
        var eight = AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 8);
        AssertCaps(aCaps, eight);
        Assert.True(Allocated(aCaps) + Allocated(b1) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));

        clock.Advance(SessionStateDefaults.LeaseTtl);
        ratesB.ObserveClusterSessionCount("alice", 8);
        Assert.True(b1.MaxSendBytesPerSecond <= 0);
        AssertCaps(aCaps, eight);
        Assert.True(Allocated(aCaps) + Allocated(b1) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));
    }

    [Fact]
    public async Task ExactScenario_TenThenSevenThenEight_LocalAndTwoNodes()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var caps = await RegisterAdmitted(rates, node, 10);
        AssertCaps(caps, 125_000);
        Assert.True(10 * 125_000 <= AccountRateFormula.AccountBytesPerSecond(RateMbps));

        for (var i = 7; i < 10; i++)
        {
            rates.Unregister("alice", "s" + i);
            await node.ReleaseAsync("alice", "s" + i);
        }

        var seven = AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 7);
        AssertCaps(caps.Take(7), seven);

        var extra = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s10", V4A, 10, 0, RateMbps));
        rates.Register("alice", "s10", extra, RateMbps);
        var eight = AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 8);
        Assert.Equal(eight, extra.MaxSendBytesPerSecond);
        AssertCaps(caps.Take(7).Append(extra), eight);

        var clock = new ManualClock();
        var split = new InMemorySessionStateStore();
        var ratesA = new AccountRateAllocator(clock);
        var ratesB = new AccountRateAllocator(clock);
        var nodeA = CreateNode(split, "nntpd01", ratesA);
        var nodeB = CreateNode(split, "nntpd02", ratesB);
        var aCaps = new FakeCap[7];
        for (var i = 0; i < 7; i++)
        {
            aCaps[i] = new FakeCap();
            Assert.Equal(SessionAdmissionResult.Success, await nodeA.TryAdmitAsync("bob", "a" + i, V4A, 10, 0, RateMbps));
            ratesA.Register("bob", "a" + i, aCaps[i], RateMbps);
        }

        var bCaps = new FakeCap[3];
        for (var i = 0; i < 3; i++)
        {
            bCaps[i] = new FakeCap();
            Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("bob", "b" + i, V4B, 10, 0, RateMbps));
            ratesB.Register("bob", "b" + i, bCaps[i], RateMbps);
            Assert.True(Allocated(aCaps) + Allocated(bCaps.Take(i + 1)) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));
        }

        await nodeA.RenewLeasesAsync();
        AssertCaps(aCaps, 125_000);
        Assert.True(Allocated(aCaps) + Allocated(bCaps) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));
        clock.Advance(SessionStateDefaults.LeaseTtl);
        ratesB.ObserveClusterSessionCount("bob", 10);
        AssertCaps(aCaps, 125_000);
        Assert.True(Allocated(aCaps) + Allocated(bCaps) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));

        for (var i = 0; i < 3; i++)
        {
            ratesB.Unregister("bob", "b" + i);
            await nodeB.ReleaseAsync("bob", "b" + i);
        }

        AssertCaps(aCaps, 125_000);
        await nodeA.RenewLeasesAsync();
        AssertCaps(aCaps, seven);

        var rejoin = new FakeCap();
        Assert.Equal(SessionAdmissionResult.Success, await nodeB.TryAdmitAsync("bob", "b0", V4B, 10, 0, RateMbps));
        ratesB.Register("bob", "b0", rejoin, RateMbps);
        AssertCaps(aCaps, seven);
        Assert.True(Allocated(rejoin) <= 3);
        Assert.True(Allocated(aCaps) + Allocated(rejoin) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));
        await nodeA.RenewLeasesAsync();
        AssertCaps(aCaps, eight);
        clock.Advance(SessionStateDefaults.LeaseTtl);
        ratesB.ObserveClusterSessionCount("bob", 8);
        Assert.True(rejoin.MaxSendBytesPerSecond <= 0);
        AssertCaps(aCaps, eight);
        Assert.True(Allocated(aCaps) + Allocated(rejoin) <= AccountRateFormula.AccountBytesPerSecond(RateMbps));
    }

    [Fact]
    public async Task RapidJoinLeave_NeverExceedsAggregate()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var caps = new Dictionary<string, FakeCap>(StringComparer.Ordinal);
        for (var i = 0; i < 20; i++)
        {
            var id = "s" + (i % 7);
            if (caps.ContainsKey(id))
            {
                rates.Unregister("alice", id);
                await node.ReleaseAsync("alice", id);
                caps.Remove(id);
            }
            else
            {
                Assert.Equal(
                    SessionAdmissionResult.Success,
                    await node.TryAdmitAsync("alice", id, V4A, 10, 0, RateMbps));
                var cap = new FakeCap();
                rates.Register("alice", id, cap, RateMbps);
                caps[id] = cap;
            }

            if (caps.Count > 0)
            {
                var expected = AccountRateFormula.PerSessionBytesPerSecond(RateMbps, caps.Count);
                AssertCaps(caps.Values, expected);
                Assert.True(expected * caps.Count <= AccountRateFormula.AccountBytesPerSecond(RateMbps));
            }
        }
    }

    [Fact]
    public async Task SimultaneousJoins_ShareTheSameFloor()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var admits = Enumerable.Range(0, 10)
            .Select(i => node.TryAdmitAsync("alice", "s" + i, V4A, 10, 0, RateMbps).AsTask());
        var results = await Task.WhenAll(admits);
        Assert.All(results, static result => Assert.Equal(SessionAdmissionResult.Success, result));

        var caps = new FakeCap[10];
        for (var i = 0; i < 10; i++)
        {
            caps[i] = new FakeCap();
            rates.Register("alice", "s" + i, caps[i], RateMbps);
        }

        rates.ObserveClusterSessionCount("alice", 10);
        AssertCaps(caps, 125_000);
    }

    [Fact]
    public async Task ReleaseDuringRenew_ExistingCapsFollowReleaseTotal()
    {
        var membership = new InMemorySessionStateStore();
        var rates = new AccountRateAllocator();
        var node = CreateNode(membership, "nntpd01", rates);
        var caps = await RegisterAdmitted(rates, node, 10);
        membership.NotifyRenewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        membership.BlockRenew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renew = node.RenewLeasesAsync().AsTask();
        await membership.NotifyRenewStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        rates.Unregister("alice", "s9");
        await node.ReleaseAsync("alice", "s9");
        AssertCaps(caps.Take(9), AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 9));
        membership.BlockRenew.TrySetResult();
        await renew.WaitAsync(TimeSpan.FromSeconds(2));
        AssertCaps(caps.Take(9), AccountRateFormula.PerSessionBytesPerSecond(RateMbps, 9));
    }

    [Fact]
    public async Task FakeRedisWritePath_DoesNotEvaluateOnLimiterWrite()
    {
        var redis = new FakeRedisService();
        var store = new RedisSessionStateStore(redis);
        var rates = new AccountRateAllocator();
        var node = new DistributedSessionStateTracker(
            store,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01",
            TimeProvider.System,
            rates: rates);
        Assert.Equal(SessionAdmissionResult.Success, await node.TryAdmitAsync("alice", "s1", V4A, 10, 0, RateMbps));
        var evals = redis.Database.ScriptEvaluateCount;
        await using var inner = new MemoryStream();
        await using var limiter = new OutboundRateLimiter(inner, 0, leaveInnerOpen: true);
        rates.Register("alice", "s1", limiter, RateMbps);
        limiter.UpdateMaxSendBytesPerSecond(125_000);
        await limiter.WriteAsync("281 Authentication accepted\r\n"u8.ToArray());
        rates.ObserveClusterSessionCount("alice", 1);
        Assert.Equal(evals, redis.Database.ScriptEvaluateCount);

        await node.RenewLeasesAsync();
        Assert.Equal(evals + 1, redis.Database.ScriptEvaluateCount);
    }

    private static async Task<FakeCap[]> RegisterAdmitted(
        AccountRateAllocator rates,
        DistributedSessionStateTracker node,
        int count)
    {
        var caps = new FakeCap[count];
        for (var i = 0; i < count; i++)
        {
            caps[i] = new FakeCap();
            Assert.Equal(
                SessionAdmissionResult.Success,
                await node.TryAdmitAsync("alice", "s" + i, V4A, count, 0, RateMbps));
            rates.Register("alice", "s" + i, caps[i], RateMbps);
        }

        return caps;
    }

    private static FakeCap[] Register(AccountRateAllocator rates, string account, int rateMbps, int count)
    {
        var caps = new FakeCap[count];
        rates.ObserveClusterSessionCount(account, count);
        for (var i = 0; i < count; i++)
        {
            caps[i] = new FakeCap();
            rates.Register(account, "s" + i, caps[i], rateMbps);
        }

        return caps;
    }

    private static void Unregister(AccountRateAllocator rates, string account, FakeCap[] caps, int remove)
    {
        for (var i = caps.Length - remove; i < caps.Length; i++)
        {
            rates.Unregister(account, "s" + i);
        }
    }

    private static void AssertCaps(IEnumerable<FakeCap> caps, long expected)
    {
        foreach (var cap in caps)
        {
            Assert.Equal(expected, cap.MaxSendBytesPerSecond);
        }
    }

    private static long Allocated(params FakeCap[] caps) =>
        Allocated((IEnumerable<FakeCap>)caps);

    private static long Allocated(IEnumerable<FakeCap> caps) =>
        caps.Sum(static cap => AccountRateFormula.AllocatedBytesPerSecond(cap.MaxSendBytesPerSecond));

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(delta.Ticks);
            _utcNow += delta;
        }
    }

    private static DistributedSessionStateTracker CreateNode(
        ISessionStateStore membership,
        string nodeId,
        IAccountRateAllocator rates) =>
        new(
            membership,
            NullLogger<DistributedSessionStateTracker>.Instance,
            nodeId,
            TimeProvider.System,
            rates: rates);

    private sealed class FakeCap : IOutboundRateCap
    {
        public long MaxSendBytesPerSecond { get; private set; }

        public void UpdateMaxSendBytesPerSecond(long bytesPerSecond) => MaxSendBytesPerSecond = bytesPerSecond;
    }
}
