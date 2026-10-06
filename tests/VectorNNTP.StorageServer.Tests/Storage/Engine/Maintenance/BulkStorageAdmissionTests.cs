using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
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
/// New Accepts are rejected before the journal ACK when the cache-volume recovery reserve
/// would be consumed. An article already in the journal stays recoverable.
/// </summary>
public sealed class BulkStorageAdmissionTests
{
    private const long Total = 10_000_000;

    [Theory]
    [InlineData(1_000_000)]
    [InlineData(7_600_000)]
    [InlineData(8_200_000)]
    public async Task Healthy_and_pressure_states_accept(long used)
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(Total, used);
        await using var engine = Open(dir, reader);
        var article = Article("<bulk-admit-ok@example>", bodyBytes: 64);

        var result = await engine.AcceptAsync(article, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome);
        Assert.True(engine.TryRead(article.ArtId, out _));
    }

    [Fact]
    public async Task Pressure_accepts_an_article_that_would_cross_the_recovery_reserve()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(Total, 8_200_000);
        await using var engine = OpenSeparateControlVolume(dir, reader);
        var article = Article("<bulk-pressure-large@example>", bodyBytes: 1_400_000);
        var copy = SegmentRecordCodec.RecordLengthForArtSize(article.ArtSize);
        var reserve = Total * 5 / 100;
        Assert.True(reader.FreeBytes - copy < reserve);

        var result = await engine.AcceptAsync(article, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome);
    }

    [Fact]
    public async Task High_accepts_when_the_copy_leaves_the_recovery_reserve()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(Total, 8_800_000));
        var article = Article("<bulk-high-fit@example>", bodyBytes: 64);

        var result = await engine.AcceptAsync(article, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome);
        Assert.True(engine.TryRead(article.ArtId, out _));
    }

    [Fact]
    public async Task High_rejects_when_the_copy_would_consume_the_recovery_reserve()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(Total, 8_800_000);
        await using var engine = Open(dir, reader);
        var article = Article("<bulk-high-miss@example>", bodyBytes: 800_000);
        var copy = SegmentRecordCodec.RecordLengthForArtSize(article.ArtSize);
        Assert.True(reader.FreeBytes - copy < Total * 5 / 100);

        var result = await engine.AcceptAsync(article, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Outcome);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, result.Reason);
        Assert.False(engine.TryRead(article.ArtId, out _));
    }

    [Fact]
    public async Task Critical_accepts_when_protected_headroom_remains()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(Total, 9_200_000));
        var article = Article("<bulk-critical-fit@example>", bodyBytes: 64);
        var copy = SegmentRecordCodec.RecordLengthForArtSize(article.ArtSize);
        Assert.True(800_000 - copy >= Total * 5 / 100);

        var result = await engine.AcceptAsync(article, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome);
    }

    [Fact]
    public async Task Critical_rejects_when_protected_headroom_is_insufficient()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(Total, 9_200_000));
        var article = Article("<bulk-critical-miss@example>", bodyBytes: 400_000);
        var copy = SegmentRecordCodec.RecordLengthForArtSize(article.ArtSize);
        Assert.True(800_000 - copy < Total * 5 / 100);

        var result = await engine.AcceptAsync(article, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Outcome);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, result.Reason);
        Assert.False(engine.TryRead(article.ArtId, out _));
    }

    [Fact]
    public async Task Emergency_rejects_a_new_accept_and_keeps_an_acked_article()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(Total, 1_000_000);
        await using var engine = Open(dir, reader);
        var kept = Article("<bulk-emergency-kept@example>", bodyBytes: 32);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(kept, CancellationToken.None)).Outcome);
        reader.UsedBytes = 9_600_000;
        var blocked = Article("<bulk-emergency-new@example>", bodyBytes: 32);
        var attempts = 0;
        engine.UsagePressureRecovery = _ =>
        {
            attempts++;
            return Task.CompletedTask;
        };

        var result = await engine.AcceptAsync(blocked, CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Outcome);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, result.Reason);
        Assert.True(engine.TryRead(kept.ArtId, out _));
        Assert.False(engine.TryRead(blocked.ArtId, out _));
        Assert.NotEqual(ArticleAcceptOutcome.RejectedPressure, result.Outcome);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(20_000)]
    [InlineData(400_000)]
    public async Task Admission_uses_the_actual_article_size(int bodyBytes)
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(Total, 9_200_000));
        var article = Article($"<bulk-size-{bodyBytes}@example>", bodyBytes);
        var copy = SegmentRecordCodec.RecordLengthForArtSize(article.ArtSize);
        var fits = 800_000 - copy >= Total * 5 / 100;

        var result = await engine.AcceptAsync(article, CancellationToken.None);

        Assert.Equal(fits ? ArticleAcceptOutcome.Accepted : ArticleAcceptOutcome.RejectedCapacity, result.Outcome);
        if (!fits)
        {
            Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, result.Reason);
        }
    }

    [Fact]
    public async Task Recovery_that_frees_enough_space_lets_the_accept_proceed()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(Total, 9_600_000);
        await using var engine = Open(dir, reader);
        var article = Article("<bulk-recover-fit@example>", bodyBytes: 64);
        engine.UsagePressureRecovery = _ =>
        {
            reader.UsedBytes = 1_000_000;
            return Task.CompletedTask;
        };

        var result = await engine.AcceptAsync(article, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome);
        Assert.True(engine.TryRead(article.ArtId, out _));
    }

    [Fact]
    public async Task Admission_recovery_reclaims_a_fully_dead_segment_and_keeps_present_articles()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader);
        var deadA = Article("<bulk-dead-a@example>", bodyBytes: 64);
        var deadB = Article("<bulk-dead-b@example>", bodyBytes: 64);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(deadA, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(deadB, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(deadA.ArtId));
        Assert.True(engine.TryEvict(deadB.ArtId));
        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.Catalogue.TryGet(Location(engine, deadA.ArtId).SegmentId, out var deadInfo));
        Assert.True(SegmentLifecycle.IsReclaimable(deadInfo));
        var deadSegment = deadInfo.SegmentId;

        var kept = Article("<bulk-present-keep@example>", bodyBytes: 64);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(kept, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(ArticleStorageState.Present, State(engine, kept.ArtId));
        reader.UsedBytes = 9_600_000;
        _ = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: 10),
            logger: NullLogger.Instance,
            bulkPressure: new BulkStoragePressureOptions());
        var blocked = Article("<bulk-after-dead@example>", bodyBytes: 64);

        var result = await engine.AcceptAsync(blocked, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Outcome);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, result.Reason);
        Assert.False(engine.Catalogue.TryGet(deadSegment, out _));
        Assert.Equal(ArticleStorageState.Present, State(engine, kept.ArtId));
        Assert.True(engine.TryRead(kept.ArtId, out _));
        Assert.False(engine.TryRead(blocked.ArtId, out _));
    }

    [Fact]
    public async Task Recovery_that_does_nothing_still_rejects()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(Total, 9_600_000);
        await using var engine = Open(dir, reader);
        engine.UsagePressureRecovery = _ => Task.CompletedTask;
        var article = Article("<bulk-recover-none@example>", bodyBytes: 32);

        var result = await engine.AcceptAsync(article, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Outcome);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, result.Reason);
        Assert.False(engine.TryRead(article.ArtId, out _));
    }

    [Fact]
    public async Task Recovery_that_throws_rejects_without_acking()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(Total, 1_000_000);
        await using var engine = Open(dir, reader);
        var kept = Article("<bulk-recover-throw-kept@example>", bodyBytes: 32);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(kept, CancellationToken.None)).Outcome);
        reader.UsedBytes = 9_600_000;
        engine.UsagePressureRecovery = _ => throw new InvalidOperationException("recovery failed");
        var blocked = Article("<bulk-recover-throw-new@example>", bodyBytes: 32);

        var result = await engine.AcceptAsync(blocked, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Outcome);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryFailedRejectionReason, result.Reason);
        Assert.True(engine.TryRead(kept.ArtId, out _));
        Assert.False(engine.TryRead(blocked.ArtId, out _));
    }

    [Fact]
    public async Task Recovery_cancellation_does_not_ack()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(Total, 1_000_000);
        await using var engine = Open(dir, reader);
        var kept = Article("<bulk-recover-cancel-kept@example>", bodyBytes: 32);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(kept, CancellationToken.None)).Outcome);
        reader.UsedBytes = 9_600_000;
        engine.UsagePressureRecovery = _ => throw new OperationCanceledException();
        var blocked = Article("<bulk-recover-cancel-new@example>", bodyBytes: 32);

        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.AcceptAsync(blocked, CancellationToken.None));

        Assert.True(engine.TryRead(kept.ArtId, out _));
        Assert.False(engine.TryRead(blocked.ArtId, out _));
    }

    [Fact]
    public async Task Unmeasured_capacity_rejects_the_new_accept()
    {
        using var dir = TempDir.Create();
        var reader = new FlakyCapacityReader(Total, 1_000_000);
        await using var engine = Open(dir, reader);
        var kept = Article("<bulk-unmeasured-kept@example>", bodyBytes: 32);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(kept, CancellationToken.None)).Outcome);
        reader.ThrowOnRead = true;
        var blocked = Article("<bulk-unmeasured-new@example>", bodyBytes: 32);

        var result = await engine.AcceptAsync(blocked, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Outcome);
        Assert.Equal(BulkStoragePressurePolicy.UnmeasuredRejectionReason, result.Reason);
        Assert.True(engine.TryRead(kept.ArtId, out _));
        Assert.False(engine.TryRead(blocked.ArtId, out _));
    }

    [Fact]
    public async Task A_stale_pre_recovery_snapshot_does_not_decide_admission()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(Total, 9_600_000);
        await using var engine = Open(dir, reader);
        engine.UsagePressureRecovery = _ =>
        {
            reader.UsedBytes = 1_000_000;
            return Task.CompletedTask;
        };
        var article = Article("<bulk-stale-snapshot@example>", bodyBytes: 64);

        var result = await engine.AcceptAsync(article, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome);
    }

    [Fact]
    public async Task Journal_hard_pressure_stays_distinct_from_bulk_pressure()
    {
        using var dir = TempDir.Create(journalHardLimitBytes: 64);
        var reader = new MutableCapacityReader(Total, 1_000_000);
        await using var engine = Open(dir, reader);
        var first = Article("<bulk-journal-a@example>", bodyBytes: 32);
        var second = Article("<bulk-journal-b@example>", bodyBytes: 32);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);

        var pressured = await engine.AcceptAsync(second, CancellationToken.None);

        Assert.Equal(StorageWritePressure.Critical, engine.GetWritePressure());
        Assert.Equal(ArticleAcceptOutcome.RejectedPressure, pressured.Outcome);
        Assert.Equal("journal-pressure", pressured.Reason);
        Assert.True(engine.TryRead(first.ArtId, out _));
    }

    [Fact]
    public async Task Bulk_emergency_and_journal_pressure_report_the_bulk_reason()
    {
        using var dir = TempDir.Create(journalHardLimitBytes: 64);
        var reader = new MutableCapacityReader(Total, 1_000_000);
        await using var engine = Open(dir, reader);
        var first = Article("<bulk-both-a@example>", bodyBytes: 32);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        reader.UsedBytes = 9_600_000;
        var second = Article("<bulk-both-b@example>", bodyBytes: 32);

        var result = await engine.AcceptAsync(second, CancellationToken.None);

        Assert.Equal(StorageWritePressure.Critical, engine.GetWritePressure());
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Outcome);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, result.Reason);
        Assert.True(engine.TryRead(first.ArtId, out _));
        Assert.False(engine.TryRead(second.ArtId, out _));
    }

    [Fact]
    public async Task Repeating_an_acked_article_during_emergency_is_a_duplicate()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(Total, 1_000_000);
        await using var engine = Open(dir, reader);
        engine.SuspendBackgroundPersist = true;
        var article = Article("<bulk-duplicate@example>", bodyBytes: 32);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(article, CancellationToken.None)).Outcome);
        reader.UsedBytes = 9_600_000;

        var again = await engine.AcceptAsync(article, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Duplicate, again.Outcome);
        Assert.True(engine.TryRead(article.ArtId, out _));
    }

    [Fact]
    public async Task Restart_before_bulk_publication_keeps_the_acked_article_under_emergency()
    {
        using var dir = TempDir.Create();
        var reader = new MutableCapacityReader(Total, 1_000_000);
        var engine = Open(dir, reader);
        engine.SuspendBackgroundPersist = true;
        var kept = Article("<bulk-restart@example>", bodyBytes: 32);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(kept, CancellationToken.None)).Outcome);
        await engine.DisposeAsync();

        reader.UsedBytes = 9_600_000;
        await using var restarted = Open(dir, reader);
        await restarted.RecoverAsync(CancellationToken.None);
        var blocked = Article("<bulk-restart-new@example>", bodyBytes: 32);
        var rejected = await restarted.AcceptAsync(blocked, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, rejected.Outcome);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, rejected.Reason);
        Assert.True(restarted.TryRead(kept.ArtId, out var read));
        Assert.Equal(kept.ArtSize, read.ArtData.Length);
        Assert.False(restarted.TryRead(blocked.ArtId, out _));
    }

    [Fact]
    public async Task Concurrent_accepts_cannot_oversubscribe_the_recovery_reserve()
    {
        using var dir = TempDir.Create();
        var left = Article("<bulk-race-a@example>", bodyBytes: 8_000);
        var right = Article("<bulk-race-b@example>", bodyBytes: 8_000);
        var copy = SegmentRecordCodec.RecordLengthForArtSize(left.ArtSize);
        Assert.Equal(copy, SegmentRecordCodec.RecordLengthForArtSize(right.ArtSize));
        var reserve = Total * 5 / 100;
        var free = reserve + copy + 1;
        var reader = new MutableCapacityReader(Total, Total - free);
        await using var engine = Open(dir, reader);
        engine.SuspendBackgroundPersist = true;

        var results = await Task.WhenAll(
            engine.AcceptAsync(left, CancellationToken.None),
            engine.AcceptAsync(right, CancellationToken.None));

        Assert.Equal(1, results.Count(static result => result.Outcome == ArticleAcceptOutcome.Accepted));
        Assert.Equal(1, results.Count(static result =>
            result.Outcome == ArticleAcceptOutcome.RejectedCapacity
            && result.Reason == BulkStoragePressurePolicy.RecoveryReserveRejectionReason));
        Assert.Equal(1, (engine.TryRead(left.ArtId, out _) ? 1 : 0) + (engine.TryRead(right.ArtId, out _) ? 1 : 0));
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

    /// <summary>
    /// Segment and control ledgers are separate, so a large segment copy is not also
    /// charged to the journal volume. Production machines that keep those directories
    /// on one drive still apply the existing utilization ceiling to both.
    /// </summary>
    private static FileArticleStorageEngine OpenSeparateControlVolume(TempDir dir, MutableCapacityReader segment) =>
        FileArticleStorageEngine.Open(
            dir.Options with
            {
                CapacityMaximumUtilization = 100,
                CapacityCompactionHeadroom = 0,
                CapacityMaximumUsageCapacity = 100,
                CapacityFreeCapacity = 1,
            },
            volumeProbe: new SplitVolumeProbe(),
            capacityReader: segment,
            controlCapacityReader: new MutableCapacityReader(Total * 10, 0),
            logger: NullLogger.Instance,
            timeProvider: TimeProvider.System);

    private static FileArticleStorageEngine Open(TempDir dir, IStorageCapacityReader reader) =>
        FileArticleStorageEngine.Open(
            dir.Options with
            {
                CapacityMaximumUtilization = 100,
                CapacityCompactionHeadroom = 0,
                CapacityMaximumUsageCapacity = 100,
                CapacityFreeCapacity = 1,
            },
            logger: NullLogger.Instance,
            timeProvider: TimeProvider.System,
            capacityReader: reader);

    private static ArticleRecord Article(string messageId, int bodyBytes)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase27\r\n\r\n");
        var remaining = bodyBytes;
        while (remaining > 0)
        {
            var take = Math.Min(900, remaining);
            _ = builder.Append('x', take).Append("\r\n");
            remaining -= take;
        }

        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class SplitVolumeProbe : IStorageVolumeProbe
    {
        public bool TryResolve(string directoryPath, out StorageVolumeIdentity identity)
        {
            identity = new StorageVolumeIdentity(directoryPath);
            return true;
        }
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

        public long FreeBytes => Math.Max(0L, TotalBytes - UsedBytes);

        public StorageCapacitySnapshot Read() => new(TotalBytes, UsedBytes, FreeBytes);
    }

    private sealed class FlakyCapacityReader : IStorageCapacityReader
    {
        private readonly long _total;
        private readonly long _used;

        public FlakyCapacityReader(long total, long used)
        {
            _total = total;
            _used = used;
        }

        public bool ThrowOnRead { get; set; }

        public StorageCapacitySnapshot Read()
        {
            if (ThrowOnRead)
            {
                throw new IOException("capacity unavailable");
            }

            return new StorageCapacitySnapshot(_total, _used, Math.Max(0L, _total - _used));
        }
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

        public static TempDir Create(long journalHardLimitBytes = 0)
        {
            var hard = journalHardLimitBytes > 0
                ? journalHardLimitBytes
                : ArticleStorageOptions.DefaultJournalHardLimitBytes;
            var soft = journalHardLimitBytes > 0
                ? Math.Min(ArticleStorageOptions.DefaultJournalSoftLimitBytes, hard / 2)
                : ArticleStorageOptions.DefaultJournalSoftLimitBytes;
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase27-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "control"));
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            return new TempDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: Path.Combine(root, "control"),
                    SegmentDir: Path.Combine(root, "cache"),
                    JournalSoftLimitBytes: soft,
                    JournalHardLimitBytes: hard,
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
