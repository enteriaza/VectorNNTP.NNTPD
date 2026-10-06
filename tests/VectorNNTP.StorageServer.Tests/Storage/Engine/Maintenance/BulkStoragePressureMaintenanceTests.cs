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

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Maintenance;

/// <summary>
/// Maintenance uses the bulk watermark to choose existing work. It does not expire an article
/// early and it does not discard acknowledged ingress.
/// </summary>
public sealed class BulkStoragePressureMaintenanceTests
{
    private static readonly DateTimeOffset Arrival = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Normal_does_not_pressure_expire_and_still_rewrites_low_density()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(1_000_000_000, 0);
        var time = new FakeTimeProvider(Arrival);
        await using var engine = Open(dir, reader, time);
        var keep = await PublishAsync(engine, "<bulk-normal-keep@example>");
        var drop = await PublishAsync(engine, "<bulk-normal-drop@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var source = Location(engine, keep.ArtId).SegmentId;
        Assert.True(engine.TryEvict(drop.ArtId));
        reader.UsedBytes = 100_000_000;

        var result = await Coordinator(engine, TimeSpan.FromHours(24)).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(nameof(BulkStoragePressureState.Normal), result.BulkPressureState);
        Assert.Equal("AgeRetention", result.BulkMaintenanceMode);
        Assert.False(result.BulkRewriteSuppressed);
        Assert.False(result.BulkEmergencyAdmissionProtectionRequired);
        Assert.Equal(ArticleStorageState.Present, State(engine, keep.ArtId));
        Assert.NotEqual(source, Location(engine, keep.ArtId).SegmentId);
    }

    [Fact]
    public async Task Warning_does_not_force_eviction()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(1_000_000_000, 0);
        await using var engine = Open(dir, reader, new FakeTimeProvider(Arrival));
        var record = await PublishAsync(engine, "<bulk-warning@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        reader.UsedBytes = 770_000_000;

        var result = await Coordinator(engine, TimeSpan.FromHours(24)).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.NoWork, result.Outcome);
        Assert.Equal(nameof(BulkStoragePressureState.Warning), result.BulkPressureState);
        Assert.Equal("AgeRetention", result.BulkMaintenanceMode);
        Assert.False(result.BulkRewriteSuppressed);
        Assert.Equal(ArticleStorageState.Present, State(engine, record.ArtId));
        Assert.False(result.CompactionAttempted);
        Assert.False(result.Reclaimed);
    }

    [Fact]
    public async Task Pressure_reclaims_a_fully_dead_segment_before_a_rewrite()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(1_000_000_000, 0);
        await using var engine = Open(dir, reader, new FakeTimeProvider(Arrival));
        var deadA = await PublishAsync(engine, "<bulk-dead-a@example>");
        var deadB = await PublishAsync(engine, "<bulk-dead-b@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var deadSegment = Location(engine, deadA.ArtId).SegmentId;
        Assert.True(engine.TryEvict(deadA.ArtId));
        Assert.True(engine.TryEvict(deadB.ArtId));

        var keep = await PublishAsync(engine, "<bulk-pressure-keep@example>");
        var drop = await PublishAsync(engine, "<bulk-pressure-drop@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var liveSegment = Location(engine, keep.ArtId).SegmentId;
        Assert.True(engine.TryEvict(drop.ArtId));
        reader.UsedBytes = 820_000_000;

        var first = await Coordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, first.Outcome);
        Assert.Equal(nameof(BulkStoragePressureState.Pressure), first.BulkPressureState);
        Assert.Equal("PrioritizeReclaimable", first.BulkMaintenanceMode);
        Assert.False(first.CompactionAttempted);
        Assert.False(first.BulkRewriteSuppressed);
        Assert.False(engine.Catalogue.TryGet(deadSegment, out _));
        Assert.True(engine.Catalogue.TryGet(liveSegment, out var stillClosed));
        Assert.Equal(SegmentState.Closed, stillClosed.State);
        Assert.Equal(ArticleStorageState.Present, State(engine, keep.ArtId));
    }

    [Fact]
    public async Task High_withholds_a_rewrite_that_would_consume_reserves()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(1_000_000_000, 0);
        await using var engine = Open(dir, reader, new FakeTimeProvider(Arrival));
        var keep = await PublishAsync(engine, "<bulk-high-keep@example>");
        var drop = await PublishAsync(engine, "<bulk-high-drop@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var before = Location(engine, keep.ArtId);
        Assert.True(engine.TryEvict(drop.ArtId));
        reader.UsedBytes = 870_000_000;

        var result = await Coordinator(engine).RunOnceAsync(CancellationToken.None);

        Assert.Equal(nameof(BulkStoragePressureState.High), result.BulkPressureState);
        Assert.Equal("AggressiveReclaim", result.BulkMaintenanceMode);
        Assert.True(result.BulkRewriteSuppressed);
        Assert.False(result.CompactionAttempted);
        Assert.Equal(before, Location(engine, keep.ArtId));
        Assert.Equal(ArticleStorageState.Present, State(engine, keep.ArtId));
        Assert.True(engine.Catalogue.TryGet(before.SegmentId, out _));
    }

    [Fact]
    public async Task High_allows_a_rewrite_when_reserves_still_cover_the_copy()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(1_000_000_000, 0);
        await using var engine = Open(dir, reader, new FakeTimeProvider(Arrival));
        var keep = await PublishAsync(engine, "<bulk-high-fit-keep@example>");
        var drop = await PublishAsync(engine, "<bulk-high-fit-drop@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        reader.UsedBytes = 870_000_000;

        var result = await Coordinator(engine, reservesPercent: 1).RunOnceAsync(CancellationToken.None);

        Assert.Equal(nameof(BulkStoragePressureState.High), result.BulkPressureState);
        Assert.False(result.BulkRewriteSuppressed);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(ArticleStorageState.Present, State(engine, keep.ArtId));
        Assert.True(engine.TryRead(keep.ArtId, out _));
    }

    [Fact]
    public async Task Critical_withholds_a_rewrite_that_does_not_free_more_than_it_copies()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(1_000_000_000, 0);
        await using var engine = Open(dir, reader, new FakeTimeProvider(Arrival));
        var records = new ArticleRecord[4];
        for (var i = 0; i < records.Length; i++)
        {
            records[i] = await PublishAsync(engine, $"<bulk-critical-waste-{i}@example>");
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var before = Location(engine, records[0].ArtId);
        Assert.True(engine.TryEvict(records[0].ArtId));
        reader.UsedBytes = 920_000_000;

        var result = await Coordinator(engine, reservesPercent: 1).RunOnceAsync(CancellationToken.None);

        Assert.Equal(nameof(BulkStoragePressureState.Critical), result.BulkPressureState);
        Assert.Equal("ImmediateSpaceRecovery", result.BulkMaintenanceMode);
        Assert.True(result.BulkRewriteSuppressed);
        Assert.False(result.CompactionAttempted);
        Assert.Equal(before.SegmentId, Location(engine, records[1].ArtId).SegmentId);
        Assert.Equal(ArticleStorageState.Present, State(engine, records[1].ArtId));
    }

    [Fact]
    public async Task Critical_allows_a_rewrite_when_dead_bytes_exceed_live_bytes()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(1_000_000_000, 0);
        await using var engine = Open(dir, reader, new FakeTimeProvider(Arrival));
        var keep = await PublishAsync(engine, "<bulk-critical-keep@example>");
        var drop1 = await PublishAsync(engine, "<bulk-critical-drop1@example>");
        var drop2 = await PublishAsync(engine, "<bulk-critical-drop2@example>");
        var drop3 = await PublishAsync(engine, "<bulk-critical-drop3@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop1.ArtId));
        Assert.True(engine.TryEvict(drop2.ArtId));
        Assert.True(engine.TryEvict(drop3.ArtId));
        reader.UsedBytes = 920_000_000;

        var result = await Coordinator(engine, reservesPercent: 1).RunOnceAsync(CancellationToken.None);

        Assert.Equal(nameof(BulkStoragePressureState.Critical), result.BulkPressureState);
        Assert.False(result.BulkRewriteSuppressed);
        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        Assert.Equal(ArticleStorageState.Present, State(engine, keep.ArtId));
        Assert.True(engine.TryRead(keep.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(keep.ArtData.Span));
    }

    [Fact]
    public async Task Emergency_does_not_discard_acknowledged_ingress_or_rewrite()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(1_000_000_000, 0);
        await using var engine = Open(
            dir,
            reader,
            new FakeTimeProvider(Arrival),
            maximumUsageCapacity: 80);
        var keep = await PublishAsync(engine, "<bulk-emergency-keep@example>");
        var drop = await PublishAsync(engine, "<bulk-emergency-drop@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var before = Location(engine, keep.ArtId);
        Assert.True(engine.TryEvict(drop.ArtId));

        engine.SuspendBackgroundPersist = true;
        var journalOnly = await PublishAsync(engine, "<bulk-emergency-journal@example>", drain: false);
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        Assert.True(outstanding > 0);
        reader.UsedBytes = 960_000_000;
        Assert.True(engine.ObserveCapacityAdmissionPressure().IsUnderUsagePressure);

        var result = await Coordinator(engine, TimeSpan.FromHours(24)).RunOnceAsync(CancellationToken.None);

        Assert.Equal(nameof(BulkStoragePressureState.Emergency), result.BulkPressureState);
        Assert.Equal("EmergencyProtectIngress", result.BulkMaintenanceMode);
        Assert.True(result.BulkEmergencyAdmissionProtectionRequired);
        Assert.True(result.BulkRewriteSuppressed);
        Assert.False(result.CompactionAttempted);
        Assert.Equal(before, Location(engine, keep.ArtId));
        Assert.Equal(ArticleStorageState.Present, State(engine, keep.ArtId));
        Assert.True(engine.TryRead(journalOnly.ArtId, out var journalRead));
        Assert.True(journalRead.ArtData.Span.SequenceEqual(journalOnly.ArtData.Span));
        Assert.Equal(outstanding, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(StorageWritePressure.Normal, engine.GetWritePressure());
    }

    [Fact]
    public async Task MaxRetentionAge_still_expires_when_bulk_pressure_is_normal()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(1_000_000_000, 100_000_000);
        var time = new FakeTimeProvider(Arrival);
        await using var engine = Open(dir, reader, time);
        var record = await PublishAsync(engine, "<bulk-age@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var published));
        time.SetUtcNow(published.AcceptedUtc.AddHours(24));

        Assert.Equal(ArticleStorageState.Present, State(engine, record.ArtId));

        var result = await Coordinator(engine, TimeSpan.FromHours(24)).RunOnceAsync(CancellationToken.None);

        Assert.Equal(nameof(BulkStoragePressureState.Normal), result.BulkPressureState);
        Assert.False(result.BulkRewriteSuppressed);
        Assert.False(engine.TryRead(record.ArtId, out _));
        if (engine.Index.TryGet(record.ArtId, out var expired))
        {
            Assert.Equal(ArticleStorageState.Evicted, expired.State);
        }
    }

    private static StorageMaintenanceCoordinator Coordinator(
        FileArticleStorageEngine engine,
        TimeSpan? maxRetentionAge = null,
        int reservesPercent = 5)
    {
        var bulk = new BulkStoragePressureOptions
        {
            OperationalReservePercent = reservesPercent,
            RecoveryReservePercent = reservesPercent,
            RewriteReservePercent = reservesPercent == 5 ? 10 : reservesPercent,
        };
        return new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: 10),
            logger: NullLogger.Instance,
            maxRetentionAge: maxRetentionAge ?? TimeSpan.Zero,
            bulkPressure: bulk);
    }

    private static FileArticleStorageEngine Open(
        TempDir dir,
        MutableCapacityReader reader,
        TimeProvider time,
        int maximumUsageCapacity = 100) =>
        FileArticleStorageEngine.Open(
            dir.Options with
            {
                CapacityMaximumUtilization = 100,
                CapacityCompactionHeadroom = 0,
                CapacityMaximumUsageCapacity = maximumUsageCapacity,
                CapacityFreeCapacity = 1,
            },
            logger: NullLogger.Instance,
            timeProvider: time,
            capacityReader: reader);

    private static async Task<ArticleRecord> PublishAsync(
        FileArticleStorageEngine engine,
        string messageId,
        bool drain = true)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase26\r\n\r\nbody\r\n");
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(created.Record, CancellationToken.None)).Outcome);
        if (drain)
        {
            await engine.DrainPendingAsync(CancellationToken.None);
        }

        return created.Record;
    }

    private static ArticleStorageState State(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        return row.State;
    }

    private static StoredArticleLocation Location(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        return row.Location;
    }

    private sealed class MutableCapacityReader : IStorageCapacityReader
    {
        public MutableCapacityReader(long total, long used)
        {
            TotalBytes = total;
            UsedBytes = used;
        }

        public long TotalBytes { get; }

        public long UsedBytes { get; set; }

        public StorageCapacitySnapshot Read() =>
            new(TotalBytes, UsedBytes, Math.Max(0L, TotalBytes - UsedBytes));
    }

    private sealed class TempDir : IDisposable
    {
        private TempDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public ArticleStorageRuntimeOptions Options { get; }

        private string Root { get; }

        public static TempDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase26-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "control"));
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            return new TempDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: Path.Combine(root, "control"),
                    SegmentDir: Path.Combine(root, "cache"),
                    JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                    SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes,
                    MaxSegmentSealDelay: TimeSpan.FromHours(1)));
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
