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
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Admission counts only segment copies and compaction destinations whose durable flush has not
/// returned. Written bindings stay until reclaim and contribute zero.
/// </summary>
public sealed class UnflushedSegmentReservationAdmissionTests
{
    [Fact]
    public void Unwritten_article_copy_contributes_until_it_is_noted_written()
    {
        var ledger = new ProcessLocalCapacityLedger();
        const long copyBytes = 2_200;
        const long total = 10_000;
        const long used = 4_890;
        const long requested = 100;
        const int ceiling = 70;

        ledger.TentativeAddArticle(copyBytes);
        Assert.Equal(copyBytes, ledger.ArticleReservedBytes);
        Assert.False(ledger.WouldFit(used, total, requested, ceiling));
        Assert.False(StaticWouldFit(ledger, used, total, requested, ceiling));

        ledger.RollbackUnboundArticle(copyBytes);
        Assert.Equal(0, ledger.ArticleReservedBytes);
        Assert.True(ledger.WouldFit(used, total, requested, ceiling));

        ledger.TentativeAddArticle(copyBytes);
        ledger.BindArticleSequence(1, copyBytes);
        Assert.Equal(copyBytes, ledger.ArticleReservedBytes);
        Assert.Equal((1, 0), ledger.GetArticleCopyCounts(1));
        Assert.False(ledger.WouldFit(used, total, requested, ceiling));

        ledger.NoteSegmentCopyWritten(1, new SegmentId(1));
        Assert.Equal(0, ledger.ArticleReservedBytes);
        Assert.Equal((1, 1), ledger.GetArticleCopyCounts(1));
        Assert.True(ledger.HoldsArticle(1));
        Assert.True(ledger.TryGetSoleWrittenSegment(1, out var written));
        Assert.Equal(new SegmentId(1), written);
        Assert.Equal(copyBytes, ledger.WrittenArticleBytesOnSegment(new SegmentId(1)));
        Assert.True(ledger.WouldFit(used, total, requested, ceiling));
        Assert.True(StaticWouldFit(ledger, used, total, requested, ceiling));
        Assert.False(ProcessLocalCapacityLedger.WouldFit(
            used,
            copyBytes,
            ledger.CompactionReservedBytes,
            total,
            requested,
            ceiling));
    }

    [Fact]
    public void Reclaim_removes_written_tracking_without_changing_admission()
    {
        var ledger = new ProcessLocalCapacityLedger();
        const long copyBytes = 2_200;
        ledger.TentativeAddArticle(copyBytes);
        ledger.BindArticleSequence(1, copyBytes);
        ledger.NoteSegmentCopyWritten(1, new SegmentId(4));
        Assert.Equal(0, ledger.ArticleReservedBytes);

        Assert.Equal(0, ledger.ReleaseWrittenArticleCopiesOnSegment(new SegmentId(4)));
        Assert.Equal(0, ledger.ArticleReservedBytes);
        Assert.False(ledger.HoldsArticle(1));
        Assert.Equal((0, 0), ledger.GetArticleCopyCounts(1));
        Assert.Equal(0, ledger.WrittenArticleBytesOnSegment(new SegmentId(4)));
        Assert.Equal(0, ledger.SegmentCopyCount);
    }

    [Fact]
    public void Second_physical_copy_contributes_only_while_it_is_unwritten()
    {
        var ledger = new ProcessLocalCapacityLedger();
        const long copyBytes = 2_200;
        const long total = 10_000;
        const long used = 4_890;
        const long requested = 100;
        ledger.TentativeAddArticle(copyBytes);
        ledger.BindArticleSequence(7, copyBytes);
        ledger.NoteSegmentCopyWritten(7, new SegmentId(1));
        Assert.Equal(0, ledger.ArticleReservedBytes);
        Assert.True(ledger.WouldFit(used, total, requested, 70));

        ledger.AddSegmentCopy(7, copyBytes);
        Assert.Equal(copyBytes, ledger.ArticleReservedBytes);
        Assert.Equal((2, 1), ledger.GetArticleCopyCounts(7));
        Assert.Equal(copyBytes, ledger.WrittenArticleBytesOnSegment(new SegmentId(1)));
        Assert.False(ledger.WouldFit(used, total, requested, 70));

        ledger.NoteSegmentCopyWritten(7, new SegmentId(2));
        Assert.Equal(0, ledger.ArticleReservedBytes);
        Assert.Equal((2, 2), ledger.GetArticleCopyCounts(7));
        Assert.Equal(copyBytes, ledger.WrittenArticleBytesOnSegment(new SegmentId(2)));
        Assert.True(ledger.WouldFit(used, total, requested, 70));
    }

    [Fact]
    public void Compaction_destination_contributes_only_before_it_is_bound()
    {
        var ledger = new ProcessLocalCapacityLedger();
        const long destinationBytes = 2_200;
        const long total = 10_000;
        const long used = 4_890;
        const long requested = 100;
        ledger.ReserveCompaction(3, 1, destinationBytes);
        Assert.Equal(destinationBytes, ledger.CompactionReservedBytes);
        Assert.Equal(1, ledger.CompactionReservationCount);
        Assert.False(ledger.TryGetCompactionDestination(3, 1, out _));
        Assert.False(ledger.WouldFit(used, total, requested, 70));
        Assert.False(StaticWouldFit(ledger, used, total, requested, 70));

        ledger.BindCompactionDestination(
            3,
            1,
            new StoredArticleLocation(new SegmentId(9), Offset: 0, Length: 64));
        Assert.Equal(0, ledger.CompactionReservedBytes);
        Assert.Equal(1, ledger.CompactionReservationCount);
        Assert.True(ledger.TryGetCompactionDestination(3, 1, out var location));
        Assert.Equal(new SegmentId(9), location.SegmentId);
        Assert.True(ledger.WouldFit(used, total, requested, 70));
        Assert.True(StaticWouldFit(ledger, used, total, requested, 70));

        Assert.Equal(1, ledger.ReleaseCompactionDestinationsOnSegment(new SegmentId(9)));
        Assert.Equal(0, ledger.CompactionReservedBytes);
        Assert.Equal(0, ledger.CompactionReservationCount);
        Assert.False(ledger.TryGetCompactionDestination(3, 1, out _));
    }

    [Fact]
    public void Pressure_snapshot_uses_the_unwritten_counters()
    {
        var ledger = new ProcessLocalCapacityLedger();
        const long copyBytes = 2_200;
        const long total = 10_000;
        const long used = 4_890;
        const int maximumUtilization = 70;
        ledger.TentativeAddArticle(copyBytes);
        ledger.BindArticleSequence(1, copyBytes);
        ledger.ReserveCompaction(1, 1, copyBytes);

        var unflushed = Snapshot(ledger, used, total, maximumUtilization);
        Assert.Equal(copyBytes, unflushed.ArticleReservedBytes);
        Assert.Equal(copyBytes, unflushed.CompactionReservedBytes);
        Assert.True(unflushed.IsUnderAdmissionPressure);
        Assert.True(unflushed.AdmissionRecoveryTargetBytes > 0);
        Assert.Equal(
            ProcessLocalCapacityLedger.ComputeAdmissionRecoveryTargetBytes(
                used,
                ledger.ArticleReservedBytes,
                ledger.CompactionReservedBytes,
                total,
                maximumUtilization,
                unflushed.MinimumAdmissionRequiredBytes),
            unflushed.AdmissionRecoveryTargetBytes);
        Assert.False(ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(ClosedLive(copyBytes), unflushed));

        ledger.NoteSegmentCopyWritten(1, new SegmentId(1));
        ledger.BindCompactionDestination(
            1,
            1,
            new StoredArticleLocation(new SegmentId(2), Offset: 0, Length: 64));
        var flushed = Snapshot(ledger, used, total, maximumUtilization);
        Assert.Equal(0, flushed.ArticleReservedBytes);
        Assert.Equal(0, flushed.CompactionReservedBytes);
        Assert.False(flushed.IsUnderAdmissionPressure);
        Assert.Equal(0, flushed.AdmissionRecoveryTargetBytes);
        Assert.True(ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(ClosedLive(500), flushed));
        Assert.True(ledger.HoldsArticle(1));
        Assert.Equal(1, ledger.CompactionReservationCount);
    }

    [Fact]
    public async Task Written_live_bytes_in_used_do_not_reject_a_fitting_admission()
    {
        using var dir = TempStorageDir.Create();
        const long total = 100_000;
        const int maximumUtilization = 70;
        var capacity = new MutableCapacityReader(total, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            dir.Options with
            {
                CapacityMaximumUtilization = maximumUtilization,
                CapacityCompactionHeadroom = 10,
            },
            capacityReader: capacity);

        var first = CreateRecord("<unflushed-live@seg.test>");
        var physical = SegmentRecordCodec.RecordLengthForArtSize(first.ArtSize);
        var accepted = await engine.AcceptAsync(first, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(first.ArtId, out var meta));
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal((1, 1), engine.ProcessLocalArticleCopyCounts(accepted.Sequence));
        Assert.Equal(physical, engine.ProcessLocalWrittenArticleBytesOnSegment(meta.Location.SegmentId));

        var second = CreateRecord("<unflushed-next@seg.test>");
        var secondFootprint = SegmentRecordCodec.RecordLengthForArtSize(second.ArtSize)
            + ArticleJournalFrameCodec.SequenceReservationBytes(second.ArtSize)
            + ArticleIndexRecordCodec.RecordLength;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, maximumUtilization);
        var otherReserved = engine.ProcessLocalReservedBytes;
        var used = ceiling - otherReserved - secondFootprint;
        Assert.InRange(used, 1, total);
        capacity.UsedBytes = used;

        var pressure = engine.ObserveCapacityAdmissionPressure();
        Assert.Equal(0, pressure.ArticleReservedBytes);
        Assert.Equal(0, pressure.AdmissionRecoveryTargetBytes);
        Assert.False(ProcessLocalCapacityLedger.WouldFit(
            used,
            physical,
            engine.ProcessLocalCompactionReservedBytes,
            total,
            secondFootprint,
            maximumUtilization,
            pressure.CheckpointReservedBytes,
            pressure.JournalReservedBytes,
            pressure.IndexReservedBytes,
            pressure.CompactionJournalReservedBytes));

        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(second, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Bound_compaction_destination_leaves_admission_and_keeps_tracking()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            dir.Options with
            {
                CapacityMaximumUtilization = 70,
                CapacityCompactionHeadroom = 10,
            },
            capacityReader: capacity);
        var record = CreateRecord("<unflushed-dest@seg.test>");
        var physical = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var source));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();

        long reservedBeforeFlush = -1;
        engine.TestHookAfterCompactionCapacityReserved = () =>
            reservedBeforeFlush = engine.ProcessLocalCompactionReservedBytes;

        var compact = await engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.Equal(physical, reservedBeforeFlush);
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(1, engine.ProcessLocalCompactionReservationCount);
        Assert.True(engine.Index.TryGet(record.ArtId, out var moved));
        Assert.NotEqual(source.Location.SegmentId, moved.Location.SegmentId);
    }

    private static bool StaticWouldFit(
        ProcessLocalCapacityLedger ledger,
        long used,
        long total,
        long requested,
        int ceiling) =>
        ProcessLocalCapacityLedger.WouldFit(
            used,
            ledger.ArticleReservedBytes,
            ledger.CompactionReservedBytes,
            total,
            requested,
            ceiling,
            ledger.CheckpointReservedBytes,
            ledger.JournalReservedBytes,
            ledger.IndexReservedBytes,
            ledger.CompactionJournalReservedBytes);

    private static CapacityAdmissionPressureSnapshot Snapshot(
        ProcessLocalCapacityLedger ledger,
        long used,
        long total,
        int maximumUtilization) =>
        CapacityAdmissionPressureSnapshot.FromCapacityState(
            new StorageCapacitySnapshot(total, used, total - used),
            ledger.ArticleReservedBytes,
            ledger.CompactionReservedBytes,
            maximumUtilization,
            compactionHeadroom: 10);

    private static SegmentInfo ClosedLive(long liveBytes) =>
        new(
            new SegmentId(1),
            SegmentState.Closed,
            Generation: 1,
            SizeBytes: liveBytes,
            LiveBytes: liveBytes,
            DeadBytes: 0,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            ClosedUtc: DateTimeOffset.UnixEpoch,
            ExtentAccountingComplete: true);

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: unflushed-reservation\r\n");
        _ = builder.Append("\r\nline1\r\nline2\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class MutableCapacityReader(long total, long used) : IStorageCapacityReader
    {
        public long TotalBytes { get; } = total;

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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-unflushed-" + Guid.NewGuid().ToString("N"));
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
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private string Root { get; }
    }
}
