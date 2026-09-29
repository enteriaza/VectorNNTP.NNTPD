using VectorNNTP.Common.Messaging.Cache;
using VectorNNTP.NNTPD.Storage;

namespace VectorNNTP.NNTPD.Tests.Storage;

public sealed class StorageServerRegistryTests
{
    [Fact]
    public void ApplyAdvertisement_CreatesAndUpdatesEntry()
    {
        var registry = new StorageServerRegistry();
        var t0 = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        var first = new StorageServerAdvertisement(1, 1, "cache01.usenet.ninja", 1000, 100, 900, t0);
        registry.ApplyAdvertisement(first, t0);

        Assert.True(registry.TryGet("cache01.usenet.ninja", out var entry));
        Assert.Equal(1, entry.ServerId);
        Assert.Equal(1000, entry.TotalBytes);
        Assert.Equal(100, entry.UsedBytes);
        Assert.Equal(900, entry.AvailableBytes);
        Assert.Equal(t0, entry.LastSeen);
        Assert.True(entry.IsActive(t0));

        var t1 = t0.AddSeconds(1);
        var second = first with { UsedBytes = 200, AvailableBytes = 800, Timestamp = t1 };
        registry.ApplyAdvertisement(second, t1);

        Assert.True(registry.TryGet("cache01.usenet.ninja", out entry));
        Assert.Equal(200, entry.UsedBytes);
        Assert.Equal(800, entry.AvailableBytes);
        Assert.Equal(t1, entry.LastSeen);
        Assert.Equal(StorageServerFleetState.Active, entry.GetState(t1));
    }

    [Fact]
    public void GetActive_ExcludesStaleServers_AndKeepsMultipleActive()
    {
        var registry = new StorageServerRegistry();
        var t0 = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        registry.ApplyAdvertisement(
            new StorageServerAdvertisement(1, 1, "cache01.usenet.ninja", 100, 10, 90, t0),
            t0);
        registry.ApplyAdvertisement(
            new StorageServerAdvertisement(1, 2, "cache02.usenet.ninja", 200, 20, 180, t0),
            t0);

        var activeAtStart = registry.GetActive(t0);
        Assert.Equal(2, activeAtStart.Count);

        var staleNow = t0 + CacheFleetTopology.LivenessWindow + TimeSpan.FromMilliseconds(1);
        Assert.Empty(registry.GetActive(staleNow));
        Assert.Equal(2, registry.Snapshot().Count);
        Assert.Equal(StorageServerFleetState.Stale, registry.Snapshot()[0].GetState(staleNow));

        registry.ApplyAdvertisement(
            new StorageServerAdvertisement(1, 2, "cache02.usenet.ninja", 200, 50, 150, staleNow),
            staleNow);
        var active = Assert.Single(registry.GetActive(staleNow));
        Assert.Equal("cache02.usenet.ninja", active.Fqdn);
        Assert.Equal(50, active.UsedBytes);
    }
}
