using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Written article segment-copy reservations stay held after Flush and are released
/// only when the segment that holds those copies is physically reclaimed.
/// </summary>
public sealed class ArticleSegmentCopyReservationReclamationTests
{
    [Fact]
    public async Task Written_article_reservation_binds_to_the_destination_segment()
    {
        using var dir = TempStorageDir.Create();
        var reader = new SegmentDirectoryCapacityReader(dir.Options.SegmentDir, total: 10_000_000);
        await using var engine = Open(dir, reader);
        var record = CreateRecord("<bind-seg@seg.test>");
        var physical = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        engine.SuspendBackgroundPersist = true;

        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Equal(physical, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal((1, 0), engine.ProcessLocalArticleCopyCounts(accepted.Sequence));
        Assert.False(engine.TryGetSoleWrittenArticleSegment(accepted.Sequence, out _));
        Assert.Equal(0, reader.Read().UsedBytes);

        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(physical, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal((1, 1), engine.ProcessLocalArticleCopyCounts(accepted.Sequence));
        Assert.True(engine.TryGetSoleWrittenArticleSegment(accepted.Sequence, out var bound));
        Assert.Equal(meta.Location.SegmentId, bound);
        Assert.Equal(physical, engine.ProcessLocalWrittenArticleBytesOnSegment(bound));
        Assert.Equal(physical, reader.Read().UsedBytes);
    }

    [Fact]
    public async Task Written_reservation_stays_held_while_the_segment_is_active_or_closed()
    {
        using var dir = TempStorageDir.Create();
        var reader = new SegmentDirectoryCapacityReader(dir.Options.SegmentDir, total: 10_000_000);
        await using var engine = Open(dir, reader);
        var record = CreateRecord("<held-seg@seg.test>");
        var physical = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var accepted = await AcceptDurableAsync(engine, record);

        Assert.Equal(physical, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal((1, 1), engine.ProcessLocalArticleCopyCounts(accepted.Sequence));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var segmentId = meta.Location.SegmentId;

        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.Equal(physical, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(physical, engine.ProcessLocalWrittenArticleBytesOnSegment(segmentId));
        Assert.Equal(physical, reader.Read().UsedBytes);
        Assert.True(engine.TryGetSoleWrittenArticleSegment(accepted.Sequence, out var still));
        Assert.Equal(segmentId, still);
    }

    [Fact]
    public async Task Reclaim_releases_the_written_reservation_after_the_file_is_deleted()
    {
        using var dir = TempStorageDir.Create();
        var reader = new SegmentDirectoryCapacityReader(dir.Options.SegmentDir, total: 10_000_000);
        await using var engine = Open(dir, reader);
        var record = CreateRecord("<reclaim-one@seg.test>");
        var physical = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var accepted = await AcceptDurableAsync(engine, record);
        var segmentId = await RetireClosedSegmentAsync(engine, record.ArtId);

        Assert.Equal(physical, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(physical, engine.ProcessLocalWrittenArticleBytesOnSegment(segmentId));
        Assert.Equal(physical, reader.Read().UsedBytes);
        Assert.True(File.Exists(RetiredPath(dir, segmentId)));

        var reclaimed = await engine.ReclaimRetiredSegmentAsync(segmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimed.Outcome);
        Assert.False(File.Exists(RetiredPath(dir, segmentId)));
        Assert.Equal(0, reader.Read().UsedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalWrittenArticleBytesOnSegment(segmentId));
        Assert.Equal((0, 0), engine.ProcessLocalArticleCopyCounts(accepted.Sequence));
        Assert.False(engine.TryGetSoleWrittenArticleSegment(accepted.Sequence, out _));
    }

    [Fact]
    public async Task Reclaim_releases_every_article_reservation_on_that_segment()
    {
        using var dir = TempStorageDir.Create();
        var reader = new SegmentDirectoryCapacityReader(dir.Options.SegmentDir, total: 80_000_000);
        await using var engine = Open(dir, reader);
        var first = CreateRecord("<multi-a@seg.test>", BodyOfMebibytes(1));
        var second = CreateRecord("<multi-b@seg.test>", BodyOfMebibytes(2));
        var third = CreateRecord("<multi-c@seg.test>", BodyOfMebibytes(3));
        var lengths = new[]
        {
            SegmentRecordCodec.RecordLengthForArtSize(first.ArtSize),
            SegmentRecordCodec.RecordLengthForArtSize(second.ArtSize),
            SegmentRecordCodec.RecordLengthForArtSize(third.ArtSize),
        };
        Assert.True(lengths[0] < lengths[1] && lengths[1] < lengths[2]);
        var total = lengths[0] + lengths[1] + lengths[2];

        _ = await AcceptDurableAsync(engine, first);
        _ = await AcceptDurableAsync(engine, second);
        _ = await AcceptDurableAsync(engine, third);
        Assert.Equal(total, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(total, reader.Read().UsedBytes);

        var segmentId = await RetireClosedSegmentAsync(engine, first.ArtId, second.ArtId, third.ArtId);
        Assert.Equal(total, engine.ProcessLocalWrittenArticleBytesOnSegment(segmentId));
        Assert.Equal(total, engine.ProcessLocalArticleReservedBytes);

        var reclaimed = await engine.ReclaimRetiredSegmentAsync(segmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimed.Outcome);
        Assert.False(File.Exists(RetiredPath(dir, segmentId)));
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalWrittenArticleBytesOnSegment(segmentId));
        Assert.Equal(0, reader.Read().UsedBytes);
    }

    [Fact]
    public async Task Reclaiming_one_segment_leaves_reservations_on_the_other()
    {
        using var dir = TempStorageDir.Create();
        var reader = new SegmentDirectoryCapacityReader(dir.Options.SegmentDir, total: 10_000_000);
        await using var engine = Open(dir, reader);
        var left = CreateRecord("<two-a@seg.test>", "left-body\r\n");
        var right = CreateRecord("<two-b@seg.test>", "right-body-longer\r\n");
        var leftBytes = SegmentRecordCodec.RecordLengthForArtSize(left.ArtSize);
        var rightBytes = SegmentRecordCodec.RecordLengthForArtSize(right.ArtSize);
        var leftAccepted = await AcceptDurableAsync(engine, left);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var rightAccepted = await AcceptDurableAsync(engine, right);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        Assert.True(engine.Index.TryGet(left.ArtId, out var leftMeta));
        Assert.True(engine.Index.TryGet(right.ArtId, out var rightMeta));
        var leftId = leftMeta.Location.SegmentId;
        var rightId = rightMeta.Location.SegmentId;
        Assert.NotEqual(leftId, rightId);
        Assert.Equal(leftBytes + rightBytes, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(leftBytes, engine.ProcessLocalWrittenArticleBytesOnSegment(leftId));
        Assert.Equal(rightBytes, engine.ProcessLocalWrittenArticleBytesOnSegment(rightId));

        await RetireClosedSegmentAsync(engine, left.ArtId);
        await RetireClosedSegmentAsync(engine, right.ArtId);
        Assert.Equal(leftBytes + rightBytes, reader.Read().UsedBytes);

        var reclaimLeft = await engine.ReclaimRetiredSegmentAsync(leftId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimLeft.Outcome);
        Assert.Equal(rightBytes, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalWrittenArticleBytesOnSegment(leftId));
        Assert.Equal(rightBytes, engine.ProcessLocalWrittenArticleBytesOnSegment(rightId));
        Assert.Equal((0, 0), engine.ProcessLocalArticleCopyCounts(leftAccepted.Sequence));
        Assert.Equal((1, 1), engine.ProcessLocalArticleCopyCounts(rightAccepted.Sequence));
        Assert.Equal(rightBytes, reader.Read().UsedBytes);

        var reclaimRight = await engine.ReclaimRetiredSegmentAsync(rightId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimRight.Outcome);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalWrittenArticleBytesOnSegment(rightId));
        Assert.Equal(0, reader.Read().UsedBytes);
    }

    [Fact]
    public async Task Failed_delete_keeps_the_reservation_until_a_later_reclaim()
    {
        using var dir = TempStorageDir.Create();
        var reader = new SegmentDirectoryCapacityReader(dir.Options.SegmentDir, total: 10_000_000);
        await using var engine = Open(dir, reader);
        var record = CreateRecord("<reclaim-fail@seg.test>");
        var physical = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var accepted = await AcceptDurableAsync(engine, record);
        var segmentId = await RetireClosedSegmentAsync(engine, record.ArtId);
        engine.TestReclamationFaultPoint = FileArticleStorageEngine.ReclamationFaultPoint.BeforeDelete;

        await Assert.ThrowsAsync<IOException>(() =>
            engine.ReclaimRetiredSegmentAsync(segmentId, CancellationToken.None));
        Assert.True(File.Exists(RetiredPath(dir, segmentId)));
        Assert.Equal(physical, reader.Read().UsedBytes);
        Assert.Equal(physical, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(physical, engine.ProcessLocalWrittenArticleBytesOnSegment(segmentId));
        Assert.Equal((1, 1), engine.ProcessLocalArticleCopyCounts(accepted.Sequence));

        engine.TestReclamationFaultPoint = FileArticleStorageEngine.ReclamationFaultPoint.None;
        var reclaimed = await engine.ReclaimRetiredSegmentAsync(segmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimed.Outcome);
        Assert.False(File.Exists(RetiredPath(dir, segmentId)));
        Assert.Equal(0, reader.Read().UsedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
    }

    [Fact]
    public async Task Second_reclaim_does_not_release_the_reservation_again()
    {
        using var dir = TempStorageDir.Create();
        var reader = new SegmentDirectoryCapacityReader(dir.Options.SegmentDir, total: 10_000_000);
        await using var engine = Open(dir, reader);
        var record = CreateRecord("<reclaim-twice@seg.test>");
        var segmentId = await RetireClosedSegmentAsync(
            engine,
            (await AcceptDurableAsync(engine, record)).ArtId);

        var first = await engine.ReclaimRetiredSegmentAsync(segmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, first.Outcome);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, reader.Read().UsedBytes);

        var second = await engine.ReclaimRetiredSegmentAsync(segmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed, second.Outcome);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalWrittenArticleBytesOnSegment(segmentId));
        Assert.Equal(0, reader.Read().UsedBytes);
    }

    [Fact]
    public async Task Ambiguous_durable_tail_keeps_one_unbound_reservation_until_ownership_is_known()
    {
        using var dir = TempStorageDir.Create();
        var reader = new SegmentDirectoryCapacityReader(dir.Options.SegmentDir, total: 10_000_000);
        await using var engine = Open(dir, reader);
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        var record = CreateRecord("<amb-bind@seg.test>");
        var physical = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var scheduled = engine.PersistRetryScheduledCount;
        engine.Segments.TestAfterWriteBeforeFlush = static (_, _, _) => throw new IOException("first-flush");
        engine.Segments.TestBeforeDurableFlush = static () => throw new IOException("second-flush");

        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        await WaitUntilAsync(() => engine.PersistRetryScheduledCount > scheduled);
        Assert.Equal(physical, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        Assert.Equal((1, 0), engine.ProcessLocalArticleCopyCounts(accepted.Sequence));
        Assert.False(engine.TryGetSoleWrittenArticleSegment(accepted.Sequence, out _));

        engine.Segments.TestBeforeDurableFlush = null;
        engine.Segments.TestAfterWriteBeforeFlush = null;
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(physical, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        Assert.Equal((1, 1), engine.ProcessLocalArticleCopyCounts(accepted.Sequence));
        Assert.True(engine.TryGetSoleWrittenArticleSegment(accepted.Sequence, out var bound));
        Assert.Equal(meta.Location.SegmentId, bound);
        Assert.Equal(physical, reader.Read().UsedBytes);
    }

    [Fact]
    public async Task Clean_failure_before_durable_bytes_releases_the_unwritten_reservation()
    {
        using var dir = TempStorageDir.Create();
        var reader = new SegmentDirectoryCapacityReader(dir.Options.SegmentDir, total: 10_000_000);
        await using var engine = Open(dir, reader);
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        engine.TestPersistFaultExceptionKind = FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;
        var record = CreateRecord("<clean-fail@seg.test>");
        var physical = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);

        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        await WaitUntilAsync(() => engine.PersistBlockedRetryScheduledCount >= 1);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalSegmentCopyCount);
        Assert.Equal((0, 0), engine.ProcessLocalArticleCopyCounts(accepted.Sequence));
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.Equal(0, reader.Read().UsedBytes);
        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.True(physical > 0);
    }

    [Fact]
    public async Task Restart_does_not_rebuild_article_reservations_for_existing_files()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<restart-hold@seg.test>");
        var physical = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        SegmentId segmentId;
        await using (var engine = Open(dir, new SegmentDirectoryCapacityReader(dir.Options.SegmentDir, total: 10_000_000)))
        {
            _ = await AcceptDurableAsync(engine, record);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            segmentId = meta.Location.SegmentId;
            Assert.Equal(physical, engine.ProcessLocalArticleReservedBytes);
            Assert.Equal(physical, engine.ProcessLocalWrittenArticleBytesOnSegment(segmentId));
        }

        var reader = new SegmentDirectoryCapacityReader(dir.Options.SegmentDir, total: 10_000_000);
        await using var restarted = Open(dir, reader);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.Equal(physical, reader.Read().UsedBytes);
        Assert.Equal(0, restarted.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, restarted.ProcessLocalSegmentCopyCount);
        Assert.Equal(0, restarted.ProcessLocalWrittenArticleBytesOnSegment(segmentId));
        Assert.True(restarted.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Reclaiming_the_source_does_not_release_the_compaction_destination_reservation()
    {
        using var dir = TempStorageDir.Create();
        var reader = new SegmentDirectoryCapacityReader(dir.Options.SegmentDir, total: 10_000_000);
        await using var engine = Open(dir, reader);
        var record = CreateRecord("<compact-iso@seg.test>");
        var physical = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        _ = await AcceptDurableAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var source));
        var sourceId = source.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();

        var compact = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var moved));
        var destinationId = moved.Location.SegmentId;
        Assert.NotEqual(sourceId, destinationId);
        Assert.Equal(physical, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(physical, engine.ProcessLocalWrittenArticleBytesOnSegment(sourceId));
        Assert.Equal(0, engine.ProcessLocalWrittenArticleBytesOnSegment(destinationId));
        Assert.Equal(physical, engine.ProcessLocalCompactionReservedBytes);

        var retired = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retired.Outcome);
        var usedBefore = reader.Read().UsedBytes;
        var reclaimSource = await engine.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimSource.Outcome);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalWrittenArticleBytesOnSegment(sourceId));
        Assert.Equal(physical, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(1, engine.ProcessLocalCompactionReservationCount);
        Assert.Equal(usedBefore - physical, reader.Read().UsedBytes);

        Assert.True(engine.TryEvict(record.ArtId));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        var compactDestination = await engine.CompactClosedSegmentAsync(destinationId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compactDestination.Outcome);
        Assert.Equal(physical, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        var retireDestination = await engine.RetireCompactedSegmentAsync(
            compactDestination.CompactionId,
            CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retireDestination.Outcome);
        var reclaimDestination = await engine.ReclaimRetiredSegmentAsync(destinationId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimDestination.Outcome);
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCompactionReservationCount);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
    }

    [Fact]
    public async Task Checkpoint_does_not_change_article_reserved_bytes()
    {
        using var dir = TempStorageDir.Create();
        var reader = new SegmentDirectoryCapacityReader(dir.Options.SegmentDir, total: 10_000_000);
        await using var engine = Open(dir, reader);
        var record = CreateRecord("<ckpt-iso@seg.test>");
        var physical = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var accepted = await AcceptDurableAsync(engine, record);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var used = reader.Read().UsedBytes;

        Assert.True(engine.CheckpointTruncateCommitted() >= 0);
        Assert.Equal(physical, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal((1, 1), engine.ProcessLocalArticleCopyCounts(accepted.Sequence));
        Assert.Equal(physical, engine.ProcessLocalWrittenArticleBytesOnSegment(meta.Location.SegmentId));

        Assert.True(engine.CheckpointIndex() >= 0);
        Assert.Equal(physical, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal((1, 1), engine.ProcessLocalArticleCopyCounts(accepted.Sequence));
        Assert.Equal(physical, engine.ProcessLocalWrittenArticleBytesOnSegment(meta.Location.SegmentId));
        Assert.Equal(used, reader.Read().UsedBytes);
    }

    private static async Task<ArticleAcceptResult> AcceptDurableAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        return accepted;
    }

    private static async Task<SegmentId> RetireClosedSegmentAsync(
        FileArticleStorageEngine engine,
        params ArticleId[] artIds)
    {
        Assert.True(engine.Index.TryGet(artIds[0], out var first));
        var segmentId = first.Location.SegmentId;
        if (engine.Segments.TryGetSegmentInfo(segmentId, out var info) && info.State == SegmentState.Active)
        {
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
        }

        foreach (var artId in artIds)
        {
            Assert.True(engine.TryEvict(artId));
        }

        engine.CompleteUnreferencedExtentAccounting();
        var compact = await engine.CompactClosedSegmentAsync(segmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        var retired = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retired.Outcome);
        return segmentId;
    }

    private static string RetiredPath(TempStorageDir dir, SegmentId segmentId) =>
        Path.Combine(dir.Options.SegmentDir, SegmentFileNames.Format(segmentId, SegmentFileKind.Retired));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met.");
            }

            await Task.Delay(10);
        }
    }

    private static string BodyOfMebibytes(int mebibytes)
    {
        var line = new string('x', 1022) + "\r\n";
        var lines = mebibytes * 1024;
        var builder = new StringBuilder(lines * line.Length);
        for (var i = 0; i < lines; i++)
        {
            _ = builder.Append(line);
        }

        return builder.ToString();
    }

    private static FileArticleStorageEngine Open(TempStorageDir dir, IStorageCapacityReader reader) =>
        FileArticleStorageEngine.Open(
            dir.Options,
            capacityReader: reader);

    private static ArticleRecord CreateRecord(string messageId, string body = "line1\r\nline2\r\n")
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: article-reservation\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class SegmentDirectoryCapacityReader(string segmentDir, long total) : IStorageCapacityReader
    {
        public StorageCapacitySnapshot Read()
        {
            long used = 0;
            if (Directory.Exists(segmentDir))
            {
                foreach (var file in Directory.EnumerateFiles(segmentDir))
                {
                    used += new FileInfo(file).Length;
                }
            }

            return new StorageCapacitySnapshot(total, used, Math.Max(0L, total - used));
        }
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-art-hold-" + Guid.NewGuid().ToString("N"));
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
    }
}
