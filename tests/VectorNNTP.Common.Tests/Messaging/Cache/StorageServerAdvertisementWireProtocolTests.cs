using System.Text;
using VectorNNTP.Common.Messaging.Cache;

namespace VectorNNTP.Common.Tests.Messaging.Cache;

public sealed class StorageServerAdvertisementWireProtocolTests
{
    [Fact]
    public void SerializeAndParse_RoundTripsRequiredFields()
    {
        var original = new StorageServerAdvertisement(
            1,
            3,
            "cache03.usenet.ninja",
            1_000_000_000,
            250_000_000,
            750_000_000,
            DateTimeOffset.Parse("2026-09-29T12:00:00Z"));

        var bytes = StorageServerAdvertisementWireProtocol.SerializeV1(original);
        Assert.True(StorageServerAdvertisementWireProtocol.TryParseV1(bytes, out var parsed, out var reason));
        Assert.Equal(string.Empty, reason);
        Assert.NotNull(parsed);
        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.ServerId, parsed.ServerId);
        Assert.Equal(original.Fqdn, parsed.Fqdn);
        Assert.Equal(original.TotalBytes, parsed.TotalBytes);
        Assert.Equal(original.UsedBytes, parsed.UsedBytes);
        Assert.Equal(original.AvailableBytes, parsed.AvailableBytes);
        Assert.Equal(original.Timestamp, parsed.Timestamp);
        Assert.DoesNotContain("usagePercent", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildNntpdBroadcastQueueName_UsesCachePrefixAndNormalizedFqdn()
    {
        Assert.Equal(
            "cache.nntpd01.usenet.ninja",
            CacheFleetTopology.BuildNntpdBroadcastQueueName("  NNTPD01.Usenet.Ninja  "));
        Assert.Equal("cache.broadcast", CacheFleetTopology.BroadcastExchangeName);
        Assert.Equal("fanout", CacheFleetTopology.BroadcastExchangeType);
        Assert.Equal("cache.requests", CacheFleetTopology.RequestsExchangeName);
        Assert.Equal("3000", CacheFleetTopology.AdvertisementExpirationMilliseconds);
        Assert.Equal(TimeSpan.FromSeconds(1), CacheFleetTopology.AdvertisementInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), CacheFleetTopology.LivenessWindow);
    }
}
