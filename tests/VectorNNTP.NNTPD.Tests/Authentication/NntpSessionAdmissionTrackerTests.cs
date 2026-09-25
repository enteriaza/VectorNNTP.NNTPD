using System.Net;
using VectorNNTP.NNTPD.Authentication;

namespace VectorNNTP.NNTPD.Tests.Authentication;

public sealed class NntpSessionAdmissionTrackerTests
{
    [Fact]
    public void ZeroLimits_AreUnlimited()
    {
        var tracker = new InMemoryNntpSessionAdmissionTracker();
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(
                NntpSessionAdmissionResult.Success,
                tracker.TryAdmit("alice", $"s{i}", IPAddress.Parse("203.0.113.10"), sessionLimit: 0, srcIpLimit: 0));
        }
    }

    [Fact]
    public void SessionLimit_IsAtomicAndExcludesReleasedSlots()
    {
        var tracker = new InMemoryNntpSessionAdmissionTracker();
        Assert.Equal(
            NntpSessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "a", IPAddress.Loopback, sessionLimit: 1, srcIpLimit: 0));
        Assert.Equal(
            NntpSessionAdmissionResult.MaxSessionsExceeded,
            tracker.TryAdmit("alice", "b", IPAddress.Loopback, sessionLimit: 1, srcIpLimit: 0));
        tracker.Release("alice", "a");
        Assert.Equal(
            NntpSessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "b", IPAddress.Loopback, sessionLimit: 1, srcIpLimit: 0));
    }

    [Fact]
    public void SourceIpLimit_CountsDistinctAddresses_NotSessionsOnSameIp()
    {
        var tracker = new InMemoryNntpSessionAdmissionTracker();
        var ip = IPAddress.Parse("2001:db8::1");
        Assert.Equal(
            NntpSessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "a", ip, sessionLimit: 0, srcIpLimit: 1));
        Assert.Equal(
            NntpSessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "b", ip, sessionLimit: 0, srcIpLimit: 1));
        Assert.Equal(
            NntpSessionAdmissionResult.IpLimitExceeded,
            tracker.TryAdmit("alice", "c", IPAddress.Parse("2001:db8::2"), sessionLimit: 0, srcIpLimit: 1));
    }

    [Fact]
    public void Ipv4MappedAddress_CountsAsIpv4()
    {
        var tracker = new InMemoryNntpSessionAdmissionTracker();
        var v4 = IPAddress.Parse("192.0.2.10");
        var mapped = IPAddress.Parse("::ffff:192.0.2.10");
        Assert.Equal(
            NntpSessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "a", v4, sessionLimit: 0, srcIpLimit: 1));
        Assert.Equal(
            NntpSessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "b", mapped, sessionLimit: 0, srcIpLimit: 1));
        Assert.Equal(
            NntpSessionAdmissionResult.IpLimitExceeded,
            tracker.TryAdmit("alice", "c", IPAddress.Parse("192.0.2.11"), sessionLimit: 0, srcIpLimit: 1));
    }

    [Fact]
    public void ConcurrentAdmit_RespectsSessionLimit()
    {
        var tracker = new InMemoryNntpSessionAdmissionTracker();
        var success = 0;
        Parallel.For(0, 32, i =>
        {
            if (tracker.TryAdmit("alice", $"s{i}", IPAddress.Loopback, sessionLimit: 2, srcIpLimit: 0)
                == NntpSessionAdmissionResult.Success)
            {
                Interlocked.Increment(ref success);
            }
        });

        Assert.Equal(2, success);
    }

    [Fact]
    public void AccountsAreIsolated()
    {
        var tracker = new InMemoryNntpSessionAdmissionTracker();
        Assert.Equal(
            NntpSessionAdmissionResult.Success,
            tracker.TryAdmit("alice", "a", IPAddress.Loopback, sessionLimit: 1, srcIpLimit: 1));
        Assert.Equal(
            NntpSessionAdmissionResult.Success,
            tracker.TryAdmit("bob", "b", IPAddress.Loopback, sessionLimit: 1, srcIpLimit: 1));
    }
}
