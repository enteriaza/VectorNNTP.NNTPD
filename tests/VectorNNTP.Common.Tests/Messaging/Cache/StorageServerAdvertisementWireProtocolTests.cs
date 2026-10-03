using System.Text;
using VectorNNTP.Common.Messaging.Cache;

namespace VectorNNTP.Common.Tests.Messaging.Cache
{
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
            Assert.Null(parsed.VatpPort);
            Assert.DoesNotContain("usagePercent", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("vatpPort", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }

        [Fact]
        public void SerializeAndParse_RoundTripsOptionalVatpPort()
        {
            var original = new StorageServerAdvertisement(
                1,
                3,
                "cache03.usenet.ninja",
                1_000_000_000,
                250_000_000,
                750_000_000,
                DateTimeOffset.Parse("2026-09-29T12:00:00Z"),
                563);
            var bytes = StorageServerAdvertisementWireProtocol.SerializeV1(original);
            Assert.True(StorageServerAdvertisementWireProtocol.TryParseV1(bytes, out var parsed, out var reason));
            Assert.Equal(string.Empty, reason);
            Assert.Equal(563, parsed!.VatpPort);
        }

        [Fact]
        public void TryParse_ToleratesUnknownProperties_AndRejectsInvalidVatpPort()
        {
            var withUnknown = """
                {"version":1,"serverId":1,"fqdn":"cache01.usenet.ninja","totalBytes":1,"usedBytes":0,"availableBytes":1,"timestamp":"2026-09-29T12:00:00.0000000Z","future":true,"vatpPort":563}
                """u8.ToArray();
            Assert.True(StorageServerAdvertisementWireProtocol.TryParseV1(withUnknown, out var parsed, out _));
            Assert.Equal(563, parsed!.VatpPort);

            var invalid = """
                {"version":1,"serverId":1,"fqdn":"cache01.usenet.ninja","totalBytes":1,"usedBytes":0,"availableBytes":1,"timestamp":"2026-09-29T12:00:00.0000000Z","vatpPort":0}
                """u8.ToArray();
            Assert.False(StorageServerAdvertisementWireProtocol.TryParseV1(invalid, out _, out var reason));
            Assert.Contains("vatpPort", reason, StringComparison.Ordinal);
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

        [Fact]
        public void LifecycleAnnouncement_RoundTrips_AndIsNotAnAdvertisement()
        {
            var original = new StorageServerLifecycleAnnouncement(
                1,
                4,
                "cache04.usenet.ninja",
                StorageServerLifecycleState.Draining,
                DateTimeOffset.Parse("2026-09-29T12:00:00Z"),
                1191);
            var bytes = StorageServerLifecycleWireProtocol.SerializeV1(original);
            Assert.True(StorageServerLifecycleWireProtocol.TryParseV1(bytes, out var parsed, out var reason, out var recognized));
            Assert.True(recognized);
            Assert.Equal(string.Empty, reason);
            Assert.Equal(original, parsed);
            Assert.False(StorageServerAdvertisementWireProtocol.TryParseV1(bytes, out _, out _));

            var advertisement = StorageServerAdvertisementWireProtocol.SerializeV1(
                new StorageServerAdvertisement(1, 4, "cache04.usenet.ninja", 1, 0, 1, original.Timestamp, 1191));
            Assert.False(StorageServerLifecycleWireProtocol.TryParseV1(
                advertisement,
                out _,
                out var notLifecycle,
                out var advertisementRecognized));
            Assert.False(advertisementRecognized);
            Assert.Equal(StorageServerLifecycleWireProtocol.NotLifecycleReason, notLifecycle);
        }

        [Fact]
        public void ReadyState_IsRejected_AndIsNotAnAdvertisement()
        {
            var ready = """{"version":1,"serverId":4,"fqdn":"cache04.usenet.ninja","state":"Ready","timestamp":"2026-09-29T12:00:00.0000000Z","vatpPort":1191}"""u8;
            Assert.False(StorageServerLifecycleWireProtocol.TryParseV1(
                ready,
                out var announcement,
                out var reason,
                out var recognized));
            Assert.True(recognized);
            Assert.Null(announcement);
            Assert.Equal("Lifecycle announcement state must be Draining.", reason);
            Assert.False(StorageServerAdvertisementWireProtocol.TryParseV1(ready, out _, out _));
            Assert.Equal(1, StorageServerLifecycleWireProtocol.CurrentVersion);
        }
    }
}
