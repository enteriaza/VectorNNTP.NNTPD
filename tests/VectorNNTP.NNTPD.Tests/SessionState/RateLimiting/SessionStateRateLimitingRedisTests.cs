using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.SessionState;
using VectorNNTP.NNTPD.SessionState.RateLimiting;
using VectorNNTP.NNTPD.Tests.Fixtures;

namespace VectorNNTP.NNTPD.Tests.SessionState.RateLimiting;

public sealed class SessionStateRateLimitingRedisTests : IClassFixture<SessionStateRedisIntegrationFixture>
{
    private const string IpA = "192.0.2.10";
    private const string IpB = "198.51.100.20";
    private const long Now = 10_000;
    private const long Lease = 30_000;
    private static readonly IPAddress V4A = IPAddress.Parse(IpA);
    private static readonly IPAddress V4B = IPAddress.Parse(IpB);
    private readonly SessionStateRedisIntegrationFixture _redis;

    public SessionStateRateLimitingRedisTests(SessionStateRedisIntegrationFixture redis)
    {
        _redis = redis;
    }

    [SessionStateRedisIntegrationFact]
    public async Task AdmitPacksSessionTotal_AndReleaseReturnsRemaining()
    {
        var account = await _redis.CreateAccountAsync();
        var first = await _redis.TryAdmitAsync(account, IpA, "nntpd01:a", 10, 0, 1, 0, Now, Lease);
        Assert.True(first.Accepted);
        Assert.Equal(1, first.SessionTotal);
        var second = await _redis.TryAdmitAsync(account, IpA, "nntpd02:b", 10, 0, 1, 0, Now, Lease);
        Assert.True(second.Accepted);
        Assert.Equal(2, second.SessionTotal);
        Assert.Equal(1, await _redis.ReleaseAsync(account, IpA, "nntpd02:b", 1, 0));
        Assert.Equal(0, await _redis.ReleaseAsync(account, IpA, "nntpd01:a", 1, 0));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task RateOnlyAdmit_TracksSessionsWhenLimitIsZero()
    {
        var account = await _redis.CreateAccountAsync();
        var first = await _redis.TryAdmitAsync(account, IpA, "nntpd01:a", 0, 0, 1, 0, Now, Lease, trackSessions: true);
        var second = await _redis.TryAdmitAsync(account, IpB, "nntpd02:b", 0, 0, 1, 0, Now, Lease, trackSessions: true);
        Assert.Equal(1, first.SessionTotal);
        Assert.Equal(2, second.SessionTotal);
        Assert.Equal(2, await _redis.SessionCountAsync(account));
        Assert.Equal(1, await _redis.ReleaseAsync(account, IpB, "nntpd02:b", 1, 0));
        Assert.Equal(1, await _redis.SessionCountAsync(account));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task Renew_ReturnsUnexpiredSessionTotal()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(account, IpA, "nntpd01:a", 10, 0, 1, 0, Now, Lease)).Accepted);
        Assert.True((await _redis.TryAdmitAsync(account, IpB, "nntpd02:b", 10, 0, 1, 0, Now, Lease)).Accepted);
        var renewed = await _redis.RenewAsync(account, "nntpd01:a", 1, Array.Empty<(string, long)>(), 20_000, Lease);
        Assert.Equal(SessionStateRenewStatus.Renewed, renewed.Status);
        Assert.Equal(2, renewed.SessionTotal);
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task ConcurrentAdmitAndRelease_KeepsExactCount()
    {
        var account = await _redis.CreateAccountAsync();
        var admits = Enumerable.Range(0, 10)
            .Select(i => _redis.TryAdmitAsync(account, IpA, "node:" + i, 10, 0, 1, 0, Now, Lease).AsTask());
        var results = await Task.WhenAll(admits);
        Assert.Equal(10, results.Count(static result => result.Accepted));
        Assert.All(
            results.Where(static result => result.Accepted),
            static result => Assert.InRange(result.SessionTotal, 1, 10));
        Assert.Equal(10, await _redis.SessionCountAsync(account));

        var releases = Enumerable.Range(0, 3)
            .Select(i => _redis.ReleaseAsync(account, IpA, "node:" + i, 1, 0).AsTask());
        var remaining = await Task.WhenAll(releases);
        Assert.Equal(7, await _redis.SessionCountAsync(account));
        Assert.All(remaining, static total => Assert.InRange(total, 7, 9));

        Assert.True((await _redis.TryAdmitAsync(account, IpA, "node:rejoin", 10, 0, 1, 0, Now, Lease)).Accepted);
        Assert.Equal(8, await _redis.SessionCountAsync(account));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task TrackerNodes_ShareClusterCountAndCaps()
    {
        var account = await _redis.CreateAccountAsync();
        var ratesA = new AccountRateAllocator();
        var ratesB = new AccountRateAllocator();
        var nodeA = new DistributedSessionStateTracker(
            _redis.Store,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd01",
            TimeProvider.System,
            rates: ratesA);
        var nodeB = new DistributedSessionStateTracker(
            _redis.Store,
            NullLogger<DistributedSessionStateTracker>.Instance,
            "nntpd02",
            TimeProvider.System,
            rates: ratesB);

        var aCaps = new FakeCap[3];
        for (var i = 0; i < 3; i++)
        {
            aCaps[i] = new FakeCap();
            Assert.Equal(
                SessionAdmissionResult.Success,
                await nodeA.TryAdmitAsync(account, "a" + i, V4A, 10, 0, 10));
            ratesA.Register(account, "a" + i, aCaps[i], 10);
        }

        var bCaps = new FakeCap[2];
        for (var i = 0; i < 2; i++)
        {
            bCaps[i] = new FakeCap();
            Assert.Equal(
                SessionAdmissionResult.Success,
                await nodeB.TryAdmitAsync(account, "b" + i, V4B, 10, 0, 10));
            ratesB.Register(account, "b" + i, bCaps[i], 10);
        }

        var three = AccountRateFormula.PerSessionBytesPerSecond(10, 3);
        var five = AccountRateFormula.PerSessionBytesPerSecond(10, 5);
        Assert.All(aCaps, cap => Assert.Equal(three, cap.MaxSendBytesPerSecond));
        Assert.True(
            aCaps.Sum(static cap => AccountRateFormula.AllocatedBytesPerSecond(cap.MaxSendBytesPerSecond))
            + bCaps.Sum(static cap => AccountRateFormula.AllocatedBytesPerSecond(cap.MaxSendBytesPerSecond))
            <= AccountRateFormula.AccountBytesPerSecond(10));
        Assert.All(bCaps, cap => Assert.True(cap.MaxSendBytesPerSecond < five));

        await nodeA.RenewLeasesAsync();
        Assert.All(aCaps, cap => Assert.Equal(five, cap.MaxSendBytesPerSecond));
        Assert.True(
            aCaps.Concat(bCaps).Sum(static cap => AccountRateFormula.AllocatedBytesPerSecond(cap.MaxSendBytesPerSecond))
            <= AccountRateFormula.AccountBytesPerSecond(10));

        ratesB.Unregister(account, "b1");
        await nodeB.ReleaseAsync(account, "b1");
        var four = AccountRateFormula.PerSessionBytesPerSecond(10, 4);
        Assert.All(aCaps, cap => Assert.Equal(five, cap.MaxSendBytesPerSecond));
        Assert.True(
            aCaps.Sum(static cap => AccountRateFormula.AllocatedBytesPerSecond(cap.MaxSendBytesPerSecond))
            + AccountRateFormula.AllocatedBytesPerSecond(bCaps[0].MaxSendBytesPerSecond)
            <= AccountRateFormula.AccountBytesPerSecond(10));

        await nodeA.RenewLeasesAsync();
        Assert.All(aCaps, cap => Assert.True(cap.MaxSendBytesPerSecond <= five));
        Assert.True(
            aCaps.Append(bCaps[0]).Sum(static cap => AccountRateFormula.AllocatedBytesPerSecond(cap.MaxSendBytesPerSecond))
            <= AccountRateFormula.AccountBytesPerSecond(10));
        Assert.True(four * 4 <= AccountRateFormula.AccountBytesPerSecond(10));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task StaleGenerationRelease_DoesNotChangeLiveCount()
    {
        var account = await _redis.CreateAccountAsync();
        Assert.True((await _redis.TryAdmitAsync(account, IpA, "nntpd01:a", 10, 0, 2, 0, Now, Lease)).Accepted);
        Assert.Equal(1, await _redis.SessionCountAsync(account));
        Assert.Equal(1, await _redis.ReleaseAsync(account, IpA, "nntpd01:a", 1, 0));
        Assert.Equal(1, await _redis.SessionCountAsync(account));
        Assert.Equal(0, await _redis.ReleaseAsync(account, IpA, "nntpd01:a", 2, 0));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task ExpiredSession_IsNotCountedAfterPruneOnAdmit()
    {
        var account = await _redis.CreateAccountAsync();
        await _redis.SeedSessionAsync(account, "nntpd01:stale", expiryUnixMs: 1_000, generation: 1, count: 4);
        var admitted = await _redis.TryAdmitAsync(account, IpA, "nntpd02:live", 10, 0, 1, 0, 2_000, Lease);
        Assert.True(admitted.Accepted);
        Assert.Equal(1, admitted.SessionTotal);
        Assert.Equal(1, await _redis.SessionCountAsync(account));
        await _redis.DeleteKeysAsync(account);
    }

    [SessionStateRedisIntegrationFact]
    public async Task RapidJoinLeave_ThroughLiveLua()
    {
        var account = await _redis.CreateAccountAsync();
        for (var i = 0; i < 12; i++)
        {
            var owner = "nntpd01:" + i;
            var admitted = await _redis.TryAdmitAsync(account, IpA, owner, 10, 0, 1, 0, Now, Lease);
            Assert.True(admitted.Accepted);
            if (i % 3 == 2)
            {
                _ = await _redis.ReleaseAsync(account, IpA, owner, 1, 0);
            }
        }

        var remaining = await _redis.SessionCountAsync(account);
        Assert.InRange(remaining, 1, 10);
        await _redis.DeleteKeysAsync(account);
    }

    private sealed class FakeCap : IOutboundRateCap
    {
        public long MaxSendBytesPerSecond { get; private set; }

        public void UpdateMaxSendBytesPerSecond(long bytesPerSecond) => MaxSendBytesPerSecond = bytesPerSecond;
    }
}
