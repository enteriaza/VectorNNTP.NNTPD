using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Destination reservations stay until the destination segment is reclaimed, and checkpoint
/// reservations stay while the extra physical file still exists.
/// </summary>
public sealed class CapacityReservationLifetimeTests
{
    [Fact]
    public async Task Destination_reservation_blocks_stale_used_until_that_segment_is_reclaimed()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        var record = CreateRecord("<life-dest@seg.test>");
        var copyBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var source));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();

        var compact = await engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var moved));
        var destinationId = moved.Location.SegmentId;
        Assert.NotEqual(source.Location.SegmentId, destinationId);
        Assert.Equal(copyBytes, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(1, engine.ProcessLocalCompactionReservationCount);

        var retireSource = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retireSource.Outcome);
        var reclaimSource = await engine.ReclaimRetiredSegmentAsync(source.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimSource.Outcome);
        Assert.Equal(copyBytes, engine.ProcessLocalCompactionReservedBytes);

        var probe = CreateRecord("<life-dest-probe@seg.test>");
        var probeBytes = AcceptFootprint(probe);
        PinUsedAgainstMargin(capacity, engine, copyBytes, probeBytes);
        Assert.Equal(
            ArticleAcceptOutcome.RejectedCapacity,
            (await engine.AcceptAsync(probe, CancellationToken.None)).Outcome);
        Assert.Equal(copyBytes, engine.ProcessLocalCompactionReservedBytes);

        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        var compactDestination = await engine.CompactClosedSegmentAsync(destinationId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compactDestination.Outcome);
        Assert.Equal(copyBytes * 2, engine.ProcessLocalCompactionReservedBytes);
        var retireDestination = await engine.RetireCompactedSegmentAsync(
            compactDestination.CompactionId,
            CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retireDestination.Outcome);
        var reclaimDestination = await engine.ReclaimRetiredSegmentAsync(destinationId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimDestination.Outcome);
        Assert.Equal(copyBytes, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(1, engine.ProcessLocalCompactionReservationCount);

        PinUsedAgainstMargin(capacity, engine, marginBytes: 0, probeBytes);
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(probe, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Destination_retry_reuses_the_held_reservation()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        var record = CreateRecord("<life-retry@seg.test>");
        var copyBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var source));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(source.Location.SegmentId, out var info));
        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, source.Location.SegmentId, info.Generation),
                CancellationToken.None));

        engine.TestRelocationFaultPoint = FileArticleStorageEngine.RelocationFaultPoint.AfterAppendBeforeWritten;
        await Assert.ThrowsAsync<IOException>(() =>
            engine.RelocateArticleAsync(
                compactionId,
                1,
                source.Location.SegmentId,
                info.Generation,
                record.ArtId,
                CancellationToken.None));
        Assert.Equal(copyBytes, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(1, engine.ProcessLocalCompactionReservationCount);
        var appends = engine.PhysicalAppendCount;

        engine.TestRelocationFaultPoint = FileArticleStorageEngine.RelocationFaultPoint.None;
        var relocated = await engine.RelocateArticleAsync(
            compactionId,
            1,
            source.Location.SegmentId,
            info.Generation,
            record.ArtId,
            CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, relocated.Outcome);
        Assert.Equal(copyBytes, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(1, engine.ProcessLocalCompactionReservationCount);
        Assert.Equal(appends, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task Journal_temp_reservation_stays_when_deletion_fails_and_blocks_stale_used()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<life-jtmp@seg.test>"), CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        engine.Journal.TestFailCheckpointTempDelete = true;
        engine.Journal.CheckpointTestFault = point =>
        {
            if (point == FileArticleJournal.CheckpointFaultPoint.AfterTempFlushed)
            {
                throw new IOException("checkpoint temp delete failed");
            }
        };

        Assert.Throws<IOException>(() => engine.CheckpointTruncateCommitted());
        Assert.NotEmpty(Directory.GetFiles(dir.Options.ControlDir, ".article.journal.*.tmp"));
        var held = engine.ProcessLocalCheckpointReservedBytes;
        Assert.True(held > 0);

        var probe = CreateRecord("<life-jtmp-probe@seg.test>");
        PinUsedAgainstMargin(capacity, engine, held, AcceptFootprint(probe));
        Assert.Equal(
            ArticleAcceptOutcome.RejectedCapacity,
            (await engine.AcceptAsync(probe, CancellationToken.None)).Outcome);
        Assert.Equal(held, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public async Task Journal_temp_reservation_is_released_after_successful_install()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<life-jok@seg.test>"), CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
        Assert.Empty(Directory.GetFiles(dir.Options.ControlDir, ".article.journal.*.tmp"));
    }

    [Fact]
    public async Task Installed_snapshot_keeps_one_reservation_across_replacement()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<life-snap1@seg.test>"), CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);

        var first = engine.Index.WriteSnapshot();
        var firstBytes = ArticleIndexSnapshotCodec.EncodedLength((int)first.RecordCount);
        Assert.Equal(firstBytes, engine.ProcessLocalCheckpointReservedBytes);
        Assert.True(File.Exists(Path.Combine(dir.Options.ControlDir, FileArticleIndex.SnapshotFileName)));
        Assert.False(File.Exists(Path.Combine(dir.Options.ControlDir, FileArticleIndex.SnapshotTempFileName)));

        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<life-snap2@seg.test>"), CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        var second = engine.Index.WriteSnapshot();
        var secondBytes = ArticleIndexSnapshotCodec.EncodedLength((int)second.RecordCount);
        Assert.True(secondBytes > firstBytes);
        Assert.Equal(secondBytes, engine.ProcessLocalCheckpointReservedBytes);

        _ = engine.Index.Checkpoint();
        Assert.Equal(secondBytes, engine.ProcessLocalCheckpointReservedBytes);
        Assert.False(File.Exists(Path.Combine(dir.Options.ControlDir, FileArticleIndex.ReplacementTempFileName)));
    }

    [Fact]
    public async Task Snapshot_temp_reservation_stays_when_deletion_fails()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<life-stmp@seg.test>"), CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        engine.Index.TestFailCheckpointTempDelete = true;
        engine.Index.TestBeforeSnapshotInstall = () => throw new IOException("snapshot temp remains");

        Assert.Throws<IOException>(() => engine.Index.WriteSnapshot());
        Assert.True(File.Exists(Path.Combine(dir.Options.ControlDir, FileArticleIndex.SnapshotTempFileName)));
        Assert.Equal(
            ArticleIndexSnapshotCodec.EncodedLength(1),
            engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public async Task Replacement_temp_reservation_stays_when_install_fails()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<life-repl@seg.test>"), CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        engine.Index.TestFailCheckpointTempDelete = true;
        engine.Index.TestBeforeReplacementInstall = () => throw new IOException("replacement remains");

        Assert.Throws<IOException>(() => engine.Index.Checkpoint());
        var snapBytes = ArticleIndexSnapshotCodec.EncodedLength(1);
        var replPath = Path.Combine(dir.Options.ControlDir, FileArticleIndex.ReplacementTempFileName);
        Assert.True(File.Exists(replPath));
        Assert.True(File.Exists(Path.Combine(dir.Options.ControlDir, FileArticleIndex.SnapshotFileName)));
        Assert.Equal(snapBytes + new FileInfo(replPath).Length, engine.ProcessLocalCheckpointReservedBytes);
    }

    private static long AcceptFootprint(ArticleRecord record) =>
        SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize)
        + ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize)
        + ArticleIndexRecordCodec.RecordLength;

    private static void PinUsedAgainstMargin(
        MutableCapacityReader reader,
        FileArticleStorageEngine engine,
        long marginBytes,
        long admitBytes)
    {
        var observed = engine.ObserveCapacityAdmissionPressure();
        var reserved = observed.ArticleReservedBytes
            + observed.CompactionReservedBytes
            + observed.CheckpointReservedBytes
            + observed.JournalReservedBytes
            + observed.IndexReservedBytes
            + observed.CompactionJournalReservedBytes;
        reader.UsedBytes = observed.ArticleCeilingBytes - (reserved - marginBytes) - admitBytes;
        Assert.InRange(reader.UsedBytes, 0, reader.TotalBytes);
    }

    private static ArticleStorageRuntimeOptions WithCapacity(ArticleStorageRuntimeOptions options) =>
        options with
        {
            CapacityMaximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization,
            CapacityCompactionHeadroom = ArticleCapacityOptions.DefaultCompactionHeadroom,
        };

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: reservation-lifetime\r\n");
        _ = builder.Append("\r\nline1\r\nline2\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class MutableCapacityReader(long total, long used) : IStorageCapacityReader
    {
        public long TotalBytes { get; set; } = total;

        public long UsedBytes { get; set; } = used;

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

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-life-" + Guid.NewGuid().ToString("N"));
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

        private string Root { get; }
    }
}
