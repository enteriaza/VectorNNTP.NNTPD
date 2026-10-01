using System.Text;
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
/// CompactionRetired stays in the journal checkpoint while the source is still Closed.
/// </summary>
public sealed class RetirementCheckpointRetentionTests
{
    [Fact]
    public async Task RetiredJournal_ClosedSource_CheckpointRetainsRecordMemoryAndReservation()
    {
        using var dir = TempStorageDir.Create();
        var prepared = await PrepareRetiredJournalClosedSourceAsync(dir);
        var sourceId = prepared.SourceId;
        var compactionId = prepared.CompactionId;
        var reserved = prepared.ReservedBytes;
        using (var engine = Open(dir))
        {
            Assert.Equal(reserved, engine.ProcessLocalCompactionJournalReservedBytes);
            Assert.True(engine.ProcessLocalCompactionJournalFrameCount > 0);
            Assert.True(engine.CheckpointTruncateCommitted() > 0);
            Assert.Equal(reserved, engine.ProcessLocalCompactionJournalReservedBytes);
            Assert.True(engine.Journal.TryGetCompaction(compactionId, out var snap));
            Assert.NotNull(snap.Retired);
            AssertState(engine, sourceId, SegmentState.Closed);
            Assert.True(File.Exists(ClosedPath(dir, sourceId)));
        }

        using var restarted = Open(dir);
        Assert.True(restarted.Journal.TryGetCompaction(compactionId, out var replayed));
        Assert.NotNull(replayed.Retired);
        Assert.Equal(reserved, restarted.ProcessLocalCompactionJournalReservedBytes);
        AssertState(restarted, sourceId, SegmentState.Closed);
    }

    [Fact]
    public async Task RetiredJournal_RetiredSource_CheckpointOmitsRecordAndReleasesReservation()
    {
        using var dir = TempStorageDir.Create();
        var prepared = await PrepareRetiredJournalClosedSourceAsync(dir);
        var sourceId = prepared.SourceId;
        var compactionId = prepared.CompactionId;
        var reserved = prepared.ReservedBytes;
        using (var engine = Open(dir))
        {
            Assert.True(reserved > 0);
            engine.CompleteUnreferencedExtentAccounting();
            var retired = await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None);
            Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retired.Outcome);
            Assert.True(engine.CheckpointTruncateCommitted() > 0);
            Assert.False(engine.Journal.TryGetCompaction(compactionId, out _));
            Assert.Equal(0, engine.ProcessLocalCompactionJournalReservedBytes);
            AssertState(engine, sourceId, SegmentState.Retired);
        }

        using var restarted = Open(dir);
        Assert.False(restarted.Journal.TryGetCompaction(compactionId, out _));
        Assert.Equal(0, restarted.ProcessLocalCompactionJournalReservedBytes);
    }

    [Fact]
    public async Task RetiredJournal_MissingSource_CheckpointOmitsRecordAndReleasesReservation()
    {
        using var dir = TempStorageDir.Create();
        var prepared = await PrepareRetiredJournalClosedSourceAsync(dir);
        var sourceId = prepared.SourceId;
        var compactionId = prepared.CompactionId;
        var reserved = prepared.ReservedBytes;
        using var engine = Open(dir);
        Assert.True(reserved > 0);
        engine.CompleteUnreferencedExtentAccounting();
        Assert.Equal(
            ArticleSegmentRetirementOutcome.Retired,
            (await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None)).Outcome);
        Assert.True(engine.Segments.TryReclaimRetired(sourceId, out var reason), reason);
        Assert.False(engine.Catalogue.TryGet(sourceId, out _));
        Assert.False(File.Exists(RetiredPath(dir, sourceId)));
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out _));
        Assert.Equal(reserved, engine.ProcessLocalCompactionJournalReservedBytes);

        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.False(engine.Journal.TryGetCompaction(compactionId, out _));
        Assert.Equal(0, engine.ProcessLocalCompactionJournalReservedBytes);
    }

    [Fact]
    public async Task CrashBeforePhysicalRetirement_RestartKeepsTheRetirementObligation()
    {
        using var dir = TempStorageDir.Create();
        var prepared = await PrepareRetiredJournalClosedSourceAsync(dir);
        var sourceId = prepared.SourceId;
        var compactionId = prepared.CompactionId;
        using (var engine = Open(dir))
        {
            Assert.True(engine.CheckpointTruncateCommitted() > 0);
            Assert.True(engine.Journal.TryGetCompaction(compactionId, out var kept));
            Assert.NotNull(kept.Retired);
        }

        using var restarted = Open(dir);
        AssertState(restarted, sourceId, SegmentState.Closed);
        Assert.True(File.Exists(ClosedPath(dir, sourceId)));
        Assert.True(restarted.Journal.TryGetCompaction(compactionId, out var replayed));
        Assert.NotNull(replayed.Retired);

        var coordinator = new StorageMaintenanceCoordinator(
            restarted,
            new ArticleSegmentPolicy(minimumDeadBytes: long.MaxValue, minimumDeadRatio: 100),
            journalCheckpointThresholdBytes: 1);
        var cycle = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.NotEqual(StorageMaintenanceOutcome.Reclaimed, cycle.Outcome);
        AssertState(restarted, sourceId, SegmentState.Closed);
        Assert.True(File.Exists(ClosedPath(dir, sourceId)));

        await restarted.RecoverAsync(CancellationToken.None);
        AssertState(restarted, sourceId, SegmentState.Retired);
        Assert.True(File.Exists(RetiredPath(dir, sourceId)));
        Assert.False(File.Exists(ClosedPath(dir, sourceId)));
    }

    [Fact]
    public async Task NormalRetirement_CheckpointOmitsThenReclamationDeletesTheFile()
    {
        using var dir = TempStorageDir.Create();
        using var engine = Open(dir);
        var sourceId = await AcceptCloseCompactAsync(engine, CreateRecord("<normal-retire@example.test>"));
        var compactionId = engine.Journal.EnumerateCompactions()
            .Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value).Begin.CompactionId;
        Assert.Equal(
            ArticleSegmentRetirementOutcome.Retired,
            (await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None)).Outcome);
        AssertState(engine, sourceId, SegmentState.Retired);
        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.False(engine.Journal.TryGetCompaction(compactionId, out _));

        var coordinator = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: long.MaxValue, minimumDeadRatio: 100),
            journalCheckpointThresholdBytes: 1);
        var cycle = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Reclaimed, cycle.Outcome);
        Assert.False(engine.Catalogue.TryGet(sourceId, out _));
        Assert.False(File.Exists(RetiredPath(dir, sourceId)));
    }

    [Fact]
    public async Task CheckpointWhileClosed_DoesNotLetReclamationTreatTheSourceAsRetired()
    {
        using var dir = TempStorageDir.Create();
        var prepared = await PrepareRetiredJournalClosedSourceAsync(dir);
        var sourceId = prepared.SourceId;
        var compactionId = prepared.CompactionId;
        using var engine = Open(dir);
        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out var kept));
        Assert.NotNull(kept.Retired);

        Assert.False(engine.Segments.TryReclaimRetired(sourceId, out _));
        var coordinator = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: long.MaxValue, minimumDeadRatio: 100),
            journalCheckpointThresholdBytes: 1);
        var cycle = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.NotEqual(StorageMaintenanceOutcome.Reclaimed, cycle.Outcome);
        AssertState(engine, sourceId, SegmentState.Closed);
        Assert.True(File.Exists(ClosedPath(dir, sourceId)));
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out _));
    }

    private static async Task<ClosedSourceSetup> PrepareRetiredJournalClosedSourceAsync(TempStorageDir dir)
    {
        using var engine = Open(dir);
        var sourceId = await AcceptCloseCompactAsync(engine, CreateRecord("<closed-retired@example.test>"));
        var compactionId = engine.Journal.EnumerateCompactions()
            .Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value).Begin.CompactionId;
        engine.TestRetirementFaultPoint = FileArticleStorageEngine.RetirementFaultPoint.AfterCompactionRetiredBeforeCatalogue;
        await Assert.ThrowsAsync<IOException>(() => engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None));
        AssertState(engine, sourceId, SegmentState.Closed);
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out var snap));
        Assert.NotNull(snap.Retired);
        var reservedBytes = engine.ProcessLocalCompactionJournalReservedBytes;
        Assert.True(reservedBytes > 0);
        return new ClosedSourceSetup(sourceId, compactionId, reservedBytes);
    }

    private readonly record struct ClosedSourceSetup(SegmentId SourceId, ulong CompactionId, long ReservedBytes);

    private static void AssertState(FileArticleStorageEngine engine, SegmentId sourceId, SegmentState expected)
    {
        Assert.True(engine.Catalogue.TryGet(sourceId, out var info));
        Assert.Equal(expected, info.State);
    }

    private static FileArticleStorageEngine Open(TempStorageDir dir) =>
        FileArticleStorageEngine.Open(
            dir.Options,
            capacityReader: new MutableCapacityReader(total: 10_000_000, used: 0));

    private static async Task<SegmentId> AcceptCloseCompactAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        var compact = await engine.CompactClosedSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        return meta.Location.SegmentId;
    }

    private static string ClosedPath(TempStorageDir dir, SegmentId sourceId) =>
        Path.Combine(dir.Options.SegmentDir, SegmentFileNames.Format(sourceId, SegmentFileKind.Closed));

    private static string RetiredPath(TempStorageDir dir, SegmentId sourceId) =>
        Path.Combine(dir.Options.SegmentDir, SegmentFileNames.Format(sourceId, SegmentFileKind.Retired));

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: retire-checkpoint\r\n");
        _ = builder.Append("\r\n");
        _ = builder.Append("line1\r\nline2\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class MutableCapacityReader(long total, long used) : IStorageCapacityReader
    {
        public long TotalBytes { get; } = total;

        public long UsedBytes { get; } = used;

        public StorageCapacitySnapshot Read() =>
            new(TotalBytes, UsedBytes, Math.Max(0L, TotalBytes - UsedBytes));
    }

    private sealed class TempStorageDir : IDisposable
    {
        private TempStorageDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string Root { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-retire-ckpt-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            return new TempStorageDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: control,
                    SegmentDir: cache,
                    JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                    SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes));
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
