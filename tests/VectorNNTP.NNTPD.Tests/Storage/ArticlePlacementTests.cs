using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Messaging.Cache;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Storage;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Transport.Vatp;

namespace VectorNNTP.NNTPD.Tests.Storage;

public sealed class ArticlePlacementTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T00:00:00Z");

    [Fact]
    public void Selector_PrefersHighestAvailableBytes_ThenServerId_ThenFqdn()
    {
        var entries = new StorageServerFleetEntry[]
        {
            Entry("cache02.example", 2, available: 10, port: 563),
            Entry("cache09.example", 9, available: 50, port: 563),
            Entry("cache03.example", 3, available: 50, port: 563),
        };
        Assert.True(StorageServerPlacementSelector.TrySelect(entries, out var selected));
        Assert.Equal(3, selected.ServerId);

        var tiedId = new StorageServerFleetEntry[]
        {
            Entry("m.example", 4, available: 20, port: 563),
            Entry("a.example", 4, available: 20, port: 563),
        };
        Assert.True(StorageServerPlacementSelector.TrySelect(tiedId, out var fqdn));
        Assert.Equal("a.example", fqdn.Fqdn);
    }

    [Fact]
    public void Selector_ExcludesMissingPort_AndEmptySetDoesNotSelect()
    {
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("cache01.example", 1, 100, port: null), Now);
        registry.ApplyAdvertisement(Advertisement("stale.example", 2, 500, port: 563), Now.AddSeconds(-10));
        var active = registry.GetActive(Now);
        Assert.Contains(active, static entry => entry.Fqdn == "cache01.example");
        Assert.DoesNotContain(active, static entry => entry.Fqdn == "stale.example");
        Assert.False(StorageServerPlacementSelector.TrySelect(active, out _));
        Assert.False(StorageServerPlacementSelector.TrySelect([], out _));
    }

    [Fact]
    public void Selector_ReplicaIsNextEligible_AndNeverReselectsFirst()
    {
        var entries = new StorageServerFleetEntry[]
        {
            Entry("cache02.example", 2, available: 10, port: 563),
            Entry("cache09.example", 9, available: 50, port: 563),
            Entry("cache03.example", 3, available: 50, port: 563),
            Entry("noport.example", 1, available: 500, port: null),
        };
        Assert.True(StorageServerPlacementSelector.TrySelect(entries, out var first));
        Assert.Equal("cache03.example", first.Fqdn);
        Assert.True(StorageServerPlacementSelector.TrySelectExcluding(entries, first.Fqdn, out var replica));
        Assert.Equal("cache09.example", replica.Fqdn);
        Assert.NotEqual(first.Fqdn, replica.Fqdn);

        var onlyFirst = new StorageServerFleetEntry[] { first, Entry("noport.example", 1, available: 500, port: null) };
        Assert.False(StorageServerPlacementSelector.TrySelectExcluding(onlyFirst, first.Fqdn, out _));
    }

    [Fact]
    public void Selector_ReplicaTieBreak_UsesAvailableBytesThenServerIdThenFqdn()
    {
        var entries = new StorageServerFleetEntry[]
        {
            Entry("m.example", 4, available: 20, port: 563),
            Entry("a.example", 4, available: 20, port: 563),
            Entry("b.example", 8, available: 20, port: 563),
        };
        Assert.True(StorageServerPlacementSelector.TrySelect(entries, out var first));
        Assert.Equal("a.example", first.Fqdn);
        Assert.True(StorageServerPlacementSelector.TrySelectExcluding(entries, first.Fqdn, out var replica));
        Assert.Equal("m.example", replica.Fqdn);
    }

    [Fact]
    public void Selector_ReplicaExcludesStaleAndMissingPort()
    {
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("stale.example", 1, 900, 563), Now.AddSeconds(-10));
        registry.ApplyAdvertisement(Advertisement("noport.example", 2, 800, port: null), Now);
        registry.ApplyAdvertisement(Advertisement("cache03.example", 3, 100, 563), Now);
        registry.ApplyAdvertisement(Advertisement("cache04.example", 4, 40, 563), Now);
        var active = registry.GetActive(Now);
        Assert.True(StorageServerPlacementSelector.TrySelect(active, out var first));
        Assert.Equal("cache03.example", first.Fqdn);
        Assert.True(StorageServerPlacementSelector.TrySelectExcluding(active, first.Fqdn, out var replica));
        Assert.Equal("cache04.example", replica.Fqdn);
        Assert.DoesNotContain(active, static entry => entry.Fqdn == "stale.example");
    }

    [Theory]
    [InlineData(InboundArticleProducer.TakeThis)]
    [InlineData(InboundArticleProducer.IHave)]
    [InlineData(InboundArticleProducer.Post)]
    public async Task LocalProducer_PlacesOnceAfterPersist(InboundArticleProducer producer)
    {
        var client = new RecordingPlacement();
        var registry = RegistryWithTarget();
        var persister = new OrderedPersister();
        var article = CanonicalArticleText.CreateQueued("<place@example.test>", producer);
        await RunAsync(article, persister, client, registry);
        Assert.Equal(1, persister.Count);
        Assert.Equal(1, client.Calls);
        Assert.True(client.SawPersist);
        Assert.Equal(article.Record.ArtId, client.Last.ArtId);
    }

    [Fact]
    public async Task BackFiller_StoresOnceOnTheLowestServerId()
    {
        var client = new RecordingPlacement();
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<bf@example.test>", InboundArticleProducer.BackFiller);
        var intent = await RunObservedAsync(article, new OrderedPersister(), client, DivergentTargets(), logs);
        Assert.Equal(1, client.Calls);
        Assert.Equal(2, client.Targets[0].ServerId);
        Assert.Equal("lowid.example", client.Targets[0].Fqdn);
        Assert.Equal(article.Record.ArtId, client.Last.ArtId);
        Assert.Contains(2620, logs.EventIds);
        Assert.DoesNotContain(logs.EventIds, static id => id is >= 2630 and <= 2641);
        Assert.False(intent.TryGet(article.Record.ArtId, out _));
    }

    [Fact]
    public async Task NoEligibleServer_DoesNotDial()
    {
        var client = new RecordingPlacement { FailIfCalled = true };
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("cache01.example", 1, 100, port: null), Now);
        var article = CanonicalArticleText.CreateQueued("<none@example.test>", InboundArticleProducer.TakeThis);
        await RunAsync(article, new OrderedPersister(), client, registry, time: Now);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task PlacementFailure_DoesNotRequeue()
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(ArticlePlacementKind.Conflict),
        };
        var article = CanonicalArticleText.CreateQueued("<conflict@example.test>", InboundArticleProducer.Post);
        var persister = new OrderedPersister();
        await RunAsync(article, persister, client, RegistryWithTarget());
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, persister.Count);
    }

    [Theory]
    [InlineData(InboundArticleProducer.TakeThis)]
    [InlineData(InboundArticleProducer.IHave)]
    [InlineData(InboundArticleProducer.Post)]
    public async Task LocalProducer_PlacesFirstAndOneReplica(InboundArticleProducer producer)
    {
        var client = new RecordingPlacement { ExpectedCalls = 2 };
        var article = CanonicalArticleText.CreateQueued("<replica@example.test>", producer);
        var logs = new ListLogger<IncomingSpoolWriterService>();
        await RunAsync(article, new OrderedPersister(), client, TwoTargets(), logs);
        Assert.Equal(2, client.Calls);
        Assert.Equal("cache01.example", client.Targets[0].Fqdn);
        Assert.Equal("cache02.example", client.Targets[1].Fqdn);
        AssertSameArtData(article.Record, client.Records[0]);
        AssertSameArtData(article.Record, client.Records[1]);
        Assert.Contains(2620, logs.EventIds);
        Assert.Contains(2630, logs.EventIds);
    }

    [Fact]
    public async Task BackFiller_Duplicate_IsOneStoreAndDoesNotReplicate()
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(ArticlePlacementKind.Duplicate),
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<bf-dup@example.test>", InboundArticleProducer.BackFiller);
        var intent = await RunObservedAsync(article, new OrderedPersister(), client, DivergentTargets(), logs);
        Assert.Equal(1, client.Calls);
        Assert.Equal("lowid.example", client.Targets[0].Fqdn);
        Assert.Contains(2621, logs.EventIds);
        Assert.DoesNotContain(logs.EventIds, static id => id is >= 2630 and <= 2641);
        Assert.False(intent.TryGet(article.Record.ArtId, out _));
    }

    [Fact]
    public async Task BackFiller_Conflict_LogsAndDoesNotStoreAgain()
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(ArticlePlacementKind.Conflict),
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<bf-conflict@example.test>", InboundArticleProducer.BackFiller);
        var intent = await RunObservedAsync(article, new OrderedPersister(), client, DivergentTargets(), logs);
        Assert.Equal(1, client.Calls);
        Assert.Equal(2, client.Targets[0].ServerId);
        Assert.Contains(2622, logs.EventIds);
        Assert.DoesNotContain(logs.EventIds, static id => id is >= 2630 and <= 2641);
        Assert.False(intent.TryGet(article.Record.ArtId, out _));
    }

    [Theory]
    [InlineData(ArticlePlacementKind.RejectedCapacity, 2623)]
    [InlineData(ArticlePlacementKind.RejectedPressure, 2624)]
    [InlineData(ArticlePlacementKind.RejectedInvalid, 2625)]
    public async Task BackFiller_Rejection_DoesNotTryAnotherServer(ArticlePlacementKind kind, int eventId)
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(kind),
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<bf-reject@example.test>", InboundArticleProducer.BackFiller);
        var intent = await RunObservedAsync(article, new OrderedPersister(), client, DivergentTargets(), logs);
        Assert.Equal(1, client.Calls);
        Assert.Equal("lowid.example", client.Targets[0].Fqdn);
        Assert.Contains(eventId, logs.EventIds);
        Assert.DoesNotContain(logs.EventIds, static id => id is >= 2630 and <= 2641);
        Assert.False(intent.TryGet(article.Record.ArtId, out _));
    }

    [Fact]
    public async Task BackFiller_TransportFailure_DoesNotTryAnotherServer()
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "IOException"),
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<bf-transport@example.test>", InboundArticleProducer.BackFiller);
        var intent = await RunObservedAsync(article, new OrderedPersister(), client, DivergentTargets(), logs);
        Assert.Equal(1, client.Calls);
        Assert.Equal(2, client.Targets[0].ServerId);
        Assert.Contains(2627, logs.EventIds);
        Assert.DoesNotContain(logs.EventIds, static id => id is >= 2630 and <= 2641);
        Assert.False(intent.TryGet(article.Record.ArtId, out _));
    }

    [Fact]
    public async Task PeerProducer_StillPrefersFreeSpaceAndOneReplica()
    {
        var client = new RecordingPlacement { ExpectedCalls = 2 };
        var article = CanonicalArticleText.CreateQueued("<peer-rank@example.test>", InboundArticleProducer.Post);
        var intent = await RunObservedAsync(article, new OrderedPersister(), client, DivergentTargets());
        Assert.Equal(2, client.Calls);
        Assert.Equal(9, client.Targets[0].ServerId);
        Assert.Equal("highspace.example", client.Targets[0].Fqdn);
        Assert.Equal(2, client.Targets[1].ServerId);
        Assert.True(intent.TryGet(article.Record.ArtId, out var pin));
        Assert.Equal(9, pin.SourceServerId);
        Assert.Equal(2, pin.TargetServerId);
    }

    [Fact]
    public async Task BackFiller_ConcurrentArticles_DoNotShareStoreState()
    {
        var client = new ConcurrentPlacement();
        var first = CanonicalArticleText.CreateQueued("<bf-a@example.test>", InboundArticleProducer.BackFiller);
        var second = CanonicalArticleText.CreateQueued("<bf-b@example.test>", InboundArticleProducer.BackFiller);
        var intent = await RunManyAsync([first, second], client, DivergentTargets(), workers: 2);
        Assert.Equal(2, client.Calls);
        Assert.Equal(2, client.Targets.Select(static target => target.ServerId).Distinct().Single());
        Assert.Contains(client.Records, record => record.ArtId.Equals(first.Record.ArtId));
        Assert.Contains(client.Records, record => record.ArtId.Equals(second.Record.ArtId));
        Assert.NotEqual(first.Record.ArtId, second.Record.ArtId);
        Assert.False(intent.TryGet(first.Record.ArtId, out _));
        Assert.False(intent.TryGet(second.Record.ArtId, out _));
    }

    [Fact]
    public async Task BackFiller_SameArticleConcurrently_StoresTwice()
    {
        var client = new ConcurrentPlacement();
        var first = CanonicalArticleText.CreateQueued("<bf-same@example.test>", InboundArticleProducer.BackFiller);
        var second = CanonicalArticleText.CreateQueued("<bf-same@example.test>", InboundArticleProducer.BackFiller);
        Assert.Equal(first.Record.ArtId, second.Record.ArtId);
        var intent = await RunManyAsync([first, second], client, DivergentTargets(), workers: 2);
        Assert.Equal(2, client.Calls);
        Assert.All(client.Records, record => Assert.Equal(first.Record.ArtId, record.ArtId));
        Assert.Equal(2, client.Targets.Select(static target => target.ServerId).Distinct().Single());
        Assert.False(intent.TryGet(first.Record.ArtId, out _));
    }

    [Fact]
    public async Task NoSecondEligible_LeavesSingleCopy()
    {
        var client = new RecordingPlacement();
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<single@example.test>", InboundArticleProducer.TakeThis);
        await RunAsync(article, new OrderedPersister(), client, RegistryWithTarget(), logs);
        Assert.Equal(1, client.Calls);
        Assert.Equal("cache01.example", client.Targets[0].Fqdn);
        Assert.Contains(2636, logs.EventIds);
        Assert.DoesNotContain(logs.EventIds, static id => id is (>= 2630 and <= 2635) or (>= 2637 and <= 2641));
    }

    [Theory]
    [InlineData(ArticlePlacementKind.Conflict, 2622)]
    [InlineData(ArticlePlacementKind.RejectedPressure, 2624)]
    [InlineData(ArticlePlacementKind.RejectedCapacity, 2623)]
    [InlineData(ArticlePlacementKind.RejectedInvalid, 2625)]
    [InlineData(ArticlePlacementKind.TransportFailure, 2627)]
    [InlineData(ArticlePlacementKind.AcknowledgementNotObserved, 2629)]
    public async Task FirstFailure_DoesNotAttemptReplica(ArticlePlacementKind kind, int eventId)
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(kind, kind == ArticlePlacementKind.TransportFailure ? "IOException" : null),
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var persister = new OrderedPersister();
        var article = CanonicalArticleText.CreateQueued("<gate@example.test>", InboundArticleProducer.Post);
        await RunAsync(article, persister, client, TwoTargets(), logs);
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, persister.Count);
        Assert.Equal("cache01.example", client.Targets[0].Fqdn);
        Assert.Contains(eventId, logs.EventIds);
        Assert.DoesNotContain(logs.EventIds, static id => id is >= 2630 and <= 2641);
    }

    [Theory]
    [InlineData(ArticlePlacementKind.Accepted, 2630)]
    [InlineData(ArticlePlacementKind.Duplicate, 2631)]
    [InlineData(ArticlePlacementKind.Conflict, 2632)]
    [InlineData(ArticlePlacementKind.RejectedPressure, 2634)]
    [InlineData(ArticlePlacementKind.RejectedCapacity, 2633)]
    [InlineData(ArticlePlacementKind.RejectedInvalid, 2635)]
    [InlineData(ArticlePlacementKind.TransportFailure, 2637)]
    [InlineData(ArticlePlacementKind.Cancelled, 2639)]
    [InlineData(ArticlePlacementKind.AcknowledgementNotObserved, 2640)]
    public async Task ReplicaOutcome_DoesNotSelectAThirdServer(ArticlePlacementKind replicaKind, int eventId)
    {
        var failure = replicaKind == ArticlePlacementKind.TransportFailure ? "IOException" : null;
        var client = new RecordingPlacement
        {
            ExpectedCalls = 2,
            Sequence =
            [
                new ArticlePlacementResult(ArticlePlacementKind.Accepted),
                new ArticlePlacementResult(replicaKind, failure),
            ],
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var persister = new OrderedPersister();
        var article = CanonicalArticleText.CreateQueued("<outcome@example.test>", InboundArticleProducer.IHave);
        await RunAsync(article, persister, client, ThreeTargets(), logs);
        Assert.Equal(2, client.Calls);
        Assert.Equal(1, persister.Count);
        Assert.Equal("cache01.example", client.Targets[0].Fqdn);
        Assert.Equal("cache02.example", client.Targets[1].Fqdn);
        Assert.Contains(eventId, logs.EventIds);
        AssertSameArtData(article.Record, client.Records[0]);
        AssertSameArtData(article.Record, client.Records[1]);
    }

    [Fact]
    public async Task FirstDuplicate_StillAttemptsReplica()
    {
        var client = new RecordingPlacement
        {
            ExpectedCalls = 2,
            Sequence =
            [
                new ArticlePlacementResult(ArticlePlacementKind.Duplicate),
                new ArticlePlacementResult(ArticlePlacementKind.Accepted),
            ],
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        await RunAsync(
            CanonicalArticleText.CreateQueued("<dup-first@example.test>", InboundArticleProducer.TakeThis),
            new OrderedPersister(),
            client,
            TwoTargets(),
            logs);
        Assert.Equal(2, client.Calls);
        Assert.Contains(2621, logs.EventIds);
        Assert.Contains(2630, logs.EventIds);
    }

    [Fact]
    public async Task ReplicaTimeout_DoesNotRetry()
    {
        var client = new RecordingPlacement
        {
            ExpectedCalls = 2,
            Sequence =
            [
                new ArticlePlacementResult(ArticlePlacementKind.Accepted),
                new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "timeout"),
            ],
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var persister = new OrderedPersister();
        await RunAsync(
            CanonicalArticleText.CreateQueued("<timeout@example.test>", InboundArticleProducer.TakeThis),
            persister,
            client,
            ThreeTargets(),
            logs);
        Assert.Equal(2, client.Calls);
        Assert.Equal(1, persister.Count);
        Assert.Contains(2638, logs.EventIds);
        Assert.DoesNotContain(2637, logs.EventIds);
    }

    [Fact]
    public async Task ReplicaDropAfterEnd_DoesNotResend()
    {
        var client = new RecordingPlacement
        {
            ExpectedCalls = 2,
            Sequence =
            [
                new ArticlePlacementResult(ArticlePlacementKind.Accepted),
                new ArticlePlacementResult(ArticlePlacementKind.AcknowledgementNotObserved),
            ],
        };
        var persister = new OrderedPersister();
        await RunAsync(
            CanonicalArticleText.CreateQueued("<ambiguous@example.test>", InboundArticleProducer.Post),
            persister,
            client,
            ThreeTargets());
        Assert.Equal(2, client.Calls);
        Assert.Equal(1, persister.Count);
        Assert.Equal(ArticlePlacementKind.AcknowledgementNotObserved, client.Outcomes[1].Kind);
    }

    [Fact]
    public async Task CancellationBeforeFirstAcceptance_DoesNotAttemptReplica()
    {
        using var cts = new CancellationTokenSource();
        var client = new RecordingPlacement
        {
            CancelAt = CancelPoint.FirstCallReturnsCancelled,
            Cancel = cts,
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        await PlaceDirectAsync(
            CanonicalArticleText.CreateQueued("<cancel-first@example.test>", InboundArticleProducer.TakeThis),
            client,
            TwoTargets(),
            cts.Token,
            logs);
        Assert.Equal(1, client.Calls);
        Assert.Equal(ArticlePlacementKind.Cancelled, client.Outcomes[0].Kind);
        Assert.Equal("cache01.example", client.Targets[0].Fqdn);
        Assert.Contains(2628, logs.EventIds);
        Assert.DoesNotContain(logs.EventIds, static id => id is >= 2630 and <= 2641);
    }

    [Fact]
    public async Task CancellationAfterFirstAcceptance_DoesNotStartReplica()
    {
        using var cts = new CancellationTokenSource();
        var client = new RecordingPlacement
        {
            CancelAt = CancelPoint.AfterFirstAccepted,
            Cancel = cts,
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        await PlaceDirectAsync(
            CanonicalArticleText.CreateQueued("<cancel-between@example.test>", InboundArticleProducer.TakeThis),
            client,
            TwoTargets(),
            cts.Token,
            logs);
        Assert.Equal(1, client.Calls);
        Assert.Equal(ArticlePlacementKind.Accepted, client.Outcomes[0].Kind);
        Assert.Equal("cache01.example", client.Targets[0].Fqdn);
        Assert.Contains(2620, logs.EventIds);
        Assert.Contains(2641, logs.EventIds);
        Assert.DoesNotContain(2630, logs.EventIds);
    }

    [Fact]
    public async Task CancellationDuringReplica_DoesNotSelectAThirdServer()
    {
        using var cts = new CancellationTokenSource();
        var client = new RecordingPlacement
        {
            CancelAt = CancelPoint.SecondCallReturnsCancelled,
            Cancel = cts,
            Sequence = [new ArticlePlacementResult(ArticlePlacementKind.Accepted)],
        };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        await PlaceDirectAsync(
            CanonicalArticleText.CreateQueued("<cancel-replica@example.test>", InboundArticleProducer.IHave),
            client,
            ThreeTargets(),
            cts.Token,
            logs);
        Assert.Equal(2, client.Calls);
        Assert.Equal(ArticlePlacementKind.Accepted, client.Outcomes[0].Kind);
        Assert.Equal(ArticlePlacementKind.Cancelled, client.Outcomes[1].Kind);
        Assert.Equal("cache01.example", client.Targets[0].Fqdn);
        Assert.Equal("cache02.example", client.Targets[1].Fqdn);
        Assert.DoesNotContain(client.Targets, static target => target.Fqdn == "cache03.example");
        Assert.Contains(2639, logs.EventIds);
    }

    [Fact]
    public async Task ReplicaAccepted_IsNotFollowedByAnotherPlacement()
    {
        var client = new RecordingPlacement
        {
            ExpectedCalls = 2,
            Sequence =
            [
                new ArticlePlacementResult(ArticlePlacementKind.Accepted),
                new ArticlePlacementResult(ArticlePlacementKind.Accepted),
            ],
        };
        await RunAsync(
            CanonicalArticleText.CreateQueued("<kept@example.test>", InboundArticleProducer.Post),
            new OrderedPersister(),
            client,
            ThreeTargets());
        Assert.Equal(2, client.Calls);
        Assert.Equal(ArticlePlacementKind.Accepted, client.Outcomes[1].Kind);
    }

    [Fact]
    public void Placement_DoesNotReferenceLookupOrReadPool()
    {
        var placementFields = typeof(ArticlePlacementClient).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(placementFields, static field => field.FieldType.Name.Contains("Lookup", StringComparison.Ordinal));
        var writerFields = typeof(IncomingSpoolWriterService).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(writerFields, static field => field.FieldType.Name.Contains("Lookup", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(StorageArticleLookupService).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            static method => method.Name.Contains("Replica", StringComparison.Ordinal)
                || method.Name.Contains("Primary", StringComparison.Ordinal));
    }

    [Fact]
    public void PlacementClient_DoesNotUseReadConnectionPool()
    {
        var fields = typeof(ArticlePlacementClient).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, static field => field.FieldType == typeof(VatpConnectionPool));
        Assert.False(typeof(VatpArticleClient).IsAssignableTo(typeof(IArticlePlacementClient)));
    }

    [Fact]
    public async Task Transfer_ResultAccepted_AndWindowPacing()
    {
        var article = CanonicalArticleText.CreateQueued("<xfer@example.test>", InboundArticleProducer.TakeThis).Record;
        Assert.True(ArticlePlacementMeta.TryFromRecord(article, out var meta));
        var limits = new ArticleTransferLimits
        {
            InitialStreamWindowBytes = 32,
            MaxStreamCreditBytes = 1024 * 1024,
            DefaultMaxFramePayload = 64,
        };
        Assert.True(article.ArtSize > 32);
        await using var link = new Link();
        var server = Task.Run(() => ScriptedServer.RunAsync(link.Server, result: (byte)ArticlePlacementKind.Accepted));
        var result = await VatpPlacementTransfer.PlaceAsync(
            link.Client,
            article,
            meta,
            limits,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Assert.Equal(ArticlePlacementKind.Accepted, result.Kind);
        Assert.True(await server.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Transfer_DropAfterEnd_IsAcknowledgementNotObserved()
    {
        var article = CanonicalArticleText.CreateQueued("<drop@example.test>", InboundArticleProducer.IHave).Record;
        Assert.True(ArticlePlacementMeta.TryFromRecord(article, out var meta));
        await using var link = new Link();
        var server = Task.Run(() => ScriptedServer.RunAsync(link.Server, result: null));
        var placed = await VatpPlacementTransfer.PlaceAsync(
            link.Client,
            article,
            meta,
            ArticleTransferLimits.Default,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Assert.Equal(ArticlePlacementKind.AcknowledgementNotObserved, placed.Kind);
        Assert.False(placed.Succeeded);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Transfer_CancelBeforeSend_DoesNotReportSuccess()
    {
        var article = CanonicalArticleText.CreateQueued("<cancel@example.test>", InboundArticleProducer.TakeThis).Record;
        Assert.True(ArticlePlacementMeta.TryFromRecord(article, out var meta));
        await using var link = new Link();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var placed = await VatpPlacementTransfer.PlaceAsync(
            link.Client,
            article,
            meta,
            ArticleTransferLimits.Default,
            TimeSpan.FromSeconds(5),
            cts.Token);
        Assert.Equal(ArticlePlacementKind.Cancelled, placed.Kind);
        Assert.False(placed.Succeeded);
    }

    [Fact]
    public async Task SecondCopySenderDisabled_DoesNotStartReplicaStore()
    {
        var client = new RecordingPlacement { ExpectedCalls = 1 };
        var registry = TwoTargets();
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<nosender@example.test>", InboundArticleProducer.Post);
        var writer = new IncomingSpoolWriterService(
            new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 }),
            new OrderedPersister(),
            Options.Create(new NntpdOptions
            {
                Replication = new ReplicationOptions { SecondCopySender = false },
                ArticleIngestion = new ArticleIngestionOptions
                {
                    MinWorkers = 1,
                    MaxWorkers = 1,
                    ScaleIntervalSeconds = 3600,
                },
            }),
            logs,
            timeProvider: new FixedTime(Now),
            placement: client,
            placementRegistry: registry,
            replicationIntent: ReplicationIntentStore.Open(Directory.CreateTempSubdirectory("vnntp-5h3a-off-").FullName));
        await InvokePlace(writer, article);
        Assert.Equal(1, client.Calls);
        Assert.Contains(2642, logs.EventIds);
        Assert.DoesNotContain(2630, logs.EventIds);
    }

    [Fact]
    public async Task MissingPinStore_DoesNotStartReplicaStore()
    {
        var client = new RecordingPlacement { ExpectedCalls = 1 };
        var logs = new ListLogger<IncomingSpoolWriterService>();
        var article = CanonicalArticleText.CreateQueued("<nopin@example.test>", InboundArticleProducer.Post);
        var writer = new IncomingSpoolWriterService(
            new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 }),
            new OrderedPersister(),
            Options.Create(new NntpdOptions
            {
                Replication = new ReplicationOptions
                {
                    SecondCopySender = true,
                    Directory = Directory.CreateTempSubdirectory("vnntp-5h3a-missing-").FullName,
                },
                ArticleIngestion = new ArticleIngestionOptions
                {
                    MinWorkers = 1,
                    MaxWorkers = 1,
                    ScaleIntervalSeconds = 3600,
                },
            }),
            logs,
            timeProvider: new FixedTime(Now),
            placement: client,
            placementRegistry: TwoTargets());
        await InvokePlace(writer, article);
        Assert.Equal(1, client.Calls);
        Assert.Contains(2643, logs.EventIds);
    }

    [Theory]
    [InlineData(ArticlePlacementKind.Conflict, null)]
    [InlineData(ArticlePlacementKind.AcknowledgementNotObserved, null)]
    [InlineData(ArticlePlacementKind.TransportFailure, "timeout")]
    public async Task PinnedReplica_IsNotRetargetedWhenAServerAppears(
        ArticlePlacementKind replicaKind,
        string? failure)
    {
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("cache03.example", 3, 500, 565), Now);
        registry.ApplyAdvertisement(Advertisement("cache02.example", 2, 100, 564), Now);
        var enabled = EnableSecondCopy();
        var article = CanonicalArticleText.CreateQueued("<pin-race@example.test>", InboundArticleProducer.Post);
        var client = new RecordingPlacement
        {
            Sequence =
            [
                new ArticlePlacementResult(ArticlePlacementKind.Accepted),
                new ArticlePlacementResult(replicaKind, failure),
                new ArticlePlacementResult(ArticlePlacementKind.Accepted),
                new ArticlePlacementResult(ArticlePlacementKind.Duplicate),
            ],
            BeforeStore = (call, target) =>
            {
                if (call != 2)
                {
                    return;
                }

                Assert.Equal(2, target.ServerId);
                Assert.True(enabled.Intent.TryGet(article.Record.ArtId, out var pinned));
                Assert.Equal(3, pinned.SourceServerId);
                Assert.Equal(2, pinned.TargetServerId);
                registry.ApplyAdvertisement(Advertisement("cache01.example", 1, 100_000, 563), Now);
                var competing = enabled.Intent.TryEstablish(article.Record.ArtId, 3, 1);
                Assert.False(competing.Created);
                Assert.Equal(2, competing.Intent.TargetServerId);
            },
        };
        var writer = new IncomingSpoolWriterService(
            new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 }),
            new OrderedPersister(),
            Options.Create(enabled.Options),
            NullLogger<IncomingSpoolWriterService>.Instance,
            timeProvider: new FixedTime(Now),
            placement: client,
            placementRegistry: registry,
            replicationIntent: enabled.Intent,
            replicationRoster: enabled.Roster);
        await InvokePlace(writer, article);
        await InvokePlace(writer, article);
        Assert.Equal(4, client.Calls);
        Assert.Equal(3, client.Targets[0].ServerId);
        Assert.Equal(2, client.Targets[1].ServerId);
        Assert.Equal(1, client.Targets[2].ServerId);
        Assert.Equal(2, client.Targets[3].ServerId);
        Assert.Equal("cache02.example", client.Targets[3].Fqdn);
        Assert.True(enabled.Intent.TryGet(article.Record.ArtId, out var still));
        Assert.Equal(2, still.TargetServerId);
        Assert.Equal(3, still.SourceServerId);
    }

    [Fact]
    public async Task ExistingPin_IsDialedInsteadOfReselecting()
    {
        var registry = ThreeTargets();
        var enabled = EnableSecondCopy();
        var article = CanonicalArticleText.CreateQueued("<pinned-before@example.test>", InboundArticleProducer.Post);
        var created = enabled.Intent.TryEstablish(article.Record.ArtId, 1, 3);
        Assert.True(created.Created);
        var client = new RecordingPlacement { ExpectedCalls = 2 };
        var writer = new IncomingSpoolWriterService(
            new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 }),
            new OrderedPersister(),
            Options.Create(enabled.Options),
            NullLogger<IncomingSpoolWriterService>.Instance,
            timeProvider: new FixedTime(Now),
            placement: client,
            placementRegistry: registry,
            replicationIntent: enabled.Intent,
            replicationRoster: enabled.Roster);
        await InvokePlace(writer, article);
        Assert.Equal(2, client.Calls);
        Assert.Equal(1, client.Targets[0].ServerId);
        Assert.Equal(3, client.Targets[1].ServerId);
        Assert.Equal("cache03.example", client.Targets[1].Fqdn);
        Assert.True(enabled.Intent.TryGet(article.Record.ArtId, out var pin));
        Assert.Equal(ReplicationIntentState.Pending, pin.State);
        Assert.Equal(1, pin.SourceServerId);
        Assert.Equal(3, pin.TargetServerId);
        var competing = enabled.Intent.TryEstablish(article.Record.ArtId, 1, 2);
        Assert.False(competing.Created);
        Assert.Equal(3, competing.Intent.TargetServerId);
    }

    [Fact]
    public async Task FailedReplica_LeavesThePinPendingOnTheSameTarget()
    {
        var enabled = EnableSecondCopy();
        var article = CanonicalArticleText.CreateQueued("<pending-pin@example.test>", InboundArticleProducer.Post);
        var client = new RecordingPlacement
        {
            ExpectedCalls = 2,
            Sequence =
            [
                new ArticlePlacementResult(ArticlePlacementKind.Accepted),
                new ArticlePlacementResult(ArticlePlacementKind.TransportFailure, "timeout"),
            ],
        };
        var writer = new IncomingSpoolWriterService(
            new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 }),
            new OrderedPersister(),
            Options.Create(enabled.Options),
            NullLogger<IncomingSpoolWriterService>.Instance,
            timeProvider: new FixedTime(Now),
            placement: client,
            placementRegistry: ThreeTargets(),
            replicationIntent: enabled.Intent,
            replicationRoster: enabled.Roster);
        await InvokePlace(writer, article);
        Assert.Equal(2, client.Calls);
        Assert.True(enabled.Intent.TryGet(article.Record.ArtId, out var pin));
        Assert.Equal(ReplicationIntentState.Pending, pin.State);
        Assert.Equal(client.Targets[1].ServerId, pin.TargetServerId);
        Assert.Equal(client.Targets[0].ServerId, pin.SourceServerId);
    }

    [Fact]
    public async Task StaleRosterTarget_IsDialedWithoutSelectingAnotherServer()
    {
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("cache01.example", 1, 1000, 563), Now);
        var enabled = EnableSecondCopy();
        enabled.Roster.Observe(2, "cache02-offline.example", 564);
        var article = CanonicalArticleText.CreateQueued("<offline-pin@example.test>", InboundArticleProducer.Post);
        Assert.True(enabled.Intent.TryEstablish(article.Record.ArtId, 1, 2).Created);
        var client = new RecordingPlacement
        {
            ExpectedCalls = 2,
            Sequence =
            [
                new ArticlePlacementResult(ArticlePlacementKind.Accepted),
                new ArticlePlacementResult(ArticlePlacementKind.Duplicate),
            ],
        };
        var writer = new IncomingSpoolWriterService(
            new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 }),
            new OrderedPersister(),
            Options.Create(enabled.Options),
            NullLogger<IncomingSpoolWriterService>.Instance,
            timeProvider: new FixedTime(Now),
            placement: client,
            placementRegistry: registry,
            replicationIntent: enabled.Intent,
            replicationRoster: enabled.Roster);
        await InvokePlace(writer, article);
        Assert.Equal(2, client.Calls);
        Assert.Equal(1, client.Targets[0].ServerId);
        Assert.Equal(2, client.Targets[1].ServerId);
        Assert.Equal("cache02-offline.example", client.Targets[1].Fqdn);
        Assert.Equal(564, client.Targets[1].VatpPort);
        Assert.True(enabled.Intent.TryGet(article.Record.ArtId, out var pin));
        Assert.Equal(2, pin.TargetServerId);
        Assert.Equal(ReplicationIntentState.Pending, pin.State);
    }

    private static async Task InvokePlace(IncomingSpoolWriterService writer, InboundArticle article)
    {
        var place = typeof(IncomingSpoolWriterService).GetMethod(
            "PlaceAfterPersistAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(place);
        try
        {
            var pending = (Task)place.Invoke(writer, [article, CancellationToken.None])!;
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static Task RunAsync(
        InboundArticle article,
        OrderedPersister persister,
        RecordingPlacement client,
        StorageServerRegistry registry,
        ListLogger<IncomingSpoolWriterService>? logs = null,
        DateTimeOffset? time = null) =>
        RunCoreAsync(article, persister, client, registry, logs, time);

    private static async Task PlaceDirectAsync(
        InboundArticle article,
        RecordingPlacement client,
        StorageServerRegistry registry,
        CancellationToken cancellationToken,
        ListLogger<IncomingSpoolWriterService>? logs = null)
    {
        var writer = CreateWriter(new OrderedPersister(), client, registry, logs, time: null);
        var place = typeof(IncomingSpoolWriterService).GetMethod(
            "PlaceAfterPersistAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(place);
        try
        {
            var pending = (Task)place.Invoke(writer, [article, cancellationToken])!;
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static IncomingSpoolWriterService CreateWriter(
        OrderedPersister persister,
        RecordingPlacement client,
        StorageServerRegistry registry,
        ListLogger<IncomingSpoolWriterService>? logs,
        DateTimeOffset? time)
    {
        var enabled = EnableSecondCopy();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        return new IncomingSpoolWriterService(
            queue,
            persister,
            Options.Create(enabled.Options),
            (ILogger<IncomingSpoolWriterService>?)logs ?? NullLogger<IncomingSpoolWriterService>.Instance,
            timeProvider: new FixedTime(time ?? Now),
            placement: client,
            placementRegistry: registry,
            replicationIntent: enabled.Intent,
            replicationRoster: enabled.Roster);
    }

    private static async Task<ReplicationIntentStore> RunObservedAsync(
        InboundArticle article,
        OrderedPersister persister,
        RecordingPlacement client,
        StorageServerRegistry registry,
        ListLogger<IncomingSpoolWriterService>? logs = null)
    {
        var enabled = EnableSecondCopy();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = new IncomingSpoolWriterService(
            queue,
            persister,
            Options.Create(enabled.Options),
            (ILogger<IncomingSpoolWriterService>?)logs ?? NullLogger<IncomingSpoolWriterService>.Instance,
            timeProvider: new FixedTime(Now),
            placement: client,
            placementRegistry: registry,
            replicationIntent: enabled.Intent,
            replicationRoster: enabled.Roster);
        await writer.StartAsync(CancellationToken.None);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(article, CancellationToken.None));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await persister.Completed.Task.WaitAsync(cts.Token);
        if (!client.FailIfCalled)
        {
            await client.Finished.Task.WaitAsync(cts.Token);
        }

        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
        return enabled.Intent;
    }

    private static async Task<ReplicationIntentStore> RunManyAsync(
        InboundArticle[] articles,
        ConcurrentPlacement client,
        StorageServerRegistry registry,
        int workers)
    {
        var enabled = EnableSecondCopy(workers);
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = new IncomingSpoolWriterService(
            queue,
            new OrderedPersister(),
            Options.Create(enabled.Options),
            NullLogger<IncomingSpoolWriterService>.Instance,
            timeProvider: new FixedTime(Now),
            placement: client,
            placementRegistry: registry,
            replicationIntent: enabled.Intent,
            replicationRoster: enabled.Roster);
        await writer.StartAsync(CancellationToken.None);
        foreach (var article in articles)
        {
            Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(article, CancellationToken.None));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.BothStored.Task.WaitAsync(cts.Token);
        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
        return enabled.Intent;
    }

    private static async Task RunCoreAsync(
        InboundArticle article,
        OrderedPersister persister,
        RecordingPlacement client,
        StorageServerRegistry registry,
        ListLogger<IncomingSpoolWriterService>? logs,
        DateTimeOffset? time)
    {
        var enabled = EnableSecondCopy();
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = new IncomingSpoolWriterService(
            queue,
            persister,
            Options.Create(enabled.Options),
            (ILogger<IncomingSpoolWriterService>?)logs ?? NullLogger<IncomingSpoolWriterService>.Instance,
            timeProvider: new FixedTime(time ?? Now),
            placement: client,
            placementRegistry: registry,
            replicationIntent: enabled.Intent,
            replicationRoster: enabled.Roster);
        await writer.StartAsync(CancellationToken.None);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(article, CancellationToken.None));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await persister.Completed.Task.WaitAsync(cts.Token);
        if (article.Producer is InboundArticleProducer.TakeThis
                or InboundArticleProducer.IHave
                or InboundArticleProducer.Post
                or InboundArticleProducer.BackFiller
            && !client.FailIfCalled)
        {
            await client.Finished.Task.WaitAsync(cts.Token);
        }

        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
    }

    private static void AssertSameArtData(ArticleRecord expected, ArticleRecord actual)
    {
        Assert.True(MemoryMarshal.TryGetArray(expected.ArtData, out var expectedArray));
        Assert.True(MemoryMarshal.TryGetArray(actual.ArtData, out var actualArray));
        Assert.Same(expectedArray.Array, actualArray.Array);
        Assert.Equal(expectedArray.Offset, actualArray.Offset);
        Assert.Equal(expectedArray.Count, actualArray.Count);
    }

    private static EnabledSecondCopy EnableSecondCopy(int workers = 1)
    {
        var directory = Directory.CreateTempSubdirectory("vnntp-5h3a-").FullName;
        return new EnabledSecondCopy(
            new NntpdOptions
            {
                Replication = new ReplicationOptions
                {
                    SecondCopySender = true,
                    Directory = directory,
                },
                ArticleIngestion = new ArticleIngestionOptions
                {
                    MinWorkers = workers,
                    MaxWorkers = workers,
                    ScaleIntervalSeconds = 3600,
                    OverviewDbMinPublisherWorkers = 1,
                    OverviewDbMaxPublisherWorkers = 1,
                },
            },
            ReplicationIntentStore.Open(directory),
            DurableStorageServerRoster.Open(directory));
    }

    private readonly record struct EnabledSecondCopy(
        NntpdOptions Options,
        ReplicationIntentStore Intent,
        DurableStorageServerRoster Roster);

    private static StorageServerRegistry TwoTargets()
    {
        var registry = RegistryWithTarget();
        registry.ApplyAdvertisement(Advertisement("cache02.example", 2, 100, 564), Now);
        return registry;
    }

    private static StorageServerRegistry ThreeTargets()
    {
        var registry = TwoTargets();
        registry.ApplyAdvertisement(Advertisement("cache03.example", 3, 10, 565), Now);
        return registry;
    }

    private static StorageServerRegistry DivergentTargets()
    {
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("lowid.example", 2, 10, 564), Now);
        registry.ApplyAdvertisement(Advertisement("highspace.example", 9, 5000, 563), Now);
        return registry;
    }

    private static StorageServerRegistry RegistryWithTarget()
    {
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("cache01.example", 1, 1000, 563), Now);
        return registry;
    }

    private static StorageServerAdvertisement Advertisement(string fqdn, int serverId, long available, int? port) =>
        new(1, serverId, fqdn, 10_000, 10_000 - available, available, Now, port);

    private static StorageServerFleetEntry Entry(string fqdn, int serverId, long available, int? port) =>
        new(serverId, fqdn, 10_000, 10_000 - available, available, Now, port);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class OrderedPersister : IIncomingArticlePersister
    {
        public int Count { get; private set; }

        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PersistAsync(InboundArticle article, CancellationToken cancellationToken)
        {
            Count++;
            Completed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private enum CancelPoint
    {
        None,
        FirstCallReturnsCancelled,
        AfterFirstAccepted,
        SecondCallReturnsCancelled,
    }

    private sealed class ConcurrentPlacement : IArticlePlacementClient
    {
        private readonly Barrier _barrier = new(2);
        private readonly List<StorageServerFleetEntry> _targets = [];
        private readonly List<ArticleRecord> _records = [];
        private readonly object _gate = new();

        public int Calls { get; private set; }

        public IReadOnlyList<StorageServerFleetEntry> Targets
        {
            get
            {
                lock (_gate)
                {
                    return [.. _targets];
                }
            }
        }

        public IReadOnlyList<ArticleRecord> Records
        {
            get
            {
                lock (_gate)
                {
                    return [.. _records];
                }
            }
        }

        public TaskCompletionSource BothStored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ArticlePlacementResult> PlaceAsync(
            ArticleRecord record,
            StorageServerFleetEntry target,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Calls++;
                _targets.Add(target);
                _records.Add(record);
            }

            _barrier.SignalAndWait(cancellationToken);
            BothStored.TrySetResult();
            return ValueTask.FromResult(new ArticlePlacementResult(ArticlePlacementKind.Accepted));
        }
    }

    private sealed class RecordingPlacement : IArticlePlacementClient
    {
        private readonly List<StorageServerFleetEntry> _targets = [];
        private readonly List<ArticleRecord> _records = [];
        private readonly List<ArticlePlacementResult> _outcomes = [];

        public int Calls { get; private set; }

        public bool SawPersist { get; private set; }

        public bool FailIfCalled { get; init; }

        public ArticleRecord Last { get; private set; }

        public ArticlePlacementResult Result { get; init; } = new(ArticlePlacementKind.Accepted);

        public ArticlePlacementResult[]? Sequence { get; init; }

        public int ExpectedCalls { get; init; } = 1;

        public CancelPoint CancelAt { get; init; }

        public CancellationTokenSource? Cancel { get; init; }

        public Action<int, StorageServerFleetEntry>? BeforeStore { get; init; }

        public IReadOnlyList<StorageServerFleetEntry> Targets => _targets;

        public IReadOnlyList<ArticleRecord> Records => _records;

        public IReadOnlyList<ArticlePlacementResult> Outcomes => _outcomes;

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ArticlePlacementResult> PlaceAsync(
            ArticleRecord record,
            StorageServerFleetEntry target,
            CancellationToken cancellationToken)
        {
            if (FailIfCalled)
            {
                throw new InvalidOperationException("Placement dialed without an eligible server.");
            }

            var call = ++Calls;
            SawPersist = true;
            Last = record;
            _targets.Add(target);
            _records.Add(record);
            BeforeStore?.Invoke(call, target);

            ArticlePlacementResult outcome;
            if (CancelAt == CancelPoint.FirstCallReturnsCancelled && call == 1)
            {
                Cancel!.Cancel();
                outcome = new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
            }
            else if (CancelAt == CancelPoint.AfterFirstAccepted && call == 1)
            {
                outcome = new ArticlePlacementResult(ArticlePlacementKind.Accepted);
                Cancel!.Cancel();
            }
            else if (CancelAt == CancelPoint.SecondCallReturnsCancelled && call == 2)
            {
                Cancel!.Cancel();
                outcome = new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                outcome = new ArticlePlacementResult(ArticlePlacementKind.Cancelled);
            }
            else
            {
                outcome = Sequence is { } sequence && call - 1 < sequence.Length
                    ? sequence[call - 1]
                    : Result;
            }

            _outcomes.Add(outcome);
            if (call >= ExpectedCalls)
            {
                Finished.TrySetResult();
            }

            return ValueTask.FromResult(outcome);
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<int> EventIds { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            EventIds.Add(eventId.Id);
        }
    }

    private sealed class Link : IAsyncDisposable
    {
        private readonly Pipe _toServer = new();
        private readonly Pipe _toClient = new();

        public Link()
        {
            Client = new PipeStream(_toClient.Reader, _toServer.Writer);
            Server = new PipeStream(_toServer.Reader, _toClient.Writer);
        }

        public Stream Client { get; }

        public Stream Server { get; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private sealed class PipeStream(PipeReader reader, PipeWriter writer) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (result.Buffer.IsEmpty && result.IsCompleted)
            {
                return 0;
            }

            var length = (int)Math.Min(buffer.Length, result.Buffer.Length);
            var written = 0;
            foreach (var segment in result.Buffer.Slice(0, length))
            {
                segment.Span.CopyTo(buffer.Span[written..]);
                written += segment.Length;
            }

            reader.AdvanceTo(result.Buffer.GetPosition(length));
            return length;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var copied = buffer.ToArray();
            await writer.WriteAsync(copied, cancellationToken).ConfigureAwait(false);
            var flushed = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (flushed.IsCompleted)
            {
                throw new IOException("peer closed");
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await writer.CompleteAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static class ScriptedServer
    {
        public static async Task<bool> RunAsync(Stream stream, byte? result)
        {
            var pending = new List<byte>();
            var buffer = new byte[8192];
            var windows = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    return windows > 0 || result is null;
                }

                pending.AddRange(buffer.AsSpan(0, read).ToArray());
                while (pending.Count > 0)
                {
                    var parsed = VatpFrameParser.ParseOneFrame(pending.ToArray(), VatpProtocol.DefaultMaxFramePayload);
                    if (parsed.Status == VatpFrameParseStatus.Incomplete)
                    {
                        break;
                    }

                    if (parsed.Status != VatpFrameParseStatus.Success || parsed.Frame is not { } frame)
                    {
                        return false;
                    }

                    pending.RemoveRange(0, checked((int)parsed.ConsumedBytes));
                    if (frame.Header.Type == VatpFrameType.Hello)
                    {
                        var hello = VatpFrameEncoder.ToSingleBuffer(
                            VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload));
                        await stream.WriteAsync(hello);
                    }
                    else if (frame.Header.Type == VatpFrameType.Data && frame.Payload.Length > 0)
                    {
                        windows++;
                        var window = VatpFrameEncoder.ToSingleBuffer(
                            VatpFrameEncoder.EncodeWindow(frame.Header.StreamId, checked((uint)frame.Payload.Length)));
                        await stream.WriteAsync(window);
                    }
                    else if (frame.Header.Type == VatpFrameType.End)
                    {
                        if (result is byte outcome)
                        {
                            var encoded = VatpFrameEncoder.ToSingleBuffer(
                                VatpFrameEncoder.EncodeResult(frame.Header.StreamId, outcome));
                            await stream.WriteAsync(encoded);
                        }
                        else
                        {
                            await stream.DisposeAsync();
                        }

                        return true;
                    }
                }
            }
        }
    }
}
