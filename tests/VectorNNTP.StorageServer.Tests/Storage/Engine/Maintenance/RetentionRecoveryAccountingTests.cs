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
/// Recovery accounting reports logical expiration and physical release as different totals.
/// Logical bytes are not treated as free space.
/// </summary>
public sealed class RetentionRecoveryAccountingTests
{
    private const long Total = 10_000_000;

    private static readonly DateTimeOffset Arrival = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Logical_expiration_is_not_physical_recovery()
    {
        var before = At(9_200_000);
        var cycle = Result(StorageMaintenanceOutcome.NoWork) with
        {
            Reclaimed = false,
        };
        var account = RetentionRecoveryAccounting.Compose(
            in before,
            before,
            Age(expired: 4, bytes: 4_000),
            Pressure(expired: 0, bytes: 0),
            in cycle,
            recoveryTargetPercent: 75,
            consecutiveUnimprovedCycles: 0,
            durationMilliseconds: 1);

        Assert.Equal(4_000, account.LogicalExpiredBytes);
        Assert.Equal(4_000, account.AgeExpiredBytes);
        Assert.Equal(0, account.NetPhysicalRecoveryBytes);
        Assert.Equal(0, account.FullyDeadBytesReclaimed);
        Assert.False(account.PressureImproved);
        Assert.NotEqual(account.LogicalExpiredBytes, account.FreeBytesAfter);
    }

    [Fact]
    public void A_committed_rewrite_does_not_count_relocated_bytes_twice()
    {
        var before = At(8_200_000);
        var cycle = Result(StorageMaintenanceOutcome.CompactedAndReclaimed) with
        {
            CompactionAttempted = true,
            CompactionCommitted = true,
            ReclamationAttempted = true,
            Reclaimed = true,
            RelocatedArticleCount = 1,
            SourceSizeBytes = 1_000,
            SourceLiveBytes = 200,
            SourceDeadBytes = 800,
            ReclaimedSegmentSizeBytes = 1_000,
            PhysicalFileDeleted = true,
        };
        var account = RetentionRecoveryAccounting.Compose(
            in before,
            before,
            default,
            default,
            in cycle,
            recoveryTargetPercent: 75,
            consecutiveUnimprovedCycles: 0,
            durationMilliseconds: 1);

        Assert.Equal(200, account.CompactionBytesRelocated);
        Assert.Equal(200, account.CompactionDestinationBytesWritten);
        Assert.Equal(1_000, account.CompactionSourceBytesReclaimed);
        Assert.Equal(800, account.NetPhysicalRecoveryBytes);
        Assert.NotEqual(
            account.CompactionSourceBytesReclaimed + account.CompactionBytesRelocated,
            account.NetPhysicalRecoveryBytes);
        Assert.Equal(0, account.FullyDeadBytesReclaimed);
        Assert.Equal(0, account.RetiredBytesReclaimed);
    }

    [Fact]
    public void An_interrupted_compaction_and_an_idempotent_reclaim_release_nothing()
    {
        var before = At(9_600_000);
        var interrupted = Result(StorageMaintenanceOutcome.Incomplete) with
        {
            CompactionAttempted = true,
            CompactionCommitted = false,
            RelocatedArticleCount = 3,
            SourceLiveBytes = 200,
            SourceDeadBytes = 800,
        };
        var abandoned = RetentionRecoveryAccounting.Compose(
            in before,
            before,
            default,
            default,
            in interrupted,
            recoveryTargetPercent: 75,
            consecutiveUnimprovedCycles: 0,
            durationMilliseconds: 1);
        Assert.True(abandoned.CompactionInterrupted);
        Assert.Equal(0, abandoned.CompactionDestinationBytesWritten);
        Assert.Equal(0, abandoned.CompactionSourceBytesReclaimed);
        Assert.Equal(0, abandoned.NetPhysicalRecoveryBytes);

        var alreadyGone = Result(StorageMaintenanceOutcome.Reclaimed) with
        {
            ReclamationAttempted = true,
            Reclaimed = true,
            ReclaimedSegmentSizeBytes = 500,
            PhysicalFileDeleted = false,
            FullyDeadFileReclaim = true,
        };
        var idempotent = RetentionRecoveryAccounting.Compose(
            in before,
            before,
            default,
            default,
            in alreadyGone,
            recoveryTargetPercent: 75,
            consecutiveUnimprovedCycles: abandoned.ConsecutiveUnimprovedCycles,
            durationMilliseconds: 1);
        Assert.Equal(0, idempotent.FullyDeadBytesReclaimed);
        Assert.Equal(0, idempotent.NetPhysicalRecoveryBytes);
        Assert.Equal(1, abandoned.ConsecutiveUnimprovedCycles);
        Assert.Equal(2, idempotent.ConsecutiveUnimprovedCycles);
    }

    [Fact]
    public void Unimproved_pressure_resets_when_free_bytes_increase()
    {
        var high = At(8_700_000);
        var eased = At(7_000_000);
        var stuck = RetentionRecoveryAccounting.Compose(
            in high,
            high,
            Age(expired: 1, bytes: 100),
            default,
            Result(StorageMaintenanceOutcome.NoWork),
            recoveryTargetPercent: 75,
            consecutiveUnimprovedCycles: 3,
            durationMilliseconds: 1);
        Assert.Equal(4, stuck.ConsecutiveUnimprovedCycles);
        Assert.False(stuck.PressureImproved);
        Assert.False(stuck.RecoveryTargetReached);

        var recovered = RetentionRecoveryAccounting.Compose(
            in high,
            eased,
            default,
            default,
            Result(StorageMaintenanceOutcome.Reclaimed),
            recoveryTargetPercent: 75,
            consecutiveUnimprovedCycles: stuck.ConsecutiveUnimprovedCycles,
            durationMilliseconds: 1);
        Assert.Equal(0, recovered.ConsecutiveUnimprovedCycles);
        Assert.True(recovered.PressureImproved);
        Assert.True(recovered.RecoveryTargetReached);
        Assert.True(recovered.FreeBytesAfter > recovered.FreeBytesBefore);
    }

    [Fact]
    public async Task Expiring_one_article_leaves_the_segment_and_reports_no_physical_recovery()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 1_000_000);
        await using var engine = Open(dir, reader, time);
        var record = await PublishAsync(engine, "<recovery-logical@example>");
        var segment = Location(engine, record.ArtId).SegmentId;
        var used = reader.UsedBytes;
        time.Advance(TimeSpan.FromHours(1));

        var result = await Coordinator(engine, reader, TimeSpan.FromHours(1)).RunOnceAsync(CancellationToken.None);

        Assert.NotNull(result.Recovery);
        Assert.True(result.Recovery.Value.AgeArticlesExpired >= 1);
        Assert.True(result.Recovery.Value.LogicalExpiredBytes > 0);
        Assert.Equal(0, result.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.Equal(0, result.Recovery.Value.FullyDeadBytesReclaimed);
        Assert.Equal(0, result.Recovery.Value.CompactionSourceBytesReclaimed);
        Assert.Equal(used, reader.UsedBytes);
        Assert.Equal(result.Recovery.Value.FreeBytesBefore, result.Recovery.Value.FreeBytesAfter);
        Assert.False(result.Recovery.Value.PressureImproved);
        Assert.Equal(ArticleStorageState.Evicted, State(engine, record.ArtId));
        Assert.True(engine.Catalogue.TryGet(segment, out var info));
        Assert.True(info.SizeBytes > 0);
        Assert.True(File.Exists(ActivePath(engine, segment)));
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task A_later_cycle_reports_physical_recovery_only_after_the_file_is_deleted()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 1_000_000);
        await using var engine = Open(dir, reader, time);
        var record = await PublishAsync(engine, "<recovery-later@example>");
        var segment = Location(engine, record.ArtId).SegmentId;
        time.Advance(TimeSpan.FromHours(1));
        var logical = await Coordinator(engine, reader, TimeSpan.FromHours(1)).RunOnceAsync(CancellationToken.None);
        Assert.True(logical.Recovery!.Value.LogicalExpiredBytes > 0);
        Assert.Equal(0, logical.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.True(engine.Catalogue.TryGet(segment, out var beforeDelete));

        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var physical = await Coordinator(engine, reader).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, physical.Recovery!.Value.LogicalExpiredBytes);
        Assert.Equal(1, physical.Recovery.Value.FullyDeadSegmentsReclaimed);
        Assert.Equal(beforeDelete.SizeBytes, physical.Recovery.Value.FullyDeadBytesReclaimed);
        Assert.Equal(beforeDelete.SizeBytes, physical.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.Equal(0, physical.Recovery.Value.CompactionDestinationBytesWritten);
        Assert.False(engine.Catalogue.TryGet(segment, out _));
        Assert.False(File.Exists(ActivePath(engine, segment)));
        Assert.False(File.Exists(ClosedPath(engine, segment)));
    }

    [Fact]
    public async Task A_partial_segment_keeps_the_live_article_and_releases_nothing()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 8_700_000);
        await using var engine = Open(dir, reader, time);
        var old = await PublishAsync(engine, "<recovery-partial-old@example>");
        time.Advance(TimeSpan.FromHours(2));
        var live = await PublishAsync(engine, "<recovery-partial-live@example>");
        var segment = Location(engine, live.ArtId).SegmentId;

        var result = await Coordinator(engine, reader, TimeSpan.FromHours(1)).RunOnceAsync(CancellationToken.None);

        Assert.True(result.Recovery!.Value.LogicalExpiredBytes > 0);
        Assert.Equal(0, result.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.Equal(ArticleStorageState.Evicted, State(engine, old.ArtId));
        Assert.Equal(ArticleStorageState.Present, State(engine, live.ArtId));
        Assert.True(engine.TryRead(live.ArtId, out _));
        Assert.False(engine.TryRead(old.ArtId, out _));
        Assert.True(engine.Catalogue.TryGet(segment, out var info));
        Assert.True(info.LiveBytes > 0);
        Assert.True(info.DeadBytes > 0);
        Assert.True(File.Exists(ActivePath(engine, segment)));
        Assert.Equal(8_700_000, reader.UsedBytes);
    }

    [Fact]
    public async Task Rewrite_reclaims_the_source_without_counting_the_copy_twice()
    {
        using var dir = TempDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(Total, 1_000_000), TimeProvider.System);
        var keep = await PublishAsync(engine, "<recovery-keep@example>");
        var drop = await PublishAsync(engine, "<recovery-drop@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        var source = Location(engine, keep.ArtId).SegmentId;
        Assert.True(engine.Catalogue.TryGet(source, out var before));

        var result = await new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: 10),
            logger: NullLogger.Instance).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.CompactedAndReclaimed, result.Outcome);
        var recovery = result.Recovery!.Value;
        Assert.Equal(0, recovery.LogicalExpiredBytes);
        Assert.Equal(before.LiveBytes, recovery.CompactionDestinationBytesWritten);
        Assert.Equal(before.LiveBytes, recovery.CompactionBytesRelocated);
        Assert.Equal(before.SizeBytes, recovery.CompactionSourceBytesReclaimed);
        Assert.Equal(before.SizeBytes - before.LiveBytes, recovery.NetPhysicalRecoveryBytes);
        Assert.Equal(0, recovery.FullyDeadBytesReclaimed);
        Assert.True(engine.TryRead(keep.ArtId, out _));
        Assert.False(engine.Catalogue.TryGet(source, out _));
    }

    [Fact]
    public async Task Reclamation_that_frees_snapshot_bytes_improves_pressure()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time);
        var first = await PublishAsync(engine, "<recovery-pressure-a@example>");
        var second = await PublishAsync(engine, "<recovery-pressure-b@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var segment = Location(engine, first.ArtId).SegmentId;
        Assert.True(engine.Catalogue.TryGet(segment, out var info));
        time.Advance(TimeSpan.FromDays(7));
        reader.UsedBytes = 9_200_000;
        var coordinator = Coordinator(engine, reader);
        coordinator.TestHookBeforeFullyDeadReclaim = _ => reader.UsedBytes = 7_000_000;

        var result = await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(nameof(BulkStoragePressureState.Critical), result.Recovery!.Value.PressureStateBefore);
        Assert.Equal(nameof(BulkStoragePressureState.Normal), result.Recovery.Value.PressureStateAfter);
        Assert.True(result.Recovery.Value.LogicalExpiredBytes > 0);
        Assert.Equal(info.SizeBytes, result.Recovery.Value.FullyDeadBytesReclaimed);
        Assert.Equal(info.SizeBytes, result.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.True(result.Recovery.Value.PressureImproved);
        Assert.True(result.Recovery.Value.RecoveryTargetReached);
        Assert.True(result.Recovery.Value.FreeBytesAfter > result.Recovery.Value.FreeBytesBefore);
        Assert.False(engine.Catalogue.TryGet(segment, out _));
        Assert.False(engine.TryRead(first.ArtId, out _));
        Assert.False(engine.TryRead(second.ArtId, out _));
    }

    [Fact]
    public async Task Logical_expiration_does_not_change_admission_free_space()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time);
        var old = await PublishAsync(engine, "<recovery-admit-old@example>");
        time.Advance(TimeSpan.FromDays(7));
        var recent = await PublishAsync(engine, "<recovery-admit-recent@example>");
        reader.UsedBytes = 9_600_000;

        var result = await Coordinator(engine, reader).RunOnceAsync(CancellationToken.None);

        Assert.True(result.Recovery!.Value.PressureExpiredBytes > 0);
        Assert.Equal(0, result.Recovery.Value.NetPhysicalRecoveryBytes);
        Assert.False(result.Recovery.Value.PressureImproved);
        Assert.Equal(9_600_000, reader.UsedBytes);
        Assert.Equal(ArticleStorageState.Evicted, State(engine, old.ArtId));
        Assert.Equal(ArticleStorageState.Present, State(engine, recent.ArtId));
        var denied = await engine.AcceptAsync(Article("<recovery-admit-new@example>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, denied.Outcome);
    }

    private static RetentionExpirationResult Age(int expired, long bytes) =>
        new(1, expired, expired, 0, 0, 128, 0, false, false, bytes);

    private static PressureExpirationResult Pressure(int expired, long bytes) =>
        new(expired, expired, 0, 0, 0, expired, expired, bytes, 0, 128, 0, false, BulkStoragePressureState.Normal);

    private static StorageMaintenanceResult Result(StorageMaintenanceOutcome outcome) =>
        new(outcome, new SegmentId(1), 0, false, false, false, false, false, false);

    private static BulkStoragePressureEvaluation At(long used) =>
        new BulkStoragePressurePolicy().Evaluate(Total, used, Total - used);

    private static StorageMaintenanceCoordinator Coordinator(
        FileArticleStorageEngine engine,
        MutableCapacityReader reader,
        TimeSpan? maxRetentionAge = null)
    {
        _ = reader;
        return new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: 10),
            logger: NullLogger.Instance,
            maxRetentionAge: maxRetentionAge ?? TimeSpan.Zero,
            bulkPressure: new BulkStoragePressureOptions
            {
                MinimumRetentionAge = TimeSpan.FromDays(7),
            });
    }

    private static FileArticleStorageEngine Open(TempDir dir, IStorageCapacityReader reader, TimeProvider time) =>
        FileArticleStorageEngine.Open(
            dir.Options with
            {
                CapacityMaximumUtilization = 100,
                CapacityCompactionHeadroom = 0,
                CapacityMaximumUsageCapacity = 100,
                CapacityFreeCapacity = 1,
            },
            logger: NullLogger.Instance,
            timeProvider: time,
            capacityReader: reader);

    private static async Task<ArticleRecord> PublishAsync(FileArticleStorageEngine engine, string messageId)
    {
        var record = Article(messageId);
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        return record;
    }

    private static ArticleRecord Article(string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase29\r\n\r\nbody\r\n");
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

    private static StoredArticleLocation Location(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        return row.Location;
    }

    private static string ActivePath(FileArticleStorageEngine engine, SegmentId segmentId) =>
        Path.Combine(engine.Segments.RootPath, SegmentFileNames.Format(segmentId, SegmentFileKind.Active));

    private static string ClosedPath(FileArticleStorageEngine engine, SegmentId segmentId) =>
        Path.Combine(engine.Segments.RootPath, SegmentFileNames.Format(segmentId, SegmentFileKind.Closed));

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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase29-" + Guid.NewGuid().ToString("N"));
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
                    MaxSegmentSealDelay: TimeSpan.Zero));
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
