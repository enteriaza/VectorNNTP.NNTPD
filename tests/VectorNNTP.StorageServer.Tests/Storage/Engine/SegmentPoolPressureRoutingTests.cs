using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Placement, pressure reclamation, and shared-filesystem admission go through the engine.
/// </summary>
public sealed class SegmentPoolPressureRoutingTests
{
    [Fact]
    public async Task Writes_stay_on_the_active_spool_while_it_is_below_critical()
    {
        using var dir = PoolDir.Create(2);
        var first = Record("<pool-stay-a@seg.test>");
        var second = Record("<pool-stay-b@seg.test>");
        var length = SegmentRecordCodec.RecordLengthForArtSize(first.ArtSize);
        Assert.Equal(length, SegmentRecordCodec.RecordLengthForArtSize(second.ArtSize));
        await using var engine = Open(dir, max: length * 10, readers: Wide());

        await AcceptAndDrain(engine, first);
        await AcceptAndDrain(engine, second);

        Assert.Equal(length * 2, FileBytes(dir.Pools[0]));
        Assert.Equal(0, FileBytes(dir.Pools[1]));
    }

    [Fact]
    public async Task Concurrent_writes_split_when_the_reservation_fills_the_active_spool()
    {
        using var dir = PoolDir.Create(2);
        var first = Record("<pool-split-a@seg.test>");
        var second = Record("<pool-split-b@seg.test>");
        var length = SegmentRecordCodec.RecordLengthForArtSize(first.ArtSize);
        await using var engine = Open(dir, max: length, readers: Wide());

        engine.SuspendBackgroundPersist = true;
        var accepted = await Task.WhenAll(Accept(engine, first), Accept(engine, second));
        Assert.All(accepted, static result => Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome));
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(length, FileBytes(dir.Pools[0]));
        Assert.Equal(length, FileBytes(dir.Pools[1]));
    }

    [Fact]
    public async Task A_critical_spool_routes_a_fitting_write_to_a_healthy_spool()
    {
        using var dir = PoolDir.Create(2);
        var large = Record("<pool-critical@seg.test>", string.Join("\r\n", Enumerable.Repeat(new string('x', 80), 40)));
        var small = Record("<pool-healthy@seg.test>", "x");
        var largeLength = SegmentRecordCodec.RecordLengthForArtSize(large.ArtSize);
        var smallLength = SegmentRecordCodec.RecordLengthForArtSize(small.ArtSize);
        var max = largeLength + smallLength;
        Assert.True(largeLength * 100 >= max * 90);
        Assert.True(largeLength + smallLength <= max);

        await using var engine = Open(dir, max, Wide());
        await AcceptAndDrain(engine, large);
        await AcceptAndDrain(engine, small);

        Assert.Equal(largeLength, FileBytes(dir.Pools[0]));
        Assert.Equal(smallLength, FileBytes(dir.Pools[1]));
        Assert.True(engine.TryRead(small.ArtId, out var kept));
        Assert.True(kept.ArtData.Span.SequenceEqual(small.ArtData.Span));
    }

    [Fact]
    public async Task Pressure_reclaims_before_critical_and_a_healthy_article_survives()
    {
        using var dir = PoolDir.Create(2);
        var time = Clock();
        var pressured = Record("<pool-pressure@seg.test>");
        var length = SegmentRecordCodec.RecordLengthForArtSize(pressured.ArtSize);
        var pressureMax = length * 100 / 82;
        Assert.InRange(length * 100 / pressureMax, 80, 84);
        var caps = new[] { pressureMax, 10_000_000L };
        await using var engine = Open(dir, caps, Wide(), time: time);
        await AcceptAndDrain(engine, pressured);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.Equal(BulkStoragePressureState.Pressure, engine.ObserveSegmentPoolPressure().State);

        var coordinator = Coordinator(engine);
        var cycle = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, FileBytes(dir.Pools[0]));
        Assert.False(engine.TryRead(pressured.ArtId, out _));
        Assert.NotEqual(nameof(BulkStoragePressureState.Critical), cycle.BulkPressureState);

        time.Advance(TimeSpan.FromSeconds(1));
        var again = Record("<pool-recovered@seg.test>");
        var againLength = SegmentRecordCodec.RecordLengthForArtSize(again.ArtSize);
        await AcceptAndDrain(engine, again);
        Assert.Equal(againLength, FileBytes(dir.Pools[0]));
        time.Advance(TimeSpan.FromSeconds(1));
        var healthy = Record("<pool-kept@seg.test>");
        var healthyLength = SegmentRecordCodec.RecordLengthForArtSize(healthy.ArtSize);
        await AcceptAndDrain(engine, healthy);
        Assert.Equal(healthyLength, FileBytes(dir.Pools[1]));

        var settled = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(nameof(BulkStoragePressureState.Normal), settled.BulkPressureState);
        Assert.False(engine.TryRead(again.ArtId, out _));
        Assert.Equal(0, FileBytes(dir.Pools[0]));
        Assert.True(engine.TryRead(healthy.ArtId, out var kept));
        Assert.True(kept.ArtData.Span.SequenceEqual(healthy.ArtData.Span));
        Assert.Equal(healthyLength, FileBytes(dir.Pools[1]));
    }

    [Fact]
    public async Task Warning_does_not_reclaim_a_spool()
    {
        using var dir = PoolDir.Create(1);
        var article = Record("<pool-warning@seg.test>");
        var length = SegmentRecordCodec.RecordLengthForArtSize(article.ArtSize);
        var max = length * 100 / 76;
        Assert.InRange(length * 100 / max, 75, 79);
        await using var engine = Open(dir, max, [new FixedReader(1_000_000_000, 100_000)]);
        await AcceptAndDrain(engine, article);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.Equal(BulkStoragePressureState.Warning, engine.ObserveSegmentPoolPressure().State);

        _ = await Coordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(length, FileBytes(dir.Pools[0]));
        Assert.True(engine.TryRead(article.ArtId, out var kept));
        Assert.True(kept.ArtData.Span.SequenceEqual(article.ArtData.Span));
    }

    [Fact]
    public async Task Spools_on_one_filesystem_share_one_reserve_budget()
    {
        using var dir = PoolDir.Create(2);
        var article = Record("<pool-shared@seg.test>");
        var length = SegmentRecordCodec.RecordLengthForArtSize(article.ArtSize);
        var reader = new FileAwareReader(1_000, length + 50, dir.Pools);
        await using var engine = Open(dir, max: 1_000_000, [reader, reader], new SharedProbe("same"));

        await AcceptAndDrain(engine, article);
        var blocked = await engine.AcceptAsync(Record("<pool-shared-b@seg.test>"), CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, blocked.Outcome);
        Assert.Equal(SegmentPoolCapacity.BackpressureReason, blocked.Reason);
        Assert.Equal(length, FileBytes(dir.Pools[0]) + FileBytes(dir.Pools[1]));
    }

    [Fact]
    public async Task Concurrent_accepts_cannot_share_one_filesystem_budget_twice()
    {
        using var dir = PoolDir.Create(2);
        var length = SegmentRecordCodec.RecordLengthForArtSize(Record("<pool-fs-a@seg.test>").ArtSize);
        var reader = new FileAwareReader(1_000, length + 50, dir.Pools);
        await using var engine = Open(dir, max: 1_000_000, [reader, reader], new SharedProbe("same"));
        engine.SuspendBackgroundPersist = true;
        var results = await Task.WhenAll(
            Accept(engine, Record("<pool-fs-a@seg.test>")),
            Accept(engine, Record("<pool-fs-b@seg.test>")));
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(1, results.Count(static result => result.Outcome == ArticleAcceptOutcome.Accepted));
        Assert.Contains(results, static result => result.Outcome == ArticleAcceptOutcome.RejectedCapacity);
        Assert.True(FileBytes(dir.Pools[0]) + FileBytes(dir.Pools[1]) <= length);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
    }

    [Fact]
    public async Task Separate_filesystems_enforce_reserves_independently()
    {
        using var dir = PoolDir.Create(2);
        var left = Record("<pool-indep-a@seg.test>");
        var right = Record("<pool-indep-b@seg.test>");
        var leftLength = SegmentRecordCodec.RecordLengthForArtSize(left.ArtSize);
        var rightLength = SegmentRecordCodec.RecordLengthForArtSize(right.ArtSize);
        var readers = new IStorageCapacityReader[]
        {
            new FileAwareReader(1_000, leftLength + 50, [dir.Pools[0]]),
            new FileAwareReader(1_000, rightLength + 50, [dir.Pools[1]]),
        };
        await using var engine = Open(
            dir,
            max: 1_000_000,
            readers,
            new MappedProbe(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [dir.Pools[0]] = "vol-a",
                [dir.Pools[1]] = "vol-b",
            }));

        await AcceptAndDrain(engine, left);
        await AcceptAndDrain(engine, right);

        Assert.Equal(leftLength, FileBytes(dir.Pools[0]));
        Assert.Equal(rightLength, FileBytes(dir.Pools[1]));
    }

    [Fact]
    public async Task A_failed_measurement_rejects_the_write()
    {
        using var dir = PoolDir.Create(1);
        await using var engine = Open(dir, max: 1_000_000, [new ThrowingReader()]);

        var rejected = await engine.AcceptAsync(Record("<pool-unmeasured@seg.test>"), CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, rejected.Outcome);
        Assert.Equal(SegmentPoolCapacity.UnmeasuredReason, rejected.Reason);
        Assert.Equal(0, FileBytes(dir.Pools[0]));
    }

    [Fact]
    public async Task A_stale_free_space_sample_does_not_expire_the_next_article()
    {
        using var dir = PoolDir.Create(1);
        var time = Clock();
        var body = string.Join("\r\n", Enumerable.Repeat(new string('x', 80), 140));
        var older = Record("<pool-stale-old@seg.test>", body);
        var newer = Record("<pool-stale-new@seg.test>", body);
        var length = SegmentRecordCodec.RecordLengthForArtSize(older.ArtSize);
        Assert.True(length * 100 > 200_000 * 5);
        var stuck = new FixedReader(200_000, 160_000);
        await using var engine = Open(dir, max: 1_000_000, [stuck], time: time, primary: stuck);
        await AcceptAndDrain(engine, older);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        await AcceptAndDrain(engine, newer);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        var coordinator = Coordinator(engine);
        _ = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.False(engine.TryRead(older.ArtId, out _));
        Assert.True(engine.TryRead(newer.ArtId, out var kept));
        Assert.True(kept.ArtData.Span.SequenceEqual(newer.ArtData.Span));

        var second = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(nameof(BulkStoragePressureState.Normal), second.BulkPressureState);
        Assert.True(engine.TryRead(newer.ArtId, out var still));
        Assert.True(still.ArtData.Span.SequenceEqual(newer.ArtData.Span));
        Assert.Equal(length, FileBytes(dir.Pools[0]));
    }

    [Fact]
    public async Task Restart_restores_spool_bytes_and_the_article()
    {
        using var dir = PoolDir.Create(2);
        var first = Record("<pool-restart-a@seg.test>");
        var second = Record("<pool-restart-b@seg.test>");
        var length = SegmentRecordCodec.RecordLengthForArtSize(first.ArtSize);
        var readers = Wide();

        await using (var engine = Open(dir, length, readers))
        {
            await AcceptAndDrain(engine, first);
            await AcceptAndDrain(engine, second);
        }

        await using var restarted = Open(dir, length, readers);
        Assert.True(restarted.TryRead(first.ArtId, out var left));
        Assert.True(left.ArtData.Span.SequenceEqual(first.ArtData.Span));
        Assert.True(restarted.TryRead(second.ArtId, out var right));
        Assert.True(right.ArtData.Span.SequenceEqual(second.ArtData.Span));
        var advertised = restarted.ReadFleetAdvertisement();
        Assert.Equal(length * 2, advertised.UsedBytes);
        Assert.Equal(length, FileBytes(dir.Pools[0]));
        Assert.Equal(length, FileBytes(dir.Pools[1]));
    }

    [Fact]
    public async Task Restart_replays_outstanding_work_onto_the_spool_that_still_has_room()
    {
        using var dir = PoolDir.Create(2);
        var committed = Record("<pool-replay-a@seg.test>");
        var outstanding = Record("<pool-replay-b@seg.test>");
        var length = SegmentRecordCodec.RecordLengthForArtSize(committed.ArtSize);
        Assert.Equal(length, SegmentRecordCodec.RecordLengthForArtSize(outstanding.ArtSize));
        var readers = Wide();

        await using (var engine = Open(dir, length, readers))
        {
            await AcceptAndDrain(engine, committed);
            engine.SuspendBackgroundPersist = true;
            var accepted = await engine.AcceptAsync(outstanding, CancellationToken.None);
            Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
            Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
            Assert.Equal(length, FileBytes(dir.Pools[0]));
            Assert.Equal(0, FileBytes(dir.Pools[1]));
        }

        await using var restarted = Open(dir, length, readers);
        restarted.SuspendBackgroundPersist = true;
        Assert.Equal(length, FileBytes(dir.Pools[0]));
        Assert.Equal(0, FileBytes(dir.Pools[1]));
        Assert.Equal(0, restarted.ProcessLocalArticleReservedBytes);
        Assert.True(restarted.Journal.TryGetOutstanding(outstanding.ArtId, out _));
        Assert.True(restarted.TryRead(committed.ArtId, out var kept));
        Assert.True(kept.ArtData.Span.SequenceEqual(committed.ArtData.Span));
        Assert.True(restarted.TryRead(outstanding.ArtId, out var fromJournal));
        Assert.True(fromJournal.ArtData.Span.SequenceEqual(outstanding.ArtData.Span));

        await restarted.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(0, restarted.ProcessLocalArticleReservedBytes);
        Assert.Equal(length, FileBytes(dir.Pools[0]));
        Assert.Equal(length, FileBytes(dir.Pools[1]));
        Assert.False(restarted.Journal.TryGetOutstanding(outstanding.ArtId, out _));
        Assert.True(restarted.TryRead(outstanding.ArtId, out var replayed));
        Assert.True(replayed.ArtData.Span.SequenceEqual(outstanding.ArtData.Span));
        Assert.Equal(length * 2, restarted.ReadFleetAdvertisement().UsedBytes);
    }

    [Fact]
    public async Task Concurrent_reservations_cannot_exceed_a_spool_cap()
    {
        using var dir = PoolDir.Create(2);
        var length = SegmentRecordCodec.RecordLengthForArtSize(Record("<pool-bound-a@seg.test>").ArtSize);
        await using var engine = Open(dir, length, Wide());
        engine.SuspendBackgroundPersist = true;
        var first = await Task.WhenAll(
            Accept(engine, Record("<pool-bound-a@seg.test>")),
            Accept(engine, Record("<pool-bound-b@seg.test>")));
        Assert.All(first, static result => Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome));
        var third = await Accept(engine, Record("<pool-bound-c@seg.test>"));
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, third.Outcome);
        Assert.Equal(SegmentPoolCapacity.HardLimitReason, third.Reason);
        Assert.Equal(length, FileBytes(dir.Pools[0]));
        Assert.Equal(length, FileBytes(dir.Pools[1]));
    }

    [Fact]
    public async Task A_fitting_write_leaves_a_pressured_spool()
    {
        using var dir = PoolDir.Create(2);
        var large = Record("<pool-press@seg.test>", string.Join("\r\n", Enumerable.Repeat(new string('x', 80), 20)));
        var small = Record("<pool-room@seg.test>", "x");
        var largeLength = SegmentRecordCodec.RecordLengthForArtSize(large.ArtSize);
        var smallLength = SegmentRecordCodec.RecordLengthForArtSize(small.ArtSize);
        var max = largeLength * 100 / 82;
        Assert.InRange(largeLength * 100 / max, 80, 84);
        Assert.True(largeLength + smallLength <= max);

        await using var engine = Open(dir, max, Wide());
        await AcceptAndDrain(engine, large);
        await AcceptAndDrain(engine, small);

        Assert.Equal(largeLength, FileBytes(dir.Pools[0]));
        Assert.Equal(smallLength, FileBytes(dir.Pools[1]));
    }

    [Fact]
    public async Task A_reservation_on_another_spool_is_not_charged_to_the_primary_ledger()
    {
        using var dir = PoolDir.Create(2);
        var first = Record("<pool-own-a@seg.test>");
        var second = Record("<pool-own-b@seg.test>");
        var length = SegmentRecordCodec.RecordLengthForArtSize(first.ArtSize);
        Assert.Equal(length, SegmentRecordCodec.RecordLengthForArtSize(second.ArtSize));
        await using var engine = Open(dir, length, Wide());
        await AcceptAndDrain(engine, first);

        engine.SuspendBackgroundPersist = true;
        var accepted = await engine.AcceptAsync(second, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.True(engine.ProcessLocalJournalReservedBytes > 0);

        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(length, FileBytes(dir.Pools[0]));
        Assert.Equal(length, FileBytes(dir.Pools[1]));
        Assert.True(engine.TryRead(second.ArtId, out var kept));
        Assert.True(kept.ArtData.Span.SequenceEqual(second.ArtData.Span));
    }

    [Fact]
    public async Task A_rejected_group_member_leaves_no_segment_charge()
    {
        using var dir = PoolDir.Create(2);
        var length = SegmentRecordCodec.RecordLengthForArtSize(Record("<pool-grp-a@seg.test>").ArtSize);
        await using var engine = Open(dir, length, Wide());
        engine.SuspendBackgroundPersist = true;
        var results = await Task.WhenAll(
            Accept(engine, Record("<pool-grp-a@seg.test>")),
            Accept(engine, Record("<pool-grp-b@seg.test>")),
            Accept(engine, Record("<pool-grp-c@seg.test>")));
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(2, results.Count(static result => result.Outcome == ArticleAcceptOutcome.Accepted));
        Assert.Contains(results, static result => result.Outcome == ArticleAcceptOutcome.RejectedCapacity);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);
        Assert.True(FileBytes(dir.Pools[0]) <= length);
        Assert.True(FileBytes(dir.Pools[1]) <= length);
    }

    [Fact]
    public async Task Active_segment_count_does_not_assign_one_writer_per_spool()
    {
        using var dir = PoolDir.Create(2);
        var first = Record("<pool-writers-a@seg.test>");
        var second = Record("<pool-writers-b@seg.test>");
        var length = SegmentRecordCodec.RecordLengthForArtSize(first.ArtSize);
        var options = Room(dir.Options(length * 10)) with { ActiveSegmentCount = 2 };
        await using var engine = FileArticleStorageEngine.Open(
            options,
            new SharedProbe("wide"),
            logger: NullLogger.Instance,
            segmentPoolReaders: Wide());

        await AcceptAndDrain(engine, first);
        await AcceptAndDrain(engine, second);

        Assert.Equal(2, engine.Segments.SegmentRoots.Count);
        Assert.Equal(length * 2, FileBytes(dir.Pools[0]));
        Assert.Equal(0, FileBytes(dir.Pools[1]));
    }

    [Fact]
    public async Task Compaction_uses_the_other_spool_when_the_primary_filesystem_cannot()
    {
        using var dir = PoolDir.Create(2);
        var article = Record("<pool-compact@seg.test>");
        var length = SegmentRecordCodec.RecordLengthForArtSize(article.ArtSize);
        var primary = new FileAwareReader(1_000, length + 10, [dir.Pools[0]]);
        var readers = new IStorageCapacityReader[]
        {
            primary,
            new FixedReader(1_000_000, 1_000),
        };
        await using var engine = Open(
            dir,
            max: 1_000_000,
            readers,
            new MappedProbe(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [dir.Pools[0]] = "vol-full",
                [dir.Pools[1]] = "vol-open",
            }),
            primary: primary);
        await AcceptAndDrain(engine, article);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.Index.TryGet(article.ArtId, out var row));

        var compact = await engine.CompactClosedSegmentAsync(row.Location.SegmentId, CancellationToken.None);

        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(length, FileBytes(dir.Pools[1]));
        Assert.True(engine.TryRead(article.ArtId, out var kept));
        Assert.True(kept.ArtData.Span.SequenceEqual(article.ArtData.Span));
    }

    private static FileArticleStorageEngine Open(
        PoolDir dir,
        long max,
        IReadOnlyList<IStorageCapacityReader> readers,
        IStorageVolumeProbe? probe = null,
        TimeProvider? time = null,
        IStorageCapacityReader? primary = null) =>
        Open(dir, Enumerable.Repeat(max, dir.Pools.Length).ToArray(), readers, probe, time, primary);

    private static FileArticleStorageEngine Open(
        PoolDir dir,
        long[] caps,
        IReadOnlyList<IStorageCapacityReader> readers,
        IStorageVolumeProbe? probe = null,
        TimeProvider? time = null,
        IStorageCapacityReader? primary = null) =>
        FileArticleStorageEngine.Open(
            Room(dir.Options(caps)),
            probe ?? new SharedProbe("wide"),
            capacityReader: primary,
            logger: NullLogger.Instance,
            timeProvider: time,
            segmentPoolReaders: readers);

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private static ArticleStorageRuntimeOptions Room(ArticleStorageRuntimeOptions options) =>
        options with
        {
            CapacityMaximumUtilization = 100,
            CapacityCompactionHeadroom = 0,
            CapacityMaximumUsageCapacity = 100,
            CapacityFreeCapacity = 1,
        };

    private static IStorageCapacityReader[] Wide() => [new FixedReader(1_000_000_000, 100_000), new FixedReader(1_000_000_000, 100_000)];

    private static StorageMaintenanceCoordinator Coordinator(FileArticleStorageEngine engine) =>
        new(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: 10),
            logger: NullLogger.Instance,
            maxRetentionAge: TimeSpan.Zero,
            bulkPressure: new BulkStoragePressureOptions());

    private static async Task<ArticleAcceptResult> Accept(FileArticleStorageEngine engine, ArticleRecord record) =>
        await engine.AcceptAsync(record, CancellationToken.None);

    private static async Task AcceptAndDrain(FileArticleStorageEngine engine, ArticleRecord record)
    {
        engine.SuspendBackgroundPersist = true;
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.True(
            accepted.Outcome == ArticleAcceptOutcome.Accepted,
            accepted.Reason ?? accepted.Outcome.ToString());
        await engine.DrainPendingAsync(CancellationToken.None);
    }

    private static long FileBytes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long sum = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "seg-*"))
        {
            sum += new FileInfo(file).Length;
        }

        return sum;
    }

    private static ArticleRecord Record(string messageId, string? body = null)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: pools\r\n\r\n").Append(body ?? "body").Append("\r\n");
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class FixedReader(long total, long used) : IStorageCapacityReader
    {
        public StorageCapacitySnapshot Read() => new(total, used, Math.Max(0, total - used));
    }

    private sealed class ThrowingReader : IStorageCapacityReader
    {
        public StorageCapacitySnapshot Read() =>
            throw new InvalidOperationException("segment filesystem could not be measured");
    }

    private sealed class FileAwareReader(long total, long free, string[] directories) : IStorageCapacityReader
    {
        public StorageCapacitySnapshot Read()
        {
            long files = 0;
            foreach (var directory in directories)
            {
                files += FileBytes(directory);
            }

            var available = Math.Max(0, free - files);
            return new StorageCapacitySnapshot(total, Math.Max(0, total - available), available);
        }
    }

    private sealed class SharedProbe(string key) : IStorageVolumeProbe
    {
        public bool TryResolve(string directoryPath, out StorageVolumeIdentity identity)
        {
            identity = new StorageVolumeIdentity(key);
            return true;
        }
    }

    private sealed class MappedProbe(Dictionary<string, string> keys) : IStorageVolumeProbe
    {
        public bool TryResolve(string directoryPath, out StorageVolumeIdentity identity)
        {
            identity = new StorageVolumeIdentity(
                keys.TryGetValue(directoryPath, out var key) ? key : "control");
            return true;
        }
    }

    private sealed class PoolDir : IDisposable
    {
        private PoolDir(string root, string control, string[] pools)
        {
            Root = root;
            Control = control;
            Pools = pools;
        }

        public string Root { get; }

        public string Control { get; }

        public string[] Pools { get; }

        public static PoolDir Create(int poolCount)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-route-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            Directory.CreateDirectory(control);
            var pools = new string[poolCount];
            for (var i = 0; i < poolCount; i++)
            {
                pools[i] = Path.Combine(root, "pool-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(pools[i]);
            }

            return new PoolDir(root, control, pools);
        }

        public ArticleStorageRuntimeOptions Options(long maxCapacity) =>
            Options(Enumerable.Repeat(maxCapacity, Pools.Length).ToArray());

        public ArticleStorageRuntimeOptions Options(long[] caps)
        {
            var runtimes = new SegmentPoolRuntime[Pools.Length];
            for (var i = 0; i < runtimes.Length; i++)
            {
                runtimes[i] = new SegmentPoolRuntime(i, Pools[i], caps[i]);
            }

            return new ArticleStorageRuntimeOptions(
                Control,
                Pools[0],
                ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                ArticleStorageOptions.DefaultJournalHardLimitBytes,
                ArticleStorageOptions.DefaultSegmentTargetSizeBytes,
                SegmentPools: runtimes);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
