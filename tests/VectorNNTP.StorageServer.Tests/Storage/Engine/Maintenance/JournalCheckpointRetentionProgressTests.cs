using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;
using Xunit.Abstractions;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Maintenance;

/// <summary>
/// Phase 32: journal checkpoint reservations versus pressure-expiration progress.
/// These tests use a synthetic capacity reader so the ceiling and the watermark can be set independently.
/// </summary>
public sealed class JournalCheckpointRetentionProgressTests
{
    private const long Total = 1_000_000;

    private static readonly DateTimeOffset Arrival = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    private readonly ITestOutputHelper _output;

    public JournalCheckpointRetentionProgressTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Disabled_checkpoint_leaves_reservations_and_expiration_makes_no_progress()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time, maximumUtilization: 100);
        var id = await PublishAsync(engine, "<p32-disabled@example>", bodyLines: 2200);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var reserved = engine.ProcessLocalJournalReservedBytes;
        var physical = engine.Journal.JournalPhysicalBytes;
        reader.UsedBytes = 900_000;
        time.Advance(Grace + TimeSpan.FromDays(1));

        var first = await Coordinator(engine, checkpointThreshold: 0).RunOnceAsync(CancellationToken.None);
        var second = await Coordinator(engine, checkpointThreshold: 0).RunOnceAsync(CancellationToken.None);

        Report("disabled", engine, first, second, reserved, physical);
        Assert.True(reserved > Total - reader.UsedBytes);
        Assert.Equal(0, first.Recovery!.Value.PressureArticlesExpired);
        Assert.Equal(0, second.Recovery!.Value.PressureArticlesExpired);
        Assert.Equal(reserved, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(physical, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(ArticleStorageState.Present, State(engine, id));
        Assert.True(engine.TryRead(id, out _));
    }

    [Fact]
    public async Task Positive_threshold_checkpoints_releasable_reservations_and_the_next_cycle_expires()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time, maximumUtilization: 100);
        var id = await PublishAsync(engine, "<p32-over-ceiling@example>", bodyLines: 2200);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var reserved = engine.ProcessLocalJournalReservedBytes;
        var physical = engine.Journal.JournalPhysicalBytes;
        reader.UsedBytes = 900_000;
        time.Advance(Grace + TimeSpan.FromDays(1));
        Assert.True(physical >= 32);

        var started = Stopwatch.StartNew();
        var first = await Coordinator(engine, checkpointThreshold: 32).RunOnceAsync(CancellationToken.None);
        started.Stop();
        Assert.True(reserved > Total - reader.UsedBytes);
        Assert.Equal(0, first.Recovery!.Value.PressureArticlesExpired);
        Assert.Equal(ArticleStorageState.Present, State(engine, id));
        Assert.True(engine.TryRead(id, out _));
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.True(engine.Journal.JournalPhysicalBytes < physical);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);

        var second = await Coordinator(engine, checkpointThreshold: 32).RunOnceAsync(CancellationToken.None);
        Report("over-ceiling-threshold-32", engine, first, second, reserved, physical);
        _output.WriteLine("first-cycle ms " + started.Elapsed.TotalMilliseconds.ToString("0.0"));
        Assert.Equal(1, second.Recovery!.Value.PressureArticlesExpired);
        Assert.False(engine.TryRead(id, out _));
    }

    [Fact]
    public async Task Large_threshold_does_not_checkpoint_and_expiration_makes_no_progress()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time, maximumUtilization: 100);
        var id = await PublishAsync(engine, "<p32-large-threshold@example>", bodyLines: 2200);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var reserved = engine.ProcessLocalJournalReservedBytes;
        var physical = engine.Journal.JournalPhysicalBytes;
        reader.UsedBytes = 900_000;
        time.Advance(Grace + TimeSpan.FromDays(1));

        var first = await Coordinator(engine, checkpointThreshold: physical + 1).RunOnceAsync(CancellationToken.None);
        var second = await Coordinator(engine, checkpointThreshold: physical + 1).RunOnceAsync(CancellationToken.None);

        Report("large-threshold", engine, first, second, reserved, physical);
        Assert.True(reserved > Total - reader.UsedBytes);
        Assert.Equal(0, first.Recovery!.Value.PressureArticlesExpired);
        Assert.Equal(0, second.Recovery!.Value.PressureArticlesExpired);
        Assert.Equal(reserved, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(physical, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(ArticleStorageState.Present, State(engine, id));
        Assert.True(engine.TryRead(id, out _));
    }

    [Fact]
    public async Task Checkpoint_under_the_ceiling_releases_journal_reservations_and_keeps_the_article()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time, maximumUtilization: 100);
        var id = await PublishAsync(engine, "<p32-under-ceiling@example>", bodyLines: 40);
        var reservedBefore = engine.ProcessLocalJournalReservedBytes;
        var physicalBefore = engine.Journal.JournalPhysicalBytes;
        var outstandingBefore = engine.Journal.OutstandingRecoverableBytes;
        reader.UsedBytes = 100_000;

        var started = Stopwatch.StartNew();
        var result = await Coordinator(engine, checkpointThreshold: 1).RunOnceAsync(CancellationToken.None);
        started.Stop();

        Report("under-ceiling", engine, result, result, reservedBefore, physicalBefore);
        _output.WriteLine("checkpoint-cycle ms " + started.Elapsed.TotalMilliseconds.ToString("0.0"));
        Assert.True(reservedBefore > 0);
        Assert.Equal(0, outstandingBefore);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.True(engine.Journal.JournalPhysicalBytes < physicalBefore);
        Assert.Equal(0, result.Recovery!.Value.PressureArticlesExpired);
        Assert.Equal(ArticleStorageState.Present, State(engine, id));
        Assert.True(engine.TryRead(id, out _));

        var options = dir.Options with
        {
            CapacityMaximumUtilization = 100,
            CapacityCompactionHeadroom = 0,
            CapacityMaximumUsageCapacity = 100,
            CapacityFreeCapacity = 1,
        };
        await engine.DisposeAsync();
        await using var reopened = FileArticleStorageEngine.Open(
            options,
            logger: NullLogger.Instance,
            timeProvider: time,
            capacityReader: reader);
        Assert.True(reopened.TryRead(id, out _));
        Assert.Equal(0, reopened.Journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task Shipped_utilization_ceiling_blocks_expiration_when_drive_used_is_already_in_bulk_pressure()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time, maximumUtilization: 70);
        var id = await PublishAsync(engine, "<p32-shipped-ceiling@example>", bodyLines: 4);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        await Coordinator(engine, checkpointThreshold: 1).RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        reader.UsedBytes = 820_000;
        time.Advance(Grace + TimeSpan.FromDays(1));

        var blocked = await Coordinator(engine, checkpointThreshold: 32L * 1024 * 1024).RunOnceAsync(CancellationToken.None);
        var again = await Coordinator(engine, checkpointThreshold: 32L * 1024 * 1024).RunOnceAsync(CancellationToken.None);

        Report("shipped-70-used-82", engine, blocked, again, 0, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(nameof(BulkStoragePressureState.Pressure), blocked.Recovery!.Value.PressureStateBefore);
        Assert.Equal(0, blocked.Recovery.Value.PressureArticlesExpired);
        Assert.Equal(0, again.Recovery!.Value.PressureArticlesExpired);
        Assert.Equal(ArticleStorageState.Present, State(engine, id));
        Assert.True(engine.TryRead(id, out _));
    }

    [Fact]
    public async Task Expiration_under_the_ceiling_still_reclaims_a_fully_dead_segment_once()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time, maximumUtilization: 100);
        var first = await PublishAsync(engine, "<p32-reclaim-a@example>", bodyLines: 4);
        var second = await PublishAsync(engine, "<p32-reclaim-b@example>", bodyLines: 4);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        await Coordinator(engine, checkpointThreshold: 1).RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.True(engine.TryRead(first, out _));
        time.Advance(Grace + TimeSpan.FromDays(1));
        reader.UsedBytes = 920_000;

        var cleared = await Coordinator(engine, checkpointThreshold: 1).RunOnceAsync(CancellationToken.None);
        var followUp = await Coordinator(engine, checkpointThreshold: 1).RunOnceAsync(CancellationToken.None);

        Report("reclaim", engine, cleared, followUp, 0, 0);
        Assert.Equal(2, cleared.Recovery!.Value.PressureArticlesExpired);
        Assert.True(cleared.Recovery.Value.FullyDeadBytesReclaimed > 0);
        Assert.Equal(0, followUp.Recovery!.Value.FullyDeadBytesReclaimed);
        Assert.False(engine.TryRead(first, out _));
        Assert.False(engine.TryRead(second, out _));
        Assert.False(engine.Index.TryGet(first, out _));
    }

    [Fact]
    public async Task Emergency_admission_stays_rejected_for_the_recovery_reserve()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 9_600_000);
        await using var engine = Open(dir, reader, time, maximumUtilization: 100);
        engine.SuspendBackgroundPersist = true;
        var accepted = await engine.AcceptAsync(Article("<p32-emergency@example>", bodyLines: 1), CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, accepted.Outcome);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, accepted.Reason);
    }

    private void Report(
        string name,
        FileArticleStorageEngine engine,
        StorageMaintenanceResult first,
        StorageMaintenanceResult second,
        long reservedBefore,
        long physicalBefore)
    {
        var recovery = first.Recovery!.Value;
        _output.WriteLine(
            name
            + " state " + recovery.PressureStateBefore
            + " expired " + recovery.PressureArticlesExpired
            + "/" + recovery.PressureArticlesEvaluated
            + " logical " + recovery.LogicalExpiredBytes
            + " physicalNet " + recovery.NetPhysicalRecoveryBytes
            + " fullyDead " + recovery.FullyDeadBytesReclaimed
            + " reserved " + reservedBefore + " -> " + engine.ProcessLocalJournalReservedBytes
            + " physical " + physicalBefore + " -> " + engine.Journal.JournalPhysicalBytes
            + " outstanding " + engine.Journal.OutstandingRecoverableBytes
            + " indexReserved " + engine.ProcessLocalIndexReservedBytes
            + " secondExpired " + second.Recovery!.Value.PressureArticlesExpired);
    }

    private static StorageMaintenanceCoordinator Coordinator(
        FileArticleStorageEngine engine,
        long checkpointThreshold) =>
        new(
            engine,
            new ArticleSegmentPolicy(new ArticleCompactionPolicyOptions()),
            journalCheckpointThresholdBytes: checkpointThreshold,
            logger: NullLogger.Instance,
            maxRetentionAge: TimeSpan.Zero,
            bulkPressure: new BulkStoragePressureOptions
            {
                MinimumRetentionAge = Grace,
            });

    private static FileArticleStorageEngine Open(
        TempDir dir,
        IStorageCapacityReader reader,
        TimeProvider time,
        int maximumUtilization) =>
        FileArticleStorageEngine.Open(
            dir.Options with
            {
                CapacityMaximumUtilization = maximumUtilization,
                CapacityCompactionHeadroom = maximumUtilization == 100 ? 0 : 10,
                CapacityMaximumUsageCapacity = maximumUtilization == 100 ? 100 : 50,
                CapacityFreeCapacity = maximumUtilization == 100 ? 1 : 5,
            },
            logger: NullLogger.Instance,
            timeProvider: time,
            capacityReader: reader);

    private static async Task<ArticleId> PublishAsync(FileArticleStorageEngine engine, string messageId, int bodyLines)
    {
        engine.SuspendBackgroundPersist = true;
        var accepted = await engine.AcceptAsync(Article(messageId, bodyLines), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        return accepted.ArtId;
    }

    private static ArticleRecord Article(string messageId, int bodyLines)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase32\r\n\r\n");
        for (var i = 0; i < bodyLines; i++)
        {
            _ = builder.Append(new string('x', 64)).Append("\r\n");
        }

        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private static ArticleStorageState State(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        return row.State;
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase32-" + Guid.NewGuid().ToString("N"));
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
