using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Messaging.Cache;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Storage;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.Storage;

public sealed class ReplicationDurabilityTests
{
    [Fact]
    public void Pin_IsDurableAcrossRestart_AndRejectsADifferentTarget()
    {
        var directory = Directory.CreateTempSubdirectory("vnntp-pin-").FullName;
        var id = ArticleId.FromMessageId("<pin@example.test>"u8);
        var first = ReplicationIntentStore.Open(directory);
        var created = first.TryEstablish(id, 3, 2);
        Assert.True(created.Created);
        Assert.Equal(ReplicationIntentState.Pending, created.Intent.State);
        Assert.Equal(2, created.Intent.TargetServerId);

        var same = first.TryEstablish(id, 3, 2);
        Assert.False(same.Created);
        Assert.Equal(created.Intent, same.Intent);

        var reopened = ReplicationIntentStore.Open(directory);
        Assert.True(reopened.TryGet(id, out var loaded));
        Assert.Equal(id, loaded.ArticleId);
        Assert.Equal(3, loaded.SourceServerId);
        Assert.Equal(2, loaded.TargetServerId);
        Assert.Equal(ReplicationIntentState.Pending, loaded.State);

        var again = reopened.TryEstablish(id, 9, 1);
        Assert.False(again.Created);
        Assert.Equal(2, again.Intent.TargetServerId);
        Assert.Equal(3, again.Intent.SourceServerId);
        Assert.True(reopened.TryGet(id, out loaded));
        Assert.Equal(2, loaded.TargetServerId);
        Assert.False(reopened.TryComplete(ArticleId.FromMessageId("<absent@example.test>"u8)));
        Assert.True(reopened.TryComplete(id));
        Assert.True(reopened.TryComplete(id));
        var completed = ReplicationIntentStore.Open(directory);
        Assert.True(completed.TryGet(id, out loaded));
        Assert.Equal(ReplicationIntentState.Completed, loaded.State);
        Assert.Equal(3, loaded.SourceServerId);
        Assert.Equal(2, loaded.TargetServerId);
        var afterComplete = completed.TryEstablish(id, 4, 8);
        Assert.False(afterComplete.Created);
        Assert.Equal(ReplicationIntentState.Completed, afterComplete.Intent.State);
        Assert.Equal(2, afterComplete.Intent.TargetServerId);
    }

    [Fact]
    public async Task TwoCallers_EstablishOnePin()
    {
        var directory = Directory.CreateTempSubdirectory("vnntp-pin-race-").FullName;
        var store = ReplicationIntentStore.Open(directory);
        var id = ArticleId.FromMessageId("<race@example.test>"u8);
        var barrier = new Barrier(2);
        ReplicationPinResult left = default;
        ReplicationPinResult right = default;
        var first = Task.Run(() =>
        {
            barrier.SignalAndWait();
            left = store.TryEstablish(id, 3, 2);
        });
        var second = Task.Run(() =>
        {
            barrier.SignalAndWait();
            right = store.TryEstablish(id, 3, 1);
        });
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(left.Created, right.Created);
        Assert.Equal(left.Intent.TargetServerId, right.Intent.TargetServerId);
        Assert.True(store.TryGet(id, out var stored));
        Assert.Equal(stored.TargetServerId, left.Intent.TargetServerId);
        Assert.True(stored.TargetServerId is 1 or 2);
    }

    [Fact]
    public void DisabledSender_FailsClearly()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            DisabledReplicationIntentStore.Instance.TryEstablish(
                ArticleId.FromMessageId("<off@example.test>"u8),
                1,
                2));
        Assert.Contains("SecondCopySender", thrown.Message, StringComparison.Ordinal);
        thrown = Assert.Throws<InvalidOperationException>(() =>
            DisabledReplicationIntentStore.Instance.TryComplete(
                ArticleId.FromMessageId("<off@example.test>"u8)));
        Assert.Contains("SecondCopySender", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Roster_SurvivesRestart_AndDoesNotStoreCapacity()
    {
        var directory = Directory.CreateTempSubdirectory("vnntp-roster-").FullName;
        var roster = DurableStorageServerRoster.Open(directory);
        roster.Observe(4, "cache04.example", 563);
        var reopened = DurableStorageServerRoster.Open(directory);
        Assert.True(reopened.TryGet(4, out var stale));
        Assert.Equal("cache04.example", stale.Fqdn);
        Assert.Equal(563, stale.VatpPort);
        reopened.Observe(4, "cache04-new.example", 564);
        var updated = DurableStorageServerRoster.Open(directory);
        Assert.True(updated.TryGet(4, out var moved));
        Assert.Equal(4, moved.ServerId);
        Assert.Equal("cache04-new.example", moved.Fqdn);
        Assert.Equal(564, moved.VatpPort);
        Assert.Single(updated.Snapshot());
        Assert.Equal(3, typeof(StorageServerRosterEntry).GetProperties().Length);
    }

    [Fact]
    public void Roster_SilenceLeavesTheServerIdResolvable()
    {
        var seen = DateTimeOffset.Parse("2026-09-30T00:00:00Z");
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(
            new StorageServerAdvertisement(1, 4, "cache04.example", 10_000, 4_000, 6_000, seen, 563),
            seen);
        var directory = Directory.CreateTempSubdirectory("vnntp-roster-stale-").FullName;
        var roster = DurableStorageServerRoster.Open(directory);
        roster.Observe(4, "cache04.example", 563);

        var silent = seen + CacheFleetTopology.LivenessWindow + TimeSpan.FromSeconds(1);
        Assert.Empty(registry.GetActive(silent));
        Assert.True(registry.TryGet("cache04.example", out var memory));
        Assert.Equal(StorageServerFleetState.Stale, memory.GetState(silent));

        var reopened = DurableStorageServerRoster.Open(directory);
        Assert.True(reopened.TryGet(4, out var durable));
        Assert.Equal(4, durable.ServerId);
        Assert.Equal("cache04.example", durable.Fqdn);
        Assert.Equal(563, durable.VatpPort);
        Assert.Single(reopened.Snapshot());
    }

    [Fact]
    public void SecondCopySender_RequiresADirectory()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.Replication = new ReplicationOptions { SecondCopySender = true, Directory = " " };
        var result = new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true)).Validate(null, options);
        Assert.False(result.Succeeded);
        Assert.Contains("SecondCopySender", string.Join('\n', result.Failures ?? []), StringComparison.Ordinal);

        options.Replication = new ReplicationOptions { SecondCopySender = false };
        Assert.True(new NntpdOptionsValidator(new FakeLocalIpAddressAssignee(assignAll: true))
            .Validate(null, options)
            .Succeeded);
    }
}
