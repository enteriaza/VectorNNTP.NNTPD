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

/// <summary>Phase 2D: one control-ledger reservation per durable compaction-journal frame.</summary>
public sealed class CompactionJournalCapacityReservationTests
{
    [Fact]
    public void Codec_frame_lengths_match_encoded_frames()
    {
        Assert.Equal(36, ArticleJournalFrameCodec.CompactionBeginFrameLength);
        Assert.Equal(92, ArticleJournalFrameCodec.RelocationIntentFrameLength);
        Assert.Equal(48, ArticleJournalFrameCodec.RelocationWrittenFrameLength);
        Assert.Equal(20, ArticleJournalFrameCodec.CompactionCommittedFrameLength);
        Assert.Equal(36, ArticleJournalFrameCodec.CompactionRetiredFrameLength);

        var artId = ArticleId.FromMessageId("<frame@seg.test>"u8);
        var location = new StoredArticleLocation(new SegmentId(3), 8, 10);
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength,
            ArticleJournalFrameCodec.EncodeCompactionBegin(
                new JournalCompactionBeginRecord(1, 9, new SegmentId(3), 1)).Length);
        Assert.Equal(
            ArticleJournalFrameCodec.RelocationIntentFrameLength,
            ArticleJournalFrameCodec.EncodeRelocationIntent(
                new JournalRelocationIntentRecord(1, 9, 4, artId, 1, 10, location)).Length);
        Assert.Equal(
            ArticleJournalFrameCodec.RelocationWrittenFrameLength,
            ArticleJournalFrameCodec.EncodeRelocationWritten(
                new JournalRelocationWrittenRecord(1, 9, 4, location)).Length);
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionCommittedFrameLength,
            ArticleJournalFrameCodec.EncodeCompactionCommitted(
                new JournalCompactionCommittedRecord(1, 9)).Length);
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionRetiredFrameLength,
            ArticleJournalFrameCodec.EncodeCompactionRetired(
                new JournalCompactionRetiredRecord(1, 9, new SegmentId(3), 1)).Length);
    }

    [Fact]
    public void Ledger_compaction_journal_keys_do_not_collide_with_article_journal_or_index()
    {
        var ledger = new ProcessLocalCapacityLedger();
        ledger.TentativeAddJournal(100);
        ledger.BindJournalSequence(7, 100);
        ledger.AddRetainedIndexFrame(default, fileOffset: 88, snapshotGeneration: 0, ArticleIndexRecordCodec.RecordLength);
        Assert.True(ledger.TryAddCompactionJournalFrame(7, CompactionJournalFrameKind.Begin, 0, 36));
        Assert.True(ledger.TryAddCompactionJournalFrame(7, CompactionJournalFrameKind.Intent, 3, 92));
        Assert.Equal(100, ledger.JournalReservedBytes);
        Assert.Equal(128, ledger.CompactionJournalReservedBytes);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, ledger.IndexReservedBytes);

        Assert.True(ledger.ReleaseJournal(7));
        Assert.Equal(0, ledger.JournalReservedBytes);
        Assert.Equal(128, ledger.CompactionJournalReservedBytes);
        Assert.False(ledger.ReleaseJournal(7));

        Assert.True(ledger.ReleaseCompactionJournal(7));
        Assert.Equal(0, ledger.CompactionJournalReservedBytes);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, ledger.IndexReservedBytes);
        Assert.False(ledger.ReleaseCompactionJournal(7));
        Assert.False(ledger.ReleaseCompactionJournalFrame(7, CompactionJournalFrameKind.Intent, 3));
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, ledger.IndexReservedBytes);
    }

    [Fact]
    public async Task Begin_reserves_36_retains_it_and_idempotent_retry_does_not_double_reserve()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var (sourceId, _) = await AcceptCloseAsync(engine, CreateRecord("<cj-begin@seg.test>"));
        var before = engine.Journal.JournalPhysicalBytes;

        engine.TestHookBeforeRelocateArticle = _ => throw new IOException("stop-after-begin");
        await Assert.ThrowsAsync<IOException>(() =>
            engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None));

        Assert.Equal(ArticleJournalFrameCodec.CompactionBeginFrameLength, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalCompactionJournalFrameCount);
        Assert.Equal(before + ArticleJournalFrameCodec.CompactionBeginFrameLength, engine.Journal.JournalPhysicalBytes);

        var reserved = engine.ProcessLocalCompactionJournalReservedBytes;
        var length = engine.Journal.JournalPhysicalBytes;
        await Assert.ThrowsAsync<IOException>(() =>
            engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None));
        Assert.Equal(reserved, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(length, engine.Journal.JournalPhysicalBytes);
    }

    [Fact]
    public async Task Begin_rejection_does_not_append_or_leave_a_reservation()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity, maximumUtilization: 0.80, compactionHeadroom: 0.10);
        var (sourceId, _) = await AcceptCloseAsync(engine, CreateRecord("<cj-begin-deny@seg.test>"));
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(capacity.TotalBytes, 0.90);
        var pins = engine.ProcessLocalReservedBytes;
        capacity.UsedBytes = ceiling - pins - ArticleJournalFrameCodec.CompactionBeginFrameLength + 1;
        var before = engine.Journal.JournalPhysicalBytes;

        var denied = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Failed, denied.Outcome);
        Assert.Equal("compaction-begin-capacity", denied.Reason);
        Assert.Equal(0, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(before, engine.Journal.JournalPhysicalBytes);

        var again = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Failed, again.Outcome);
        Assert.Equal(before, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(0, engine.ProcessLocalCompactionJournalReservedBytes);
    }

    [Fact]
    public async Task Begin_append_exception_releases_only_that_attempt()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var (sourceId, _) = await AcceptCloseAsync(engine, CreateRecord("<cj-begin-throw@seg.test>"));
        var before = engine.Journal.JournalPhysicalBytes;
        engine.Journal.TestBeforeFrameAppend = () =>
        {
            engine.Journal.TestBeforeFrameAppend = null;
            throw new IOException("begin-append");
        };

        await Assert.ThrowsAsync<IOException>(() =>
            engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None));
        Assert.Equal(0, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(before, engine.Journal.JournalPhysicalBytes);

        engine.TestHookBeforeRelocateArticle = _ => throw new IOException("stop-after-begin");
        await Assert.ThrowsAsync<IOException>(() =>
            engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None));
        Assert.Equal(ArticleJournalFrameCodec.CompactionBeginFrameLength, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(before + ArticleJournalFrameCodec.CompactionBeginFrameLength, engine.Journal.JournalPhysicalBytes);
    }

    [Fact]
    public async Task Intent_rejection_does_not_append_and_repeated_rejection_does_not_grow_the_journal()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var record = CreateRecord("<cj-intent-deny@seg.test>");
        var (sourceId, generation) = await AcceptCloseAsync(engine, record);
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(capacity.TotalBytes, 0.90);
        var pins = engine.ProcessLocalReservedBytes;
        var begin = ArticleJournalFrameCodec.CompactionBeginFrameLength;
        var intent = ArticleJournalFrameCodec.RelocationIntentFrameLength;
        capacity.UsedBytes = ceiling - pins - begin - intent + 1;
        var before = engine.Journal.JournalPhysicalBytes;

        var denied = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Incomplete, denied.Outcome);
        Assert.Contains("compaction-journal-intent-capacity", denied.Reason, StringComparison.Ordinal);
        Assert.Equal(begin, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(before + begin, engine.Journal.JournalPhysicalBytes);
        Assert.True(engine.Journal.TryGetCompaction(denied.CompactionId, out var snap));
        Assert.Empty(snap.Relocations);

        var length = engine.Journal.JournalPhysicalBytes;
        var again = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Incomplete, again.Outcome);
        Assert.Equal(length, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(begin, engine.ProcessLocalCompactionJournalReservedBytes);
    }

    [Fact]
    public async Task Intent_reserves_92_and_a_later_destination_rejection_keeps_it()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var record = CreateRecord("<cj-intent-keep@seg.test>");
        var (sourceId, generation) = await AcceptCloseAsync(engine, record);
        await StopAfterBeginAsync(engine, sourceId);
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(capacity.TotalBytes, 0.90);
        var pins = engine.ProcessLocalReservedBytes;
        var intent = ArticleJournalFrameCodec.RelocationIntentFrameLength;
        var destination = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        capacity.UsedBytes = ceiling - pins - intent - destination + 1;
        var before = engine.Journal.JournalPhysicalBytes;
        Assert.True(engine.Journal.EnumerateOpenCompactions().Count() == 1);
        var compactionId = engine.Journal.EnumerateOpenCompactions().Single().Begin.CompactionId;

        var denied = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedCapacity, denied.Outcome);
        Assert.Equal("storage-capacity", denied.Reason);
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength + intent,
            engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(before + intent, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);

        var length = engine.Journal.JournalPhysicalBytes;
        var reserved = engine.ProcessLocalCompactionJournalReservedBytes;
        var again = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedCapacity, again.Outcome);
        Assert.Equal(length, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(reserved, engine.ProcessLocalCompactionJournalReservedBytes);
    }

    [Fact]
    public async Task Intent_append_exception_releases_only_the_new_frame_then_retry_retains_it()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var record = CreateRecord("<cj-intent-throw@seg.test>");
        var (sourceId, generation) = await AcceptCloseAsync(engine, record);
        await StopAfterBeginAsync(engine, sourceId);
        var compactionId = engine.Journal.EnumerateOpenCompactions().Single().Begin.CompactionId;
        var before = engine.Journal.JournalPhysicalBytes;
        engine.Journal.TestBeforeFrameAppend = () =>
        {
            engine.Journal.TestBeforeFrameAppend = null;
            throw new IOException("intent-append");
        };

        await Assert.ThrowsAsync<IOException>(() =>
            engine.RelocateArticleAsync(compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None));
        Assert.Equal(ArticleJournalFrameCodec.CompactionBeginFrameLength, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(before, engine.Journal.JournalPhysicalBytes);

        var relocated = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, relocated.Outcome);
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength
            + ArticleJournalFrameCodec.RelocationIntentFrameLength
            + ArticleJournalFrameCodec.RelocationWrittenFrameLength,
            engine.ProcessLocalCompactionJournalReservedBytes);
    }

    [Fact]
    public async Task New_relocation_id_reserves_another_92_and_idempotent_retry_does_not()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var first = CreateRecord("<cj-rel-a@seg.test>");
        var second = CreateRecord("<cj-rel-b@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(second, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(first.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        await StopAfterBeginAsync(engine, sourceId);
        var compactionId = engine.Journal.EnumerateOpenCompactions().Single().Begin.CompactionId;
        var begin = ArticleJournalFrameCodec.CompactionBeginFrameLength;
        var intent = ArticleJournalFrameCodec.RelocationIntentFrameLength;
        var written = ArticleJournalFrameCodec.RelocationWrittenFrameLength;

        Assert.Equal(
            ArticleRelocationOutcome.Relocated,
            (await engine.RelocateArticleAsync(
                compactionId, 1, sourceId, info.Generation, first.ArtId, CancellationToken.None)).Outcome);
        Assert.Equal(begin + intent + written, engine.ProcessLocalCompactionJournalReservedBytes);

        var again = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, info.Generation, first.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.IdempotentNoOp, again.Outcome);
        Assert.Equal(begin + intent + written, engine.ProcessLocalCompactionJournalReservedBytes);

        Assert.Equal(
            ArticleRelocationOutcome.Relocated,
            (await engine.RelocateArticleAsync(
                compactionId, 2, sourceId, info.Generation, second.ArtId, CancellationToken.None)).Outcome);
        Assert.Equal(begin + (2 * (intent + written)), engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(5, engine.ProcessLocalCompactionJournalFrameCount);
    }

    [Fact]
    public async Task Written_rejection_does_not_append_and_keeps_the_intent_reservation()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var record = CreateRecord("<cj-written-deny@seg.test>");
        var (sourceId, generation) = await AcceptCloseAsync(engine, record);
        await StopAfterBeginAsync(engine, sourceId);
        var compactionId = engine.Journal.EnumerateOpenCompactions().Single().Begin.CompactionId;
        var before = engine.Journal.JournalPhysicalBytes;
        engine.TestHookAfterCompactionCapacityReserved = () => capacity.UsedBytes = capacity.TotalBytes;

        var denied = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedCapacity, denied.Outcome);
        Assert.Equal("compaction-journal-written-capacity", denied.Reason);
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength
            + ArticleJournalFrameCodec.RelocationIntentFrameLength,
            engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(before + ArticleJournalFrameCodec.RelocationIntentFrameLength, engine.Journal.JournalPhysicalBytes);
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out var snap));
        Assert.Null(Assert.Single(snap.Relocations).Written);
        Assert.Equal(
            SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize),
            engine.ProcessLocalCompactionReservedBytes);

        var length = engine.Journal.JournalPhysicalBytes;
        var again = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedCapacity, again.Outcome);
        Assert.Equal(length, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength
            + ArticleJournalFrameCodec.RelocationIntentFrameLength,
            engine.ProcessLocalCompactionJournalReservedBytes);
    }

    [Fact]
    public async Task Written_success_retains_intent_and_written_and_retry_does_not_double_reserve()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var record = CreateRecord("<cj-written-ok@seg.test>");
        var (sourceId, generation) = await AcceptCloseAsync(engine, record);
        await StopAfterBeginAsync(engine, sourceId);
        var compactionId = engine.Journal.EnumerateOpenCompactions().Single().Begin.CompactionId;
        engine.TestHookAfterCompactionCapacityReserved = () => capacity.UsedBytes = capacity.TotalBytes;
        _ = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        engine.TestHookAfterCompactionCapacityReserved = null;
        capacity.UsedBytes = 0;
        var journalBefore = engine.ProcessLocalJournalReservedBytes;
        var indexBefore = engine.ProcessLocalIndexReservedBytes;

        var relocated = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, relocated.Outcome);
        var expected = ArticleJournalFrameCodec.CompactionBeginFrameLength
            + ArticleJournalFrameCodec.RelocationIntentFrameLength
            + ArticleJournalFrameCodec.RelocationWrittenFrameLength;
        Assert.Equal(expected, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(journalBefore, engine.ProcessLocalJournalReservedBytes);

        var retry = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.IdempotentNoOp, retry.Outcome);
        Assert.Equal(expected, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.True(indexBefore <= engine.ProcessLocalIndexReservedBytes);
    }

    [Fact]
    public async Task Written_append_exception_releases_only_the_48_byte_attempt()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var record = CreateRecord("<cj-written-throw@seg.test>");
        var (sourceId, generation) = await AcceptCloseAsync(engine, record);
        await StopAfterBeginAsync(engine, sourceId);
        var compactionId = engine.Journal.EnumerateOpenCompactions().Single().Begin.CompactionId;
        engine.TestRelocationFaultPoint = FileArticleStorageEngine.RelocationFaultPoint.AfterIntentBeforeAppend;
        await Assert.ThrowsAsync<IOException>(() =>
            engine.RelocateArticleAsync(compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None));
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength
            + ArticleJournalFrameCodec.RelocationIntentFrameLength,
            engine.ProcessLocalCompactionJournalReservedBytes);
        var before = engine.Journal.JournalPhysicalBytes;
        engine.Journal.TestBeforeFrameAppend = () =>
        {
            engine.Journal.TestBeforeFrameAppend = null;
            throw new IOException("written-append");
        };

        await Assert.ThrowsAsync<IOException>(() =>
            engine.RelocateArticleAsync(compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None));
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength
            + ArticleJournalFrameCodec.RelocationIntentFrameLength,
            engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(before, engine.Journal.JournalPhysicalBytes);

        var relocated = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, relocated.Outcome);
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength
            + ArticleJournalFrameCodec.RelocationIntentFrameLength
            + ArticleJournalFrameCodec.RelocationWrittenFrameLength,
            engine.ProcessLocalCompactionJournalReservedBytes);
    }

    [Fact]
    public async Task Committed_rejection_keeps_earlier_frames_and_success_retains_20()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var record = CreateRecord("<cj-commit@seg.test>");
        var (sourceId, generation) = await AcceptCloseAsync(engine, record);
        await StopAfterBeginAsync(engine, sourceId);
        var compactionId = engine.Journal.EnumerateOpenCompactions().Single().Begin.CompactionId;
        Assert.Equal(
            ArticleRelocationOutcome.Relocated,
            (await engine.RelocateArticleAsync(
                compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None)).Outcome);
        var beforeFrames = engine.ProcessLocalCompactionJournalReservedBytes;
        var beforeLength = engine.Journal.JournalPhysicalBytes;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(capacity.TotalBytes, 0.90);
        capacity.UsedBytes = ceiling
            - engine.ProcessLocalReservedBytes
            - ArticleJournalFrameCodec.CompactionCommittedFrameLength
            + 1;

        var denied = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Failed, denied.Outcome);
        Assert.Equal("compaction-committed-capacity", denied.Reason);
        Assert.Equal(beforeFrames, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(beforeLength, engine.Journal.JournalPhysicalBytes);

        capacity.UsedBytes = 0;
        var committed = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, committed.Outcome);
        Assert.Equal(
            beforeFrames + ArticleJournalFrameCodec.CompactionCommittedFrameLength,
            engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(beforeLength + ArticleJournalFrameCodec.CompactionCommittedFrameLength, engine.Journal.JournalPhysicalBytes);
    }

    [Fact]
    public async Task Retired_rejection_keeps_earlier_frames_and_success_retains_36()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var record = CreateRecord("<cj-retire@seg.test>");
        var sourceId = await AcceptCloseAsync(engine, record);
        var committed = await engine.CompactClosedSegmentAsync(sourceId.SourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, committed.Outcome);
        engine.CompleteUnreferencedExtentAccounting();
        var beforeFrames = engine.ProcessLocalCompactionJournalReservedBytes;
        var beforeLength = engine.Journal.JournalPhysicalBytes;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(capacity.TotalBytes, 0.90);
        capacity.UsedBytes = ceiling
            - engine.ProcessLocalReservedBytes
            - ArticleJournalFrameCodec.CompactionRetiredFrameLength
            + 1;

        var denied = await engine.RetireCompactedSegmentAsync(committed.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Failed, denied.Outcome);
        Assert.Equal("compaction-retired-capacity", denied.Reason);
        Assert.Equal(beforeFrames, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(beforeLength, engine.Journal.JournalPhysicalBytes);

        capacity.UsedBytes = 0;
        var retired = await engine.RetireCompactedSegmentAsync(committed.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retired.Outcome);
        Assert.Equal(
            beforeFrames + ArticleJournalFrameCodec.CompactionRetiredFrameLength,
            engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(beforeLength + ArticleJournalFrameCodec.CompactionRetiredFrameLength, engine.Journal.JournalPhysicalBytes);
    }

    [Fact]
    public async Task Checkpoint_releases_only_omitted_compactions_and_failure_keeps_them()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var openRecord = CreateRecord("<cj-open@seg.test>");
        var (openSource, _) = await AcceptCloseAsync(engine, openRecord);
        await StopAfterBeginAsync(engine, openSource);
        var openBytes = engine.ProcessLocalCompactionJournalReservedBytes;
        Assert.Equal(ArticleJournalFrameCodec.CompactionBeginFrameLength, openBytes);

        var retiredRecord = CreateRecord("<cj-retired-ckpt@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(retiredRecord, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(retiredRecord.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var committed = await engine.CompactClosedSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, committed.Outcome);
        var committedBytes = engine.ProcessLocalCompactionJournalReservedBytes;
        engine.Journal.CheckpointTestFault = _ => throw new IOException("checkpoint-before-install");
        var duringFailure = engine.ProcessLocalCompactionJournalReservedBytes;
        Assert.Throws<IOException>(() => engine.CheckpointTruncateCommitted());
        Assert.Equal(duringFailure, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
        engine.Journal.CheckpointTestFault = null;

        engine.CompleteUnreferencedExtentAccounting();
        var retired = await engine.RetireCompactedSegmentAsync(committed.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retired.Outcome);
        var beforeInstall = engine.ProcessLocalCompactionJournalReservedBytes;
        Assert.True(beforeInstall > committedBytes);

        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.Equal(openBytes, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);

        var journalBefore = engine.ProcessLocalJournalReservedBytes;
        var indexBefore = engine.ProcessLocalIndexReservedBytes;
        Assert.Equal(0, engine.CheckpointTruncateCommitted());
        Assert.Equal(openBytes, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(journalBefore, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(indexBefore, engine.ProcessLocalIndexReservedBytes);
    }

    [Fact]
    public async Task Committed_but_not_retired_stays_reserved_across_checkpoint()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var (sourceId, _) = await AcceptCloseAsync(engine, CreateRecord("<cj-committed-open@seg.test>"));
        var committed = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, committed.Outcome);
        var frames = engine.ProcessLocalCompactionJournalReservedBytes;
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength
            + ArticleJournalFrameCodec.RelocationIntentFrameLength
            + ArticleJournalFrameCodec.RelocationWrittenFrameLength
            + ArticleJournalFrameCodec.CompactionCommittedFrameLength,
            frames);

        _ = engine.CheckpointTruncateCommitted();
        Assert.Equal(frames, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.True(engine.Journal.TryGetCompaction(committed.CompactionId, out var snap));
        Assert.True(snap.Committed);
        Assert.Null(snap.Retired);
    }

    [Fact]
    public async Task Restart_reconstructs_every_physical_compaction_frame_once()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        ulong openId;
        ulong retiredId;
        {
            await using var engine = Open(dir, capacity);
            var (openSource, _) = await AcceptCloseAsync(engine, CreateRecord("<cj-rec-open@seg.test>"));
            await StopAfterBeginAsync(engine, openSource);
            openId = engine.Journal.EnumerateOpenCompactions().Single().Begin.CompactionId;

            var first = CreateRecord("<cj-rec-a@seg.test>");
            var second = CreateRecord("<cj-rec-b@seg.test>");
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(second, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(first.ArtId, out var meta));
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
            var multi = await engine.CompactClosedSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
            Assert.Equal(ArticleCompactionOutcome.Committed, multi.Outcome);
            retiredId = multi.CompactionId;
            engine.CompleteUnreferencedExtentAccounting();
            Assert.Equal(
                ArticleSegmentRetirementOutcome.Retired,
                (await engine.RetireCompactedSegmentAsync(retiredId, CancellationToken.None)).Outcome);
        }

        await using (var restarted = Open(dir, capacity))
        {
            var begin = ArticleJournalFrameCodec.CompactionBeginFrameLength;
            var oneRelocation = ArticleJournalFrameCodec.RelocationIntentFrameLength
                + ArticleJournalFrameCodec.RelocationWrittenFrameLength;
            var retiredTail = ArticleJournalFrameCodec.CompactionCommittedFrameLength
                + ArticleJournalFrameCodec.CompactionRetiredFrameLength;
            Assert.Equal(begin + begin + (2 * oneRelocation) + retiredTail, restarted.ProcessLocalCompactionJournalReservedBytes);
            Assert.True(restarted.Journal.TryGetCompaction(openId, out var open));
            Assert.Empty(open.Relocations);
            Assert.True(restarted.Journal.TryGetCompaction(retiredId, out var retired));
            Assert.Equal(2, retired.Relocations.Count);
            Assert.All(retired.Relocations, static relocation => Assert.NotNull(relocation.Written));
            Assert.NotNull(retired.Retired);
        }
    }

    [Fact]
    public async Task Restart_after_checkpoint_reconstructs_only_retained_compactions()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        {
            await using var engine = Open(dir, capacity);
            var (openSource, _) = await AcceptCloseAsync(engine, CreateRecord("<cj-keep@seg.test>"));
            await StopAfterBeginAsync(engine, openSource);
            var retiredSource = await AcceptCloseSeparateAsync(engine, CreateRecord("<cj-drop@seg.test>"));
            var committed = await engine.CompactClosedSegmentAsync(retiredSource, CancellationToken.None);
            Assert.Equal(ArticleCompactionOutcome.Committed, committed.Outcome);
            engine.CompleteUnreferencedExtentAccounting();
            Assert.Equal(
                ArticleSegmentRetirementOutcome.Retired,
                (await engine.RetireCompactedSegmentAsync(committed.CompactionId, CancellationToken.None)).Outcome);
            Assert.True(engine.CheckpointTruncateCommitted() > 0);
            Assert.Equal(
                ArticleJournalFrameCodec.CompactionBeginFrameLength,
                engine.ProcessLocalCompactionJournalReservedBytes);
        }

        await using var restarted = Open(dir, capacity);
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength,
            restarted.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(1, restarted.ProcessLocalCompactionJournalFrameCount);
        Assert.Single(restarted.Journal.EnumerateCompactions());
    }

    [Fact]
    public async Task Headroom_admits_a_frame_maximum_utilization_rejects_and_the_reservation_counts_later()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = Open(dir, capacity, maximumUtilization: 0.80, compactionHeadroom: 0.10);
        var (sourceId, _) = await AcceptCloseAsync(engine, CreateRecord("<cj-headroom@seg.test>"));
        var pins = engine.ProcessLocalReservedBytes;
        var begin = ArticleJournalFrameCodec.CompactionBeginFrameLength;
        var articleCeiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(capacity.TotalBytes, 0.80);
        var compactionCeiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(capacity.TotalBytes, 0.90);
        capacity.UsedBytes = articleCeiling - pins - begin + 1;
        Assert.False(ProcessLocalCapacityLedger.WouldFit(
            capacity.UsedBytes, pins, 0, capacity.TotalBytes, begin, 0.80));
        Assert.True(ProcessLocalCapacityLedger.WouldFit(
            capacity.UsedBytes, pins, 0, capacity.TotalBytes, begin, 0.90));

        engine.TestHookBeforeRelocateArticle = _ => throw new IOException("stop-after-begin");
        await Assert.ThrowsAsync<IOException>(() =>
            engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None));
        Assert.Equal(begin, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.False(ProcessLocalCapacityLedger.WouldFit(
            capacity.UsedBytes,
            pins + begin,
            0,
            capacity.TotalBytes,
            SegmentRecordCodec.MinimumRecordLength,
            0.80));

        engine.TestHookBeforeRelocateArticle = null;
        capacity.UsedBytes = 0;
        var next = await engine.AcceptAsync(CreateRecord("<cj-headroom-next@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, next.Outcome);
        var blockedSource = await AcceptCloseSeparateAsync(engine, CreateRecord("<cj-headroom-block@seg.test>"));
        capacity.UsedBytes = compactionCeiling - engine.ProcessLocalReservedBytes - begin + 1;
        var blocked = await engine.CompactClosedSegmentAsync(blockedSource, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Failed, blocked.Outcome);
        Assert.Equal("compaction-begin-capacity", blocked.Reason);
    }

    [Fact]
    public async Task Shared_volume_aggregates_compaction_journal_bytes_and_split_volume_keeps_them_on_control()
    {
        using var sharedDir = TempStorageDir.Create();
        var shared = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using (var engine = FileArticleStorageEngine.Open(
            WithCapacity(sharedDir.Options),
            capacityReader: shared,
            volumeProbe: ScriptedVolumeProbe.SameVolume(sharedDir)))
        {
            var (sourceId, _) = await AcceptCloseAsync(engine, CreateRecord("<cj-shared@seg.test>"));
            await StopAfterBeginAsync(engine, sourceId);
            Assert.Same(engine.SegmentCapacity, engine.ControlCapacity);
            Assert.Equal(
                ArticleJournalFrameCodec.CompactionBeginFrameLength,
                engine.SegmentCapacity!.WithLedger(static ledger => ledger.CompactionJournalReservedBytes));
            Assert.True(engine.ProcessLocalReservedBytes >= engine.ProcessLocalCompactionJournalReservedBytes);
        }

        using var splitDir = TempStorageDir.Create();
        var segment = new MutableCapacityReader(total: 1_000_000, used: 0);
        var control = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var split = FileArticleStorageEngine.Open(
            WithCapacity(splitDir.Options),
            capacityReader: segment,
            volumeProbe: ScriptedVolumeProbe.SplitVolumes(splitDir),
            controlCapacityReader: control);
        var (splitSource, _) = await AcceptCloseAsync(split, CreateRecord("<cj-split@seg.test>"));
        await StopAfterBeginAsync(split, splitSource);
        Assert.Equal(0, split.SegmentCapacity!.WithLedger(static ledger => ledger.CompactionJournalReservedBytes));
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength,
            split.ControlCapacity!.WithLedger(static ledger => ledger.CompactionJournalReservedBytes));
        Assert.Equal(
            ArticleJournalFrameCodec.CompactionBeginFrameLength,
            split.ProcessLocalCompactionJournalReservedBytes);
    }

    [Fact]
    public async Task Capacity_disabled_does_not_reserve_compaction_journal_frames()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 100, used: 100);
        await using var engine = FileArticleStorageEngine.Open(
            dir.Options with { CapacityAdmissionEnabled = false },
            capacityReader: capacity);
        var (sourceId, _) = await AcceptCloseAsync(engine, CreateRecord("<cj-off@seg.test>"));
        var committed = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, committed.Outcome);
        Assert.Equal(0, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCompactionJournalFrameCount);
    }

    private static FileArticleStorageEngine Open(
        TempStorageDir dir,
        MutableCapacityReader capacity,
        double maximumUtilization = 0.80,
        double compactionHeadroom = 0.10) =>
        FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization, compactionHeadroom),
            capacityReader: capacity);

    private static ArticleStorageRuntimeOptions WithCapacity(
        ArticleStorageRuntimeOptions options,
        double maximumUtilization = 0.80,
        double compactionHeadroom = 0.10) =>
        options with
        {
            CapacityAdmissionEnabled = true,
            CapacityMaximumUtilization = maximumUtilization,
            CapacityCompactionHeadroom = compactionHeadroom,
        };

    private static async Task StopAfterBeginAsync(FileArticleStorageEngine engine, SegmentId sourceId)
    {
        engine.TestHookBeforeRelocateArticle = _ => throw new IOException("stop-after-begin");
        await Assert.ThrowsAsync<IOException>(() =>
            engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None));
        engine.TestHookBeforeRelocateArticle = null;
    }

    private static async Task<(SegmentId SourceId, ulong Generation)> AcceptCloseAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        return (sourceId, info.Generation);
    }

    private static async Task<SegmentId> AcceptCloseSeparateAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        return meta.Location.SegmentId;
    }

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: compaction-journal\r\n");
        _ = builder.Append("\r\nline1\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class ScriptedVolumeProbe : IStorageVolumeProbe
    {
        private readonly string _segmentDir;
        private readonly string _controlDir;
        private readonly bool _same;

        private ScriptedVolumeProbe(string segmentDir, string controlDir, bool same)
        {
            _segmentDir = Path.GetFullPath(segmentDir);
            _controlDir = Path.GetFullPath(controlDir);
            _same = same;
        }

        public static ScriptedVolumeProbe SameVolume(TempStorageDir dir) =>
            new(dir.Options.SegmentDir, dir.Options.ControlDir, same: true);

        public static ScriptedVolumeProbe SplitVolumes(TempStorageDir dir) =>
            new(dir.Options.SegmentDir, dir.Options.ControlDir, same: false);

        public bool TryResolve(string directoryPath, out StorageVolumeIdentity identity)
        {
            var full = Path.GetFullPath(directoryPath);
            if (string.Equals(full, _segmentDir, StringComparison.OrdinalIgnoreCase))
            {
                identity = new StorageVolumeIdentity("volume-segment");
                return true;
            }

            if (string.Equals(full, _controlDir, StringComparison.OrdinalIgnoreCase))
            {
                identity = new StorageVolumeIdentity(_same ? "volume-segment" : "volume-control");
                return true;
            }

            identity = default;
            return false;
        }
    }

    private sealed class MutableCapacityReader : IStorageCapacityReader
    {
        public MutableCapacityReader(long total, long used)
        {
            TotalBytes = total;
            UsedBytes = used;
        }

        public long TotalBytes { get; set; }

        public long UsedBytes { get; set; }

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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-cj-" + Guid.NewGuid().ToString("N"));
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
                // best-effort
            }
        }
    }
}
