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
/// Phase 2F: a durability flush that throws is not evidence that the write is absent.
/// </summary>
public sealed class AmbiguousAppendCapacityTests
{
    [Fact]
    public async Task Journal_no_growth_releases_the_attempt_reservation()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 100_000, used: 0);
        await using var engine = Open(dir, reader);
        engine.SuspendBackgroundPersist = true;
        engine.Journal.TestAfterWriteBeforeFlush = NoGrowth;
        var record = CreateRecord("<amb-j-none@seg.test>");

        var ex = await Assert.ThrowsAsync<IOException>(() => engine.AcceptAsync(record, CancellationToken.None));
        Assert.IsNotType<UnreconciledDurableTailException>(ex);
        Assert.Equal(0, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalIndexReservedBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Journal_complete_accept_is_retained_and_not_duplicated()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 100_000, used: 0);
        await using var engine = Open(dir, reader);
        engine.SuspendBackgroundPersist = true;
        engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
        var record = CreateRecord("<amb-j-accept@seg.test>");
        var admit = AdmitBytes(record);

        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        var length = engine.Journal.JournalPhysicalBytes;
        Assert.True(length > 0);
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
        Assert.Equal(ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize), engine.ProcessLocalJournalReservedBytes);
        Assert.Single(engine.Journal.EnumerateIncomplete());

        engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
        var duplicate = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal(length, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
    }

    [Fact]
    public async Task Journal_complete_accept_blocks_exact_fit_capacity()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<amb-j-fit@seg.test>");
        var admit = AdmitBytes(record);
        const long total = 100_000;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 0.80);
        var reader = new MutableCapacityReader(total, used: ceiling - admit);
        await using var engine = Open(dir, reader);
        engine.SuspendBackgroundPersist = true;
        engine.Journal.TestAfterWriteBeforeFlush = FlushFails;

        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
        var length = engine.Journal.JournalPhysicalBytes;

        var rejected = await engine.AcceptAsync(CreateRecord("<amb-j-fit2@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, rejected.Outcome);
        Assert.Equal(length, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
    }

    [Fact]
    public async Task Journal_complete_physical_written_is_not_duplicated()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(100_000, 0));
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<amb-j-pw@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        var accept = engine.Journal.EnumerateIncomplete()[0].Accept;
        var location = new StoredArticleLocation(new SegmentId(1), 0, accept.ArtSize);
        var written = new JournalPhysicalWrittenRecord(1, accept.Sequence, location);
        var frameLength = ArticleJournalFrameCodec.EncodePhysicalWritten(written).Length;
        var before = engine.Journal.JournalPhysicalBytes;
        engine.Journal.TestAfterWriteBeforeFlush = FlushFails;

        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendPhysicalWrittenAsync(written, CancellationToken.None));
        Assert.Equal(before + frameLength, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(
            JournalAppendOutcome.IdempotentNoOp,
            await engine.Journal.AppendPhysicalWrittenAsync(written, CancellationToken.None));
        Assert.Equal(before + frameLength, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize), engine.ProcessLocalJournalReservedBytes);
    }

    [Fact]
    public async Task Journal_complete_index_committed_is_not_duplicated()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(100_000, 0));
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<amb-j-ic@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        var accept = engine.Journal.EnumerateIncomplete()[0].Accept;
        var location = new StoredArticleLocation(new SegmentId(1), 0, accept.ArtSize);
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                CancellationToken.None));
        var committed = new JournalIndexCommittedRecord(1, accept.Sequence);
        var frameLength = ArticleJournalFrameCodec.EncodeIndexCommitted(committed).Length;
        var before = engine.Journal.JournalPhysicalBytes;
        engine.Journal.TestAfterWriteBeforeFlush = FlushFails;

        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendIndexCommittedAsync(committed, CancellationToken.None));
        Assert.Equal(before + frameLength, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(
            JournalAppendOutcome.IdempotentNoOp,
            await engine.Journal.AppendIndexCommittedAsync(committed, CancellationToken.None));
        Assert.Equal(before + frameLength, engine.Journal.JournalPhysicalBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize), engine.ProcessLocalJournalReservedBytes);
    }

    [Fact]
    public async Task Journal_complete_compaction_begin_is_not_duplicated()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(100_000, 0));
        engine.SuspendBackgroundPersist = true;
        var compactionId = engine.Journal.AllocateCompactionId();
        var begin = new JournalCompactionBeginRecord(1, compactionId, new SegmentId(1), 1);
        engine.Journal.TestAfterWriteBeforeFlush = FlushFails;

        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(begin, CancellationToken.None));
        Assert.Equal(ArticleJournalFrameCodec.CompactionBeginFrameLength, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(
            JournalAppendOutcome.IdempotentNoOp,
            await engine.Journal.AppendCompactionBeginAsync(begin, CancellationToken.None));
        Assert.Equal(ArticleJournalFrameCodec.CompactionBeginFrameLength, engine.Journal.JournalPhysicalBytes);
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out _));
    }

    [Fact]
    public async Task Journal_incomplete_tail_is_truncated_before_another_append()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(100_000, 0));
        engine.SuspendBackgroundPersist = true;
        engine.Journal.TestAfterWriteBeforeFlush = PartialGrowth;
        var record = CreateRecord("<amb-j-torn@seg.test>");

        var ex = await Assert.ThrowsAsync<IOException>(() => engine.AcceptAsync(record, CancellationToken.None));
        Assert.IsNotType<UnreconciledDurableTailException>(ex);
        Assert.Equal(0, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);

        engine.Journal.TestAfterWriteBeforeFlush = null;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.Journal.JournalPhysicalBytes > 0);
    }

    [Fact]
    public async Task Segment_no_growth_keeps_one_copy_and_retry_writes_once()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(100_000, 0));
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        var faults = 0;
        engine.Segments.TestAfterWriteBeforeFlush = (stream, start, _) =>
        {
            if (Interlocked.Increment(ref faults) == 1)
            {
                stream.SetLength(start);
                throw new IOException("no-growth");
            }
        };
        var record = CreateRecord("<amb-s-none@seg.test>");
        var copyBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(2, faults);
        Assert.Equal(copyBytes, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        Assert.Equal(copyBytes, ActiveSegmentLength(dir));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(0, meta.Location.Offset);
        Assert.Equal(1, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task Segment_complete_record_retains_reservation_and_reconciles_cursor()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(100_000, 0));
        engine.Segments.TestAfterWriteBeforeFlush = FlushFails;
        var first = CreateRecord("<amb-s-ok@seg.test>");
        var copyBytes = SegmentRecordCodec.RecordLengthForArtSize(first.ArtSize);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(copyBytes, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        Assert.True(engine.Index.TryGet(first.ArtId, out var firstMeta));
        Assert.Equal(0, firstMeta.Location.Offset);
        Assert.Equal(copyBytes, firstMeta.Location.Length);
        Assert.Equal(copyBytes, ActiveSegmentLength(dir));
        var appender = await engine.Segments.GetActiveAppenderAsync(CancellationToken.None);
        Assert.Equal(copyBytes, appender.SizeBytes);

        engine.Segments.TestAfterWriteBeforeFlush = null;
        var second = CreateRecord("<amb-s-ok2@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(second, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(engine.Index.TryGet(second.ArtId, out var secondMeta));
        Assert.Equal(firstMeta.Location.Length, secondMeta.Location.Offset);
        Assert.Equal(2, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task Segment_incomplete_tail_is_repaired_before_retry()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(100_000, 0));
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        var faults = 0;
        engine.Segments.TestAfterWriteBeforeFlush = (stream, start, _) =>
        {
            if (Interlocked.Increment(ref faults) == 1)
            {
                stream.SetLength(start + 1);
                throw new IOException("partial");
            }
        };
        var record = CreateRecord("<amb-s-torn@seg.test>");
        var copyBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(2, faults);
        Assert.Equal(copyBytes, ActiveSegmentLength(dir));
        Assert.Equal(copyBytes, (await engine.Segments.GetActiveAppenderAsync(CancellationToken.None)).SizeBytes);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(0, meta.Location.Offset);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.Equal(copyBytes, engine.ProcessLocalArticleReservedBytes);
    }

    [Fact]
    public async Task Segment_complete_record_continues_consuming_capacity()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<amb-s-cap@seg.test>");
        var admit = AdmitBytes(record);
        const long total = 100_000;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 0.80);
        var reader = new MutableCapacityReader(total, used: ceiling - admit);
        await using var engine = Open(dir, reader);
        engine.Segments.TestAfterWriteBeforeFlush = FlushFails;

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize), engine.ProcessLocalArticleReservedBytes);

        var rejected = await engine.AcceptAsync(CreateRecord("<amb-s-cap2@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, rejected.Outcome);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.Equal(SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize), ActiveSegmentLength(dir));
    }

    [Fact]
    public async Task Compaction_destination_complete_record_keeps_its_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(1_000_000, 0));
        var record = CreateRecord("<amb-dest@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(engine.Index.TryGet(record.ArtId, out var source));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.Segments.TestAfterWriteBeforeFlush = FlushFails;

        var compact = await engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        var copyBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        Assert.Equal(copyBytes, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(2, engine.PhysicalAppendCount);
        Assert.Equal(copyBytes, ActiveSegmentLength(dir));

        var again = await engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None);
        Assert.Equal(2, engine.PhysicalAppendCount);
        Assert.Equal(copyBytes, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(copyBytes, ActiveSegmentLength(dir));
        Assert.NotEqual(ArticleCompactionOutcome.Failed, again.Outcome);
    }

    [Fact]
    public async Task Accept_pending_flush_does_not_publish_and_retry_uses_one_reservation()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<amb-pend-acc@seg.test>");
        var admit = AdmitBytes(record);
        const long total = 100_000;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 0.80);
        var reader = new MutableCapacityReader(total, used: ceiling - admit);
        await using var engine = Open(dir, reader);
        engine.SuspendBackgroundPersist = true;
        engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
        engine.Journal.TestBeforeDurableFlush = () => throw new IOException("second-flush");

        var pending = await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.AcceptAsync(record, CancellationToken.None));
        Assert.False(string.IsNullOrWhiteSpace(pending.Message));
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
        var physical = engine.Journal.JournalPhysicalBytes;
        Assert.True(physical > 0);

        var again = await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.AcceptAsync(record, CancellationToken.None));
        Assert.False(string.IsNullOrWhiteSpace(again.Message));
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
        Assert.Equal(physical, engine.Journal.JournalPhysicalBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());

        var other = await engine.AcceptAsync(CreateRecord("<amb-pend-other@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, other.Outcome);
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);

        engine.Journal.TestBeforeDurableFlush = null;
        engine.Journal.TestAfterWriteBeforeFlush = null;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
        Assert.Equal(physical, engine.Journal.JournalPhysicalBytes);
    }

    [Fact]
    public async Task Accept_failed_truncate_reuses_the_same_reservation()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<amb-trunc@seg.test>");
        var admit = AdmitBytes(record);
        await using var engine = Open(dir, new MutableCapacityReader(100_000, 0));
        engine.SuspendBackgroundPersist = true;
        engine.Journal.TestAfterWriteBeforeFlush = PartialGrowth;
        engine.Journal.TestFailTailTruncate = true;

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.AcceptAsync(record, CancellationToken.None));
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.AcceptAsync(record, CancellationToken.None));
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.AcceptAsync(CreateRecord("<amb-trunc-other@seg.test>"), CancellationToken.None));
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());

        engine.Journal.TestFailTailTruncate = false;
        engine.Journal.TestAfterWriteBeforeFlush = null;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
    }

    [Fact]
    public async Task Segment_pending_flush_keeps_the_pre_write_cursor()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<amb-pend-seg@seg.test>");
        var admit = AdmitBytes(record);
        var copyBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        const long total = 100_000;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 0.80);
        await using var engine = Open(dir, new MutableCapacityReader(total, used: ceiling - admit));
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long sizeDuringPending = -1;
        engine.Segments.TestAfterWriteBeforeFlush = (_, _, _) => throw new IOException("first-flush");
        engine.Segments.TestBeforeDurableFlush = () =>
        {
            if (sizeDuringPending >= 0)
            {
                return;
            }

            sizeDuringPending = engine.Segments.GetActiveSizeBytes();
            blocked.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            throw new IOException("second-flush");
        };

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        Assert.Equal(0, sizeDuringPending);
        Assert.Equal(copyBytes, ActiveSegmentLength(dir));
        Assert.Equal(copyBytes, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
        var rejected = await engine.AcceptAsync(CreateRecord("<amb-pend-seg2@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, rejected.Outcome);
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
        release.TrySetResult();

        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(0, meta.Location.Offset);
        Assert.Equal(copyBytes, meta.Location.Length);
        Assert.Equal(copyBytes, (await engine.Segments.GetActiveAppenderAsync(CancellationToken.None)).SizeBytes);
        Assert.Equal(copyBytes, ActiveSegmentLength(dir));
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.Equal(copyBytes, engine.ProcessLocalArticleReservedBytes);
    }

    [Fact]
    public async Task Compaction_begin_stays_pending_until_durable_flush()
    {
        using var dir = TempStorageDir.Create();
        await using (var engine = Open(dir, new MutableCapacityReader(1_000_000, 0)))
        {
            var record = CreateRecord("<amb-pend-begin@seg.test>");
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(engine.Index.TryGet(record.ArtId, out var source));
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
            engine.Journal.TestBeforeDurableFlush = () => throw new IOException("second-flush");

            await Assert.ThrowsAsync<UnreconciledDurableTailException>(
                () => engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None));
            Assert.Empty(engine.Journal.EnumerateOpenCompactions());
            Assert.Equal(
                ArticleJournalFrameCodec.CompactionBeginFrameLength,
                engine.ProcessLocalCompactionJournalReservedBytes);
            Assert.Equal(1, engine.ProcessLocalCompactionJournalFrameCount);

            engine.Journal.TestBeforeDurableFlush = null;
            engine.Journal.TestAfterWriteBeforeFlush = null;
            var compact = await engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None);
            Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
            Assert.True(engine.Journal.TryGetCompaction(compact.CompactionId, out var snapshot));
            Assert.True(snapshot.Committed);
            Assert.Equal(1, engine.ProcessLocalCompactionJournalFrameCount
                - snapshot.Relocations.Count
                - snapshot.Relocations.Count(static relocation => relocation.Written is not null)
                - 1
                - (snapshot.Retired is not null ? 1 : 0));
        }

        Assert.Equal(1, CountFrames(dir).GetValueOrDefault(ArticleJournalFrameType.CompactionBegin));
    }

    [Fact]
    public async Task Compaction_destination_pending_reuses_its_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(1_000_000, 0));
        var record = CreateRecord("<amb-pend-dest@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(engine.Index.TryGet(record.ArtId, out var source));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var copyBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        engine.Segments.TestAfterWriteBeforeFlush = FlushFails;
        engine.Segments.TestBeforeDurableFlush = () => throw new IOException("second-flush");

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None));
        Assert.Equal(copyBytes, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.Equal(source.Location, engine.Index.TryGet(record.ArtId, out var during) ? during.Location : default);
        Assert.Equal(copyBytes, ActiveSegmentLength(dir));

        engine.Segments.TestBeforeDurableFlush = null;
        engine.Segments.TestAfterWriteBeforeFlush = null;
        var compact = await engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.Equal(copyBytes, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(2, engine.PhysicalAppendCount);
        Assert.Equal(copyBytes, ActiveSegmentLength(dir));
        Assert.True(engine.Index.TryGet(record.ArtId, out var moved));
        Assert.NotEqual(source.Location, moved.Location);
        Assert.Equal(0, moved.Location.Offset);
    }

    [Fact]
    public async Task Restart_replays_a_pending_journal_frame_from_the_file()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<amb-re-pend@seg.test>");
        var reader = new MutableCapacityReader(100_000, 0);
        await using (var engine = Open(dir, reader))
        {
            engine.SuspendBackgroundPersist = true;
            engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
            engine.Journal.TestBeforeDurableFlush = () => throw new IOException("second-flush");
            await Assert.ThrowsAsync<UnreconciledDurableTailException>(
                () => engine.AcceptAsync(record, CancellationToken.None));
            Assert.Empty(engine.Journal.EnumerateIncomplete());
        }

        await using (var restarted = Open(dir, reader))
        {
            Assert.Single(restarted.Journal.EnumerateIncomplete());
            Assert.Equal(1, restarted.ProcessLocalJournalReservationCount);
            Assert.Equal(
                ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize),
                restarted.ProcessLocalJournalReservedBytes);
        }

        Assert.Equal(1, CountFrames(dir).GetValueOrDefault(ArticleJournalFrameType.Accept));
    }

    [Fact]
    public async Task Restart_adopts_a_pending_segment_record_once()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<amb-re-pend-seg@seg.test>");
        var reader = new MutableCapacityReader(100_000, 0);
        var copyBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var engine = Open(dir, reader))
        {
            engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
            engine.Segments.TestAfterWriteBeforeFlush = (_, _, _) => throw new IOException("first-flush");
            engine.Segments.TestBeforeDurableFlush = () =>
            {
                failed.TrySetResult();
                throw new IOException("second-flush");
            };
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        await using var restarted = Open(dir, reader);
        await restarted.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(restarted.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(0, meta.Location.Offset);
        Assert.Equal(copyBytes, meta.Location.Length);
        Assert.Equal(copyBytes, ActiveSegmentLength(dir));
        Assert.Equal(0, restarted.PhysicalAppendCount);
        Assert.Equal(0, restarted.ProcessLocalSegmentCopyCount);
    }

    [Fact]
    public async Task Compaction_journal_complete_frame_is_retained_once()
    {
        using var dir = TempStorageDir.Create();
        await using (var engine = Open(dir, new MutableCapacityReader(1_000_000, 0)))
        {
            var record = CreateRecord("<amb-cj@seg.test>");
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(engine.Index.TryGet(record.ArtId, out var source));
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            var before = engine.Journal.JournalPhysicalBytes;
            engine.Journal.TestAfterWriteBeforeFlush = FlushFails;

            var compact = await engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None);
            Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
            Assert.True(engine.Journal.TryGetCompaction(compact.CompactionId, out var snapshot));
            Assert.True(snapshot.Committed);
            var lengthAfterBegin = before + ArticleJournalFrameCodec.CompactionBeginFrameLength;
            Assert.True(engine.Journal.JournalPhysicalBytes > lengthAfterBegin);
            Assert.Equal(
                JournalAppendOutcome.IdempotentNoOp,
                await engine.Journal.AppendCompactionBeginAsync(snapshot.Begin, CancellationToken.None));
            var expectedJournal = (long)ArticleJournalFrameCodec.CompactionBeginFrameLength;
            var expectedFrames = 1;
            foreach (var relocation in snapshot.Relocations)
            {
                expectedJournal += ArticleJournalFrameCodec.RelocationIntentFrameLength;
                expectedFrames++;
                if (relocation.Written is not null)
                {
                    expectedJournal += ArticleJournalFrameCodec.RelocationWrittenFrameLength;
                    expectedFrames++;
                }
            }

            if (snapshot.Committed)
            {
                expectedJournal += ArticleJournalFrameCodec.CompactionCommittedFrameLength;
                expectedFrames++;
            }

            if (snapshot.Retired is not null)
            {
                expectedJournal += ArticleJournalFrameCodec.CompactionRetiredFrameLength;
                expectedFrames++;
            }

            Assert.Equal(expectedJournal, engine.ProcessLocalCompactionJournalReservedBytes);
            Assert.Equal(expectedFrames, engine.ProcessLocalCompactionJournalFrameCount);
        }

        Assert.Equal(1, CountFrames(dir).GetValueOrDefault(ArticleJournalFrameType.CompactionBegin));
    }

    [Fact]
    public async Task Restart_replays_complete_accept_once()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<amb-re-acc@seg.test>");
        var reader = new MutableCapacityReader(100_000, 0);
        await using (var engine = Open(dir, reader))
        {
            engine.SuspendBackgroundPersist = true;
            engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        }

        await using (var restarted = Open(dir, reader))
        {
            Assert.Equal(1, restarted.ProcessLocalJournalReservationCount);
            Assert.Equal(ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize), restarted.ProcessLocalJournalReservedBytes);
            Assert.Equal(0, restarted.ProcessLocalSegmentCopyCount);
            Assert.Single(restarted.Journal.EnumerateIncomplete());
        }

        var counts = CountFrames(dir);
        Assert.Equal(1, counts.GetValueOrDefault(ArticleJournalFrameType.Accept));
        Assert.Equal(0, counts.GetValueOrDefault(ArticleJournalFrameType.PhysicalWritten));
    }

    [Fact]
    public async Task Restart_replays_complete_compaction_begin_once()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(100_000, 0);
        ulong compactionId;
        await using (var engine = Open(dir, reader))
        {
            engine.SuspendBackgroundPersist = true;
            compactionId = engine.Journal.AllocateCompactionId();
            engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engine.Journal.AppendCompactionBeginAsync(
                    new JournalCompactionBeginRecord(1, compactionId, new SegmentId(7), 3),
                    CancellationToken.None));
        }

        await using (var restarted = Open(dir, reader))
        {
            Assert.Equal(1, restarted.ProcessLocalCompactionJournalFrameCount);
            Assert.Equal(ArticleJournalFrameCodec.CompactionBeginFrameLength, restarted.ProcessLocalCompactionJournalReservedBytes);
            Assert.True(restarted.Journal.TryGetCompaction(compactionId, out var snapshot));
            Assert.Equal(new SegmentId(7), snapshot.Begin.SourceSegmentId);
            Assert.False(snapshot.Committed);
        }

        Assert.Equal(1, CountFrames(dir).GetValueOrDefault(ArticleJournalFrameType.CompactionBegin));
    }

    [Fact]
    public async Task Restart_proves_complete_segment_record_without_double_counting()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<amb-re-seg@seg.test>");
        var reader = new MutableCapacityReader(100_000, 0);
        StoredArticleLocation location;
        await using (var engine = Open(dir, reader))
        {
            engine.Segments.TestAfterWriteBeforeFlush = FlushFails;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            location = meta.Location;
        }

        await using (var restarted = Open(dir, reader))
        {
            Assert.True(restarted.Index.TryGet(record.ArtId, out var restored));
            Assert.Equal(location, restored.Location);
            Assert.True(restarted.Segments.TryReadProven(
                location,
                record.ArtId,
                record.ArtHash,
                record.ArtSize,
                out var artData));
            Assert.Equal(record.ArtSize, artData.Length);
            Assert.Equal(1, restarted.ProcessLocalJournalReservationCount);
            Assert.Equal(1, restarted.ProcessLocalIndexFrameCount);
            Assert.Equal(0, restarted.ProcessLocalSegmentCopyCount);
            Assert.Equal(0, restarted.ProcessLocalArticleReservedBytes);
        }

        var counts = CountFrames(dir);
        Assert.Equal(1, counts.GetValueOrDefault(ArticleJournalFrameType.Accept));
        Assert.Equal(1, counts.GetValueOrDefault(ArticleJournalFrameType.PhysicalWritten));
        Assert.Equal(1, counts.GetValueOrDefault(ArticleJournalFrameType.IndexCommitted));
    }

    [Fact]
    public async Task Restart_truncates_incomplete_journal_tail()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<amb-re-jt@seg.test>");
        var reader = new MutableCapacityReader(100_000, 0);
        long validLength;
        await using (var engine = Open(dir, reader))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            validLength = engine.Journal.JournalPhysicalBytes;
        }

        var path = Path.Combine(dir.Options.ControlDir, FileArticleJournal.JournalFileName);
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.WriteByte(0x5A);
        }

        await using (var restarted = Open(dir, reader))
        {
            Assert.Equal(validLength, restarted.Journal.JournalPhysicalBytes);
            Assert.Equal(1, restarted.ProcessLocalJournalReservationCount);
            Assert.Equal(ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize), restarted.ProcessLocalJournalReservedBytes);
        }

        Assert.Equal(1, CountFrames(dir).GetValueOrDefault(ArticleJournalFrameType.Accept));
    }

    [Fact]
    public async Task Restart_truncates_incomplete_active_segment_tail()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<amb-re-st@seg.test>");
        var reader = new MutableCapacityReader(100_000, 0);
        var copyBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        await using (var engine = Open(dir, reader))
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(copyBytes, ActiveSegmentLength(dir));
        }

        var active = Directory.GetFiles(dir.Options.SegmentDir, "seg-*.active").Single();
        using (var stream = new FileStream(active, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.WriteByte(0x5A);
        }

        await using var restarted = Open(dir, reader);
        Assert.Equal(copyBytes, ActiveSegmentLength(dir));
        Assert.Equal(copyBytes, (await restarted.Segments.GetActiveAppenderAsync(CancellationToken.None)).SizeBytes);
        Assert.True(restarted.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(0, meta.Location.Offset);
        Assert.Equal(0, restarted.ProcessLocalSegmentCopyCount);
        Assert.Equal(1, restarted.ProcessLocalJournalReservationCount);
    }

    [Fact]
    public async Task Pending_physical_written_does_not_become_another_accept()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(1_000_000, 0));
        engine.SuspendBackgroundPersist = true;
        var owner = CreateRecord("<amb-own-pw@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(owner, CancellationToken.None)).Outcome);
        var accept = engine.Journal.EnumerateIncomplete()[0].Accept;
        var written = new JournalPhysicalWrittenRecord(
            1,
            accept.Sequence,
            new StoredArticleLocation(new SegmentId(1), 0, accept.ArtSize));
        var before = engine.Journal.JournalPhysicalBytes;
        var frameLength = ArticleJournalFrameCodec.EncodePhysicalWritten(written).Length;
        var reserved = engine.ProcessLocalReservedBytes;
        engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
        engine.Journal.TestBeforeDurableFlush = () => throw new IOException("second-flush");

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.Journal.AppendPhysicalWrittenAsync(written, CancellationToken.None).AsTask());
        Assert.Null(engine.Journal.EnumerateIncomplete()[0].PhysicalWritten);
        Assert.Equal(before + frameLength, engine.Journal.JournalPhysicalBytes);

        var other = CreateRecord("<amb-own-pw-b@seg.test>");
        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.AcceptAsync(other, CancellationToken.None));
        Assert.Equal(reserved, engine.ProcessLocalReservedBytes);
        Assert.Equal(before + frameLength, engine.Journal.JournalPhysicalBytes);
        Assert.Null(engine.Journal.EnumerateIncomplete()[0].PhysicalWritten);
        Assert.DoesNotContain(engine.Journal.EnumerateIncomplete(), item => item.Accept.ArtId == other.ArtId);

        engine.Journal.TestBeforeDurableFlush = null;
        engine.Journal.TestAfterWriteBeforeFlush = null;
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendPhysicalWrittenAsync(written, CancellationToken.None));
        Assert.Equal(before + frameLength, engine.Journal.JournalPhysicalBytes);
        Assert.NotNull(engine.Journal.EnumerateIncomplete()[0].PhysicalWritten);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(other, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Pending_index_committed_rejects_another_sequence_and_article()
    {
        using var dir = TempStorageDir.Create();
        await using (var engine = Open(dir, new MutableCapacityReader(1_000_000, 0)))
        {
            engine.SuspendBackgroundPersist = true;
            var owner = CreateRecord("<amb-own-ic@seg.test>");
            var other = CreateRecord("<amb-own-ic-b@seg.test>");
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(owner, CancellationToken.None)).Outcome);
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(other, CancellationToken.None)).Outcome);
            var ownerAccept = engine.Journal.EnumerateIncomplete().Single(item => item.Accept.ArtId == owner.ArtId).Accept;
            var otherAccept = engine.Journal.EnumerateIncomplete().Single(item => item.Accept.ArtId == other.ArtId).Accept;
            Assert.NotEqual(ownerAccept.Sequence, otherAccept.Sequence);
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engine.Journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(
                        1,
                        ownerAccept.Sequence,
                        new StoredArticleLocation(new SegmentId(1), 0, ownerAccept.ArtSize)),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engine.Journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(
                        1,
                        otherAccept.Sequence,
                        new StoredArticleLocation(new SegmentId(1), 0, otherAccept.ArtSize)),
                    CancellationToken.None));

            var ownerCommitted = new JournalIndexCommittedRecord(1, ownerAccept.Sequence);
            var otherCommitted = new JournalIndexCommittedRecord(1, otherAccept.Sequence);
            var frameLength = ArticleJournalFrameCodec.EncodeIndexCommitted(ownerCommitted).Length;
            var before = engine.Journal.JournalPhysicalBytes;
            var reserved = engine.ProcessLocalJournalReservedBytes;
            Assert.Equal(
                ArticleJournalFrameCodec.SequenceReservationBytes(owner.ArtSize)
                + ArticleJournalFrameCodec.SequenceReservationBytes(other.ArtSize),
                reserved);
            Assert.Equal(2, engine.ProcessLocalJournalReservationCount);
            engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
            engine.Journal.TestBeforeDurableFlush = () => throw new IOException("second-flush");

            await Assert.ThrowsAsync<UnreconciledDurableTailException>(
                () => engine.Journal.AppendIndexCommittedAsync(ownerCommitted, CancellationToken.None).AsTask());
            Assert.Equal(before + frameLength, engine.Journal.JournalPhysicalBytes);
            Assert.Equal(2, engine.Journal.EnumerateIncomplete().Count);
            Assert.NotNull(engine.Journal.EnumerateIncomplete().Single(item => item.Accept.Sequence == ownerAccept.Sequence).PhysicalWritten);
            Assert.Equal(reserved, engine.ProcessLocalJournalReservedBytes);
            Assert.Equal(2, engine.ProcessLocalJournalReservationCount);

            await Assert.ThrowsAsync<UnreconciledDurableTailException>(
                () => engine.Journal.AppendIndexCommittedAsync(otherCommitted, CancellationToken.None).AsTask());
            Assert.Equal(before + frameLength, engine.Journal.JournalPhysicalBytes);
            Assert.Equal(2, engine.Journal.EnumerateIncomplete().Count);
            Assert.Contains(
                engine.Journal.EnumerateIncomplete(),
                item => item.Accept.Sequence == ownerAccept.Sequence && item.PhysicalWritten is not null);
            Assert.Contains(
                engine.Journal.EnumerateIncomplete(),
                item => item.Accept.Sequence == otherAccept.Sequence && item.PhysicalWritten is not null);
            Assert.Equal(reserved, engine.ProcessLocalJournalReservedBytes);
            Assert.Equal(2, engine.ProcessLocalJournalReservationCount);

            var third = CreateRecord("<amb-own-ic-c@seg.test>");
            await Assert.ThrowsAsync<UnreconciledDurableTailException>(
                () => engine.AcceptAsync(third, CancellationToken.None));
            Assert.Equal(before + frameLength, engine.Journal.JournalPhysicalBytes);
            Assert.Equal(reserved, engine.ProcessLocalJournalReservedBytes);
            Assert.Equal(2, engine.ProcessLocalJournalReservationCount);
            Assert.DoesNotContain(engine.Journal.EnumerateIncomplete(), item => item.Accept.ArtId == third.ArtId);

            engine.Journal.TestBeforeDurableFlush = null;
            engine.Journal.TestAfterWriteBeforeFlush = null;
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engine.Journal.AppendIndexCommittedAsync(ownerCommitted, CancellationToken.None));
            Assert.Equal(before + frameLength, engine.Journal.JournalPhysicalBytes);
            Assert.Equal(
                JournalAppendOutcome.IdempotentNoOp,
                await engine.Journal.AppendIndexCommittedAsync(ownerCommitted, CancellationToken.None));
            Assert.Equal(before + frameLength, engine.Journal.JournalPhysicalBytes);
            var remaining = Assert.Single(engine.Journal.EnumerateIncomplete());
            Assert.Equal(otherAccept.Sequence, remaining.Accept.Sequence);
            Assert.Equal(reserved, engine.ProcessLocalJournalReservedBytes);
            Assert.Equal(2, engine.ProcessLocalJournalReservationCount);
        }

        var counts = CountFrames(dir);
        Assert.Equal(2, counts.GetValueOrDefault(ArticleJournalFrameType.Accept));
        Assert.Equal(2, counts.GetValueOrDefault(ArticleJournalFrameType.PhysicalWritten));
        Assert.Equal(1, counts.GetValueOrDefault(ArticleJournalFrameType.IndexCommitted));
    }

    [Fact]
    public async Task Pending_accept_rejects_a_different_accept_and_keeps_one_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(1_000_000, 0));
        engine.SuspendBackgroundPersist = true;
        var owner = CreateRecord("<amb-own-acc@seg.test>");
        var admit = AdmitBytes(owner);
        engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
        engine.Journal.TestBeforeDurableFlush = () => throw new IOException("second-flush");

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.AcceptAsync(owner, CancellationToken.None));
        var physical = engine.Journal.JournalPhysicalBytes;
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.AcceptAsync(CreateRecord("<amb-own-acc-b@seg.test>"), CancellationToken.None));
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
        Assert.Equal(physical, engine.Journal.JournalPhysicalBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());

        engine.Journal.TestBeforeDurableFlush = null;
        engine.Journal.TestAfterWriteBeforeFlush = null;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(owner, CancellationToken.None)).Outcome);
        Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
        Assert.Equal(physical, engine.Journal.JournalPhysicalBytes);
    }

    [Fact]
    public async Task Pending_compaction_begin_rejects_a_different_begin()
    {
        using var dir = TempStorageDir.Create();
        SegmentId firstSegment;
        SegmentId secondSegment;
        await using (var engine = Open(dir, new MutableCapacityReader(1_000_000, 0)))
        {
            var first = CreateRecord("<amb-own-begin-a@seg.test>");
            var second = CreateRecord("<amb-own-begin-b@seg.test>");
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(engine.Index.TryGet(first.ArtId, out var firstMeta));
            firstSegment = firstMeta.Location.SegmentId;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(second, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(engine.Index.TryGet(second.ArtId, out var secondMeta));
            secondSegment = secondMeta.Location.SegmentId;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
            engine.Journal.TestBeforeDurableFlush = () => throw new IOException("second-flush");

            await Assert.ThrowsAsync<UnreconciledDurableTailException>(
                () => engine.CompactClosedSegmentAsync(firstSegment, CancellationToken.None));
            Assert.Equal(
                ArticleJournalFrameCodec.CompactionBeginFrameLength,
                engine.ProcessLocalCompactionJournalReservedBytes);
            Assert.Empty(engine.Journal.EnumerateOpenCompactions());

            await Assert.ThrowsAsync<UnreconciledDurableTailException>(
                () => engine.CompactClosedSegmentAsync(secondSegment, CancellationToken.None));
            Assert.Equal(
                ArticleJournalFrameCodec.CompactionBeginFrameLength,
                engine.ProcessLocalCompactionJournalReservedBytes);
            Assert.Empty(engine.Journal.EnumerateOpenCompactions());

            engine.Journal.TestBeforeDurableFlush = null;
            engine.Journal.TestAfterWriteBeforeFlush = null;
            var compact = await engine.CompactClosedSegmentAsync(firstSegment, CancellationToken.None);
            Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        }

        Assert.Equal(1, CountFrames(dir).GetValueOrDefault(ArticleJournalFrameType.CompactionBegin));
    }

    [Fact]
    public async Task Pending_journal_frame_defers_checkpoint_until_flush_succeeds()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(1_000_000, 0));
        var committed = CreateRecord("<amb-ck-done@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(committed, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        engine.SuspendBackgroundPersist = true;
        var pending = CreateRecord("<amb-ck-pend@seg.test>");
        engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
        engine.Journal.TestBeforeDurableFlush = () => throw new IOException("second-flush");

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.AcceptAsync(pending, CancellationToken.None));
        var physical = engine.Journal.JournalPhysicalBytes;
        var reserved = engine.ProcessLocalReservedBytes;
        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => Task.Run(() => engine.CheckpointTruncateCommitted()));
        Assert.Equal(physical, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(reserved, engine.ProcessLocalReservedBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());

        engine.Journal.TestBeforeDurableFlush = null;
        engine.Journal.TestAfterWriteBeforeFlush = null;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(pending, CancellationToken.None)).Outcome);
        Assert.Equal(physical, engine.Journal.JournalPhysicalBytes);
        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Equal(
            ArticleJournalFrameCodec.SequenceReservationBytes(pending.ArtSize),
            engine.ProcessLocalJournalReservedBytes);
    }

    [Fact]
    public async Task Pending_compaction_begin_defers_checkpoint()
    {
        using var dir = TempStorageDir.Create();
        await using (var engine = Open(dir, new MutableCapacityReader(1_000_000, 0)))
        {
            var record = CreateRecord("<amb-ck-begin@seg.test>");
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(engine.Index.TryGet(record.ArtId, out var source));
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
            engine.Journal.TestBeforeDurableFlush = () => throw new IOException("second-flush");

            await Assert.ThrowsAsync<UnreconciledDurableTailException>(
                () => engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None));
            var physical = engine.Journal.JournalPhysicalBytes;
            await Assert.ThrowsAsync<UnreconciledDurableTailException>(
                () => Task.Run(() => engine.CheckpointTruncateCommitted()));
            Assert.Equal(physical, engine.Journal.JournalPhysicalBytes);
            Assert.Equal(
                ArticleJournalFrameCodec.CompactionBeginFrameLength,
                engine.ProcessLocalCompactionJournalReservedBytes);
            Assert.Empty(engine.Journal.EnumerateOpenCompactions());

            engine.Journal.TestBeforeDurableFlush = null;
            engine.Journal.TestAfterWriteBeforeFlush = null;
            Assert.Equal(
                ArticleCompactionOutcome.Committed,
                (await engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None)).Outcome);
            Assert.True(engine.CheckpointTruncateCommitted() >= 0);
        }

        Assert.Equal(1, CountFrames(dir).GetValueOrDefault(ArticleJournalFrameType.CompactionBegin));
    }

    [Fact]
    public async Task Pending_segment_record_defers_close_until_flush_succeeds()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(1_000_000, 0));
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        var record = CreateRecord("<amb-close-pend@seg.test>");
        var copyBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var scheduled = engine.PersistRetryScheduledCount;
        engine.Segments.TestAfterWriteBeforeFlush = (_, _, _) => throw new IOException("first-flush");
        engine.Segments.TestBeforeDurableFlush = () => throw new IOException("second-flush");

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await WaitUntil(() => engine.PersistRetryScheduledCount > scheduled);
        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.Segments.CloseActiveAsync(CancellationToken.None).AsTask());
        Assert.Equal(copyBytes, ActiveSegmentLength(dir));
        Assert.Equal(0, engine.Segments.GetActiveSizeBytes());
        Assert.Equal(copyBytes, engine.ProcessLocalArticleReservedBytes);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));

        engine.Segments.TestBeforeDurableFlush = null;
        engine.Segments.TestAfterWriteBeforeFlush = null;
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(0, meta.Location.Offset);
        Assert.Equal(copyBytes, meta.Location.Length);
        Assert.Equal(copyBytes, engine.Segments.GetActiveSizeBytes());
        Assert.Equal(copyBytes, ActiveSegmentLength(dir));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.Empty(Directory.GetFiles(dir.Options.SegmentDir, "seg-*.active"));
        Assert.Single(Directory.GetFiles(dir.Options.SegmentDir, "seg-*.closed"));
    }

    [Fact]
    public async Task Blocked_segment_tail_defers_close()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(1_000_000, 0));
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        var record = CreateRecord("<amb-close-tail@seg.test>");
        var scheduled = engine.PersistRetryScheduledCount;
        engine.Segments.TestAfterWriteBeforeFlush = PartialGrowth;
        engine.Segments.TestFailTailTruncate = true;

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await WaitUntil(() => engine.PersistRetryScheduledCount > scheduled);
        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.Segments.CloseActiveAsync(CancellationToken.None).AsTask());
        Assert.Single(Directory.GetFiles(dir.Options.SegmentDir, "seg-*.active"));
        Assert.Equal(0, engine.Segments.GetActiveSizeBytes());
        Assert.Equal(SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize), engine.ProcessLocalArticleReservedBytes);
    }

    [Fact]
    public async Task Pending_intent_and_written_frames_reject_unrelated_operations()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(1_000_000, 0));
        var record = CreateRecord("<amb-own-intent@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(engine.Index.TryGet(record.ArtId, out var source));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var writes = 0;
        engine.Journal.TestAfterWriteBeforeFlush = (_, _, _) =>
        {
            if (Interlocked.Increment(ref writes) == 2)
            {
                throw new IOException("flush-failed");
            }
        };
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            if (writes == 2)
            {
                throw new IOException("second-flush");
            }
        };

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None));
        var open = engine.Journal.EnumerateOpenCompactions();
        var snapshot = Assert.Single(open);
        Assert.Empty(snapshot.Relocations);
        var reserved = engine.ProcessLocalCompactionJournalReservedBytes;
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength + ArticleJournalFrameCodec.RelocationIntentFrameLength,
            reserved);

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.RelocateArticleAsync(
                snapshot.Begin.CompactionId,
                relocationId: 99,
                source.Location.SegmentId,
                snapshot.Begin.SourceGeneration,
                record.ArtId,
                CancellationToken.None));
        Assert.Equal(reserved, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Empty(engine.Journal.EnumerateOpenCompactions()[0].Relocations);
        Assert.False(engine.Journal.EnumerateOpenCompactions()[0].Committed);

        engine.Journal.TestAfterWriteBeforeFlush = null;
        engine.Journal.TestBeforeDurableFlush = null;
        writes = 0;
        engine.Journal.TestAfterWriteBeforeFlush = (_, _, _) =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                throw new IOException("flush-failed");
            }
        };
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            if (writes == 1)
            {
                throw new IOException("second-flush");
            }
        };

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None));
        Assert.True(engine.Journal.TryGetCompaction(snapshot.Begin.CompactionId, out var duringWritten));
        Assert.Single(duringWritten.Relocations);
        Assert.Null(duringWritten.Relocations[0].Written);
        Assert.False(duringWritten.Committed);
        reserved = engine.ProcessLocalCompactionJournalReservedBytes;
        Assert.Equal(
            SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize),
            engine.ProcessLocalCompactionReservedBytes);

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.Journal.AppendCompactionCommittedAsync(
                new JournalCompactionCommittedRecord(1, snapshot.Begin.CompactionId),
                CancellationToken.None).AsTask());
        Assert.Equal(reserved, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(
            SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize),
            engine.ProcessLocalCompactionReservedBytes);
        Assert.False(engine.Journal.TryGetCompaction(snapshot.Begin.CompactionId, out var still) && still.Committed);

        engine.Journal.TestAfterWriteBeforeFlush = null;
        engine.Journal.TestBeforeDurableFlush = null;
        Assert.Equal(
            ArticleCompactionOutcome.Committed,
            (await engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None)).Outcome);
        Assert.Equal(
            SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize),
            engine.ProcessLocalCompactionReservedBytes);
    }

    [Fact]
    public async Task Pending_retired_frame_keeps_one_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(1_000_000, 0));
        var record = CreateRecord("<amb-own-ret@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(engine.Index.TryGet(record.ArtId, out var source));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var compact = await engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        engine.CompleteUnreferencedExtentAccounting();
        engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
        engine.Journal.TestBeforeDurableFlush = () => throw new IOException("second-flush");

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None));
        Assert.True(engine.Journal.TryGetCompaction(compact.CompactionId, out var pending));
        Assert.Null(pending.Retired);
        var reserved = engine.ProcessLocalCompactionJournalReservedBytes;

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None));
        Assert.Equal(reserved, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.True(engine.Journal.TryGetCompaction(compact.CompactionId, out pending));
        Assert.Null(pending.Retired);
        Assert.Equal(SegmentState.Closed, engine.Catalogue.TryGet(source.Location.SegmentId, out var info) ? info.State : SegmentState.Retired);

        engine.Journal.TestBeforeDurableFlush = null;
        engine.Journal.TestAfterWriteBeforeFlush = null;
        var retired = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retired.Outcome);
        Assert.Equal(reserved, engine.ProcessLocalCompactionJournalReservedBytes);
    }

    [Fact]
    public async Task Pending_committed_frame_rejects_an_unrelated_accept()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new MutableCapacityReader(1_000_000, 0));
        var record = CreateRecord("<amb-own-commit@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(engine.Index.TryGet(record.ArtId, out var source));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        var writes = 0;
        engine.Journal.TestAfterWriteBeforeFlush = (_, _, _) =>
        {
            if (Interlocked.Increment(ref writes) == 4)
            {
                throw new IOException("flush-failed");
            }
        };
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            if (writes == 4)
            {
                throw new IOException("second-flush");
            }
        };

        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None));
        var compactionId = Assert.Single(engine.Journal.EnumerateOpenCompactions()).Begin.CompactionId;
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out var pending));
        Assert.False(pending.Committed);
        var reserved = engine.ProcessLocalCompactionJournalReservedBytes;
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength
            + ArticleJournalFrameCodec.RelocationIntentFrameLength
            + ArticleJournalFrameCodec.RelocationWrittenFrameLength
            + ArticleJournalFrameCodec.CompactionCommittedFrameLength,
            reserved);

        var other = CreateRecord("<amb-own-commit-b@seg.test>");
        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.AcceptAsync(other, CancellationToken.None));
        Assert.Equal(reserved, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(
            ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize),
            engine.ProcessLocalJournalReservedBytes);
        Assert.False(engine.Index.TryGet(other.ArtId, out _));
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out pending));
        Assert.False(pending.Committed);

        engine.Journal.TestAfterWriteBeforeFlush = null;
        engine.Journal.TestBeforeDurableFlush = null;
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionCommittedAsync(
                new JournalCompactionCommittedRecord(1, compactionId),
                CancellationToken.None));
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out pending));
        Assert.True(pending.Committed);
        Assert.Equal(reserved, engine.ProcessLocalCompactionJournalReservedBytes);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), timeout.Token);
        }
    }

    private static void FlushFails(FileStream stream, long start, int expectedLength)
    {
        _ = stream;
        _ = start;
        _ = expectedLength;
        throw new IOException("flush-failed");
    }

    private static void NoGrowth(FileStream stream, long start, int expectedLength)
    {
        _ = expectedLength;
        stream.SetLength(start);
        throw new IOException("no-growth");
    }

    private static void PartialGrowth(FileStream stream, long start, int expectedLength)
    {
        _ = expectedLength;
        stream.SetLength(start + 1);
        throw new IOException("partial");
    }

    private static long AdmitBytes(ArticleRecord record) =>
        SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize)
        + ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize)
        + ArticleIndexRecordCodec.RecordLength;

    private static long ActiveSegmentLength(TempStorageDir dir)
    {
        var files = Directory.GetFiles(dir.Options.SegmentDir, "seg-*.active");
        Assert.Single(files);
        return new FileInfo(files[0]).Length;
    }

    private static Dictionary<ArticleJournalFrameType, int> CountFrames(TempStorageDir dir)
    {
        var path = Path.Combine(dir.Options.ControlDir, FileArticleJournal.JournalFileName);
        var bytes = File.ReadAllBytes(path);
        var counts = new Dictionary<ArticleJournalFrameType, int>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            Assert.True(
                ArticleJournalFrameCodec.TryDecode(
                    bytes.AsSpan(offset),
                    out var type,
                    out var frameLength,
                    out ArticleJournalDecodedFrame _,
                    out var error),
                error.ToString());
            counts[type] = counts.GetValueOrDefault(type) + 1;
            offset += frameLength;
        }

        Assert.Equal(bytes.Length, offset);
        return counts;
    }

    private static FileArticleStorageEngine Open(TempStorageDir dir, MutableCapacityReader reader) =>
        FileArticleStorageEngine.Open(
            dir.Options with
            {
                CapacityAdmissionEnabled = true,
                CapacityMaximumUtilization = 0.80,
                CapacityCompactionHeadroom = 0.10,
            },
            volumeProbe: ScriptedVolumeProbe.Same(dir),
            capacityReader: reader);

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: ambiguous-append\r\n");
        _ = builder.Append("\r\nline1\r\nline2\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class ScriptedVolumeProbe : IStorageVolumeProbe
    {
        private readonly string _segmentDir;
        private readonly string _controlDir;

        private ScriptedVolumeProbe(string segmentDir, string controlDir)
        {
            _segmentDir = Path.GetFullPath(segmentDir);
            _controlDir = Path.GetFullPath(controlDir);
        }

        public static ScriptedVolumeProbe Same(TempStorageDir dir) =>
            new(dir.Options.SegmentDir, dir.Options.ControlDir);

        public bool TryResolve(string directoryPath, out StorageVolumeIdentity identity)
        {
            var full = Path.GetFullPath(directoryPath);
            if (string.Equals(full, _segmentDir, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, _controlDir, StringComparison.OrdinalIgnoreCase))
            {
                identity = new StorageVolumeIdentity("volume-shared");
                return true;
            }

            identity = default;
            return false;
        }
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

        public string Root { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-amb-" + Guid.NewGuid().ToString("N"));
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
