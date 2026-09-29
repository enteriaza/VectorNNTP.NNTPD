using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>Phase 4B.2: compaction journal frames, replay, checkpoint isolation, recovery.</summary>
public sealed class FileArticleCompactionJournalTests
{
    [Fact]
    public async Task A_CompactionBegin_RoundTrip()
    {
        using var dir = TempControlDir.Create();
        var begin = new JournalCompactionBeginRecord(1, 7, new SegmentId(3), 11);
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendCompactionBeginAsync(begin, CancellationToken.None));
            Assert.Equal(0, journal.OutstandingRecoverableBytes);
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryGetCompaction(7, out var snap));
        Assert.Equal(begin.SourceSegmentId, snap.Begin.SourceSegmentId);
        Assert.Equal(begin.SourceGeneration, snap.Begin.SourceGeneration);
        Assert.Equal(0, journalB.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task B_RelocationIntent_RoundTrip()
    {
        using var dir = TempControlDir.Create();
        var art = CreateRecord("<cj-b@seg.test>");
        var intent = new JournalRelocationIntentRecord(
            1, 1, 1, art.ArtId, art.ArtHash, art.ArtSize,
            new StoredArticleLocation(new SegmentId(9), 0, art.ArtSize + 32));
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 1, new SegmentId(9), 1), CancellationToken.None));
            Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendRelocationIntentAsync(intent, CancellationToken.None));
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryGetCompaction(1, out var snap));
        var recovered = Assert.Single(snap.Relocations);
        Assert.Equal(intent.ArtId, recovered.Intent.ArtId);
        Assert.Equal(intent.ExpectedSourceLocation, recovered.Intent.ExpectedSourceLocation);
        Assert.Null(recovered.Written);
    }

    [Fact]
    public async Task C_RelocationWritten_RoundTrip()
    {
        using var dir = TempControlDir.Create();
        var art = CreateRecord("<cj-c@seg.test>");
        var src = new StoredArticleLocation(new SegmentId(1), 0, 100);
        var dest = new StoredArticleLocation(new SegmentId(2), 40, 100);
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            _ = journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 5, new SegmentId(1), 2), CancellationToken.None);
            _ = journal.AppendRelocationIntentAsync(
                new JournalRelocationIntentRecord(1, 5, 3, art.ArtId, art.ArtHash, art.ArtSize, src),
                CancellationToken.None);
            Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendRelocationWrittenAsync(
                    new JournalRelocationWrittenRecord(1, 5, 3, dest),
                    CancellationToken.None));
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryGetCompaction(5, out var snap));
        Assert.Equal(dest, Assert.Single(snap.Relocations).Written!.Value.DestinationLocation);
    }

    [Fact]
    public async Task D_CompactionCommitted_RoundTrip()
    {
        using var dir = TempControlDir.Create();
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            _ = journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 2, new SegmentId(1), 1), CancellationToken.None);
            Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendCompactionCommittedAsync(new JournalCompactionCommittedRecord(1, 2), CancellationToken.None));
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryGetCompaction(2, out var snap));
        Assert.True(snap.Committed);
    }

    [Fact]
    public async Task E_CompactionRetired_RoundTrip()
    {
        using var dir = TempControlDir.Create();
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            _ = journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 4, new SegmentId(8), 6), CancellationToken.None);
            _ = journal.AppendCompactionCommittedAsync(new JournalCompactionCommittedRecord(1, 4), CancellationToken.None);
            Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendCompactionRetiredAsync(
                    new JournalCompactionRetiredRecord(1, 4, new SegmentId(8), 6),
                    CancellationToken.None));
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryGetCompaction(4, out var snap));
        Assert.NotNull(snap.Retired);
        Assert.Equal(6UL, snap.Retired!.Value.ExpectedGeneration);
    }

    [Fact]
    public async Task F_InvalidCrc_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        var path = Path.Combine(dir.Options.ControlDir, FileArticleJournal.JournalFileName);
        var frame = ArticleJournalFrameCodec.EncodeCompactionBegin(
            new JournalCompactionBeginRecord(1, 1, new SegmentId(1), 1));
        frame[^1] ^= 0xFF;
        // Trailing bytes after a complete corrupt frame ⇒ fail closed (Phase 2A).
        var bytes = new byte[frame.Length + 4];
        frame.CopyTo(bytes, 0);
        bytes[^1] = 0x5A;
        await File.WriteAllBytesAsync(path, bytes);
        Assert.Throws<ArticleJournalCorruptException>(() => FileArticleJournal.Open(dir.Options));
    }

    [Fact]
    public async Task G_IncompleteFinalFrame_Truncated()
    {
        using var dir = TempControlDir.Create();
        var path = Path.Combine(dir.Options.ControlDir, FileArticleJournal.JournalFileName);
        var frame = ArticleJournalFrameCodec.EncodeCompactionBegin(
            new JournalCompactionBeginRecord(1, 9, new SegmentId(1), 1));
        File.WriteAllBytes(path, frame.AsSpan(0, frame.Length - 3).ToArray());
        using var journal = FileArticleJournal.Open(dir.Options);
        Assert.False(journal.TryGetCompaction(9, out _));
    }

    [Fact]
    public async Task H_IntentWithoutBegin_Rejected()
    {
        using var dir = TempControlDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        var art = CreateRecord("<cj-h@seg.test>");
        Assert.Equal(JournalAppendOutcome.Rejected, await journal.AppendRelocationIntentAsync(
                new JournalRelocationIntentRecord(
                    1, 1, 1, art.ArtId, art.ArtHash, art.ArtSize,
                    new StoredArticleLocation(new SegmentId(1), 0, 10)),
                CancellationToken.None));
    }

    [Fact]
    public async Task I_WrittenWithoutIntent_Rejected()
    {
        using var dir = TempControlDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        _ = journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 1, new SegmentId(1), 1), CancellationToken.None);
        Assert.Equal(JournalAppendOutcome.Rejected, await journal.AppendRelocationWrittenAsync(
                new JournalRelocationWrittenRecord(1, 1, 1, new StoredArticleLocation(new SegmentId(2), 0, 10)),
                CancellationToken.None));
    }

    [Fact]
    public async Task J_DuplicateIdenticalIntent_Idempotent()
    {
        using var dir = TempControlDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        var art = CreateRecord("<cj-j@seg.test>");
        var intent = new JournalRelocationIntentRecord(
            1, 1, 1, art.ArtId, art.ArtHash, art.ArtSize,
            new StoredArticleLocation(new SegmentId(1), 0, 10));
        _ = journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 1, new SegmentId(1), 1), CancellationToken.None);
        Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendRelocationIntentAsync(intent, CancellationToken.None));
        Assert.Equal(JournalAppendOutcome.IdempotentNoOp, await journal.AppendRelocationIntentAsync(intent, CancellationToken.None));
    }

    [Fact]
    public async Task K_ConflictingIntent_Rejected()
    {
        using var dir = TempControlDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        var art = CreateRecord("<cj-k@seg.test>");
        _ = journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 1, new SegmentId(1), 1), CancellationToken.None);
        _ = journal.AppendRelocationIntentAsync(
            new JournalRelocationIntentRecord(
                1, 1, 1, art.ArtId, art.ArtHash, art.ArtSize,
                new StoredArticleLocation(new SegmentId(1), 0, 10)),
            CancellationToken.None);
        Assert.Equal(JournalAppendOutcome.Conflict, await journal.AppendRelocationIntentAsync(
                new JournalRelocationIntentRecord(
                    1, 1, 1, art.ArtId, art.ArtHash, art.ArtSize,
                    new StoredArticleLocation(new SegmentId(1), 99, 10)),
                CancellationToken.None));
    }

    [Fact]
    public async Task L_DuplicateIdenticalWritten_Idempotent()
    {
        using var dir = TempControlDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        var art = CreateRecord("<cj-l@seg.test>");
        var dest = new StoredArticleLocation(new SegmentId(2), 0, 10);
        _ = journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 1, new SegmentId(1), 1), CancellationToken.None);
        _ = journal.AppendRelocationIntentAsync(
            new JournalRelocationIntentRecord(
                1, 1, 1, art.ArtId, art.ArtHash, art.ArtSize,
                new StoredArticleLocation(new SegmentId(1), 0, 10)),
            CancellationToken.None);
        var written = new JournalRelocationWrittenRecord(1, 1, 1, dest);
        Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendRelocationWrittenAsync(written, CancellationToken.None));
        Assert.Equal(JournalAppendOutcome.IdempotentNoOp, await journal.AppendRelocationWrittenAsync(written, CancellationToken.None));
    }

    [Fact]
    public async Task M_ConflictingWritten_Rejected()
    {
        using var dir = TempControlDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        var art = CreateRecord("<cj-m@seg.test>");
        _ = journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 1, new SegmentId(1), 1), CancellationToken.None);
        _ = journal.AppendRelocationIntentAsync(
            new JournalRelocationIntentRecord(
                1, 1, 1, art.ArtId, art.ArtHash, art.ArtSize,
                new StoredArticleLocation(new SegmentId(1), 0, 10)),
            CancellationToken.None);
        _ = journal.AppendRelocationWrittenAsync(
            new JournalRelocationWrittenRecord(1, 1, 1, new StoredArticleLocation(new SegmentId(2), 0, 10)),
            CancellationToken.None);
        Assert.Equal(JournalAppendOutcome.Conflict, await journal.AppendRelocationWrittenAsync(
                new JournalRelocationWrittenRecord(1, 1, 1, new StoredArticleLocation(new SegmentId(2), 50, 10)),
                CancellationToken.None));
    }

    [Fact]
    public async Task N_CompactionBegin_SourceGenerationConflict()
    {
        using var dir = TempControlDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        _ = journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 1, new SegmentId(1), 1), CancellationToken.None);
        Assert.Equal(JournalAppendOutcome.Conflict, await journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 1, new SegmentId(1), 2), CancellationToken.None));
    }

    [Fact]
    public async Task OPQ_Replay_ReconstructsIntentsAndWritten()
    {
        using var dir = TempControlDir.Create();
        var a = CreateRecord("<cj-opq1@seg.test>");
        var b = CreateRecord("<cj-opq2@seg.test>");
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            _ = journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 10, new SegmentId(1), 1), CancellationToken.None);
            _ = journal.AppendRelocationIntentAsync(
                new JournalRelocationIntentRecord(
                    1, 10, 1, a.ArtId, a.ArtHash, a.ArtSize,
                    new StoredArticleLocation(new SegmentId(1), 0, 20)),
                CancellationToken.None);
            _ = journal.AppendRelocationIntentAsync(
                new JournalRelocationIntentRecord(
                    1, 10, 2, b.ArtId, b.ArtHash, b.ArtSize,
                    new StoredArticleLocation(new SegmentId(1), 20, 20)),
                CancellationToken.None);
            _ = journal.AppendRelocationWrittenAsync(
                new JournalRelocationWrittenRecord(1, 10, 1, new StoredArticleLocation(new SegmentId(2), 0, 20)),
                CancellationToken.None);
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryGetCompaction(10, out var snap));
        Assert.Equal(2, snap.Relocations.Count);
        Assert.NotNull(snap.Relocations[0].Written);
        Assert.Null(snap.Relocations[1].Written);
    }

    [Fact]
    public async Task RSTUVWXYZ_Recovery_IndexCases()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<cj-rec@seg.test>");
        StoredArticleLocation sourceLoc;
        StoredArticleLocation destLoc;
        ulong compactionId;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            var accept = await engine.AcceptAsync(record, CancellationToken.None);
            Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            sourceLoc = meta.Location;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.Segments.TryGetSegmentInfo(sourceLoc.SegmentId, out var srcInfo));

            // Append a destination copy via segment store (simulates prior RelocateArticle append).
            var artData = record.ArtData;
            destLoc = await (await engine.Segments.GetActiveAppenderAsync(CancellationToken.None))
                .AppendAsync(artData, CancellationToken.None);

            compactionId = engine.Journal.AllocateCompactionId();
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engine.Journal.AppendCompactionBeginAsync(
                    new JournalCompactionBeginRecord(1, compactionId, sourceLoc.SegmentId, srcInfo.Generation),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engine.Journal.AppendRelocationIntentAsync(
                    new JournalRelocationIntentRecord(
                        1, compactionId, 1, record.ArtId, record.ArtHash, record.ArtSize, sourceLoc),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engine.Journal.AppendRelocationWrittenAsync(
                    new JournalRelocationWrittenRecord(1, compactionId, 1, destLoc),
                    CancellationToken.None));
        }

        // U: still at source → recovery retries TryRelocate
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            await engine.RecoverAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var after));
            Assert.Equal(ArticleStorageState.Present, after.State);
            Assert.Equal(destLoc, after.Location);
            Assert.True(engine.TryRead(record.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        }

        // T: already at destination → complete
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            await engine.RecoverAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var after));
            Assert.Equal(destLoc, after.Location);
        }
    }

    [Fact]
    public async Task V_IndexEvicted_DoesNotResurrect()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<cj-v@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceLoc = meta.Location;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceLoc.SegmentId, out var srcInfo));
        var destLoc = await (await engine.Segments.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(record.ArtData, CancellationToken.None);

        var compactionId = engine.Journal.AllocateCompactionId();
        _ = await engine.Journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, compactionId, sourceLoc.SegmentId, srcInfo.Generation),
            CancellationToken.None);
        _ = await engine.Journal.AppendRelocationIntentAsync(
            new JournalRelocationIntentRecord(
                1, compactionId, 1, record.ArtId, record.ArtHash, record.ArtSize, sourceLoc),
            CancellationToken.None);
        _ = await engine.Journal.AppendRelocationWrittenAsync(
            new JournalRelocationWrittenRecord(1, compactionId, 1, destLoc),
            CancellationToken.None);

        Assert.True(engine.TryEvict(record.ArtId));
        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Evicted, after.State);
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task W_IndexInvalid_DoesNotResurrect()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<cj-w@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceLoc = meta.Location;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceLoc.SegmentId, out var srcInfo));
        var destLoc = await (await engine.Segments.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(record.ArtData, CancellationToken.None);

        var compactionId = engine.Journal.AllocateCompactionId();
        _ = await engine.Journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, compactionId, sourceLoc.SegmentId, srcInfo.Generation),
            CancellationToken.None);
        _ = await engine.Journal.AppendRelocationIntentAsync(
            new JournalRelocationIntentRecord(
                1, compactionId, 1, record.ArtId, record.ArtHash, record.ArtSize, sourceLoc),
            CancellationToken.None);
        _ = await engine.Journal.AppendRelocationWrittenAsync(
            new JournalRelocationWrittenRecord(1, compactionId, 1, destLoc),
            CancellationToken.None);

        Assert.True(engine.TryInvalidate(record.ArtId));
        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Invalid, after.State);
    }

    [Fact]
    public async Task X_IndexMovedElsewhere_Abandoned()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<cj-x@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceLoc = meta.Location;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceLoc.SegmentId, out var srcInfo));
        var staleDest = await (await engine.Segments.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(record.ArtData, CancellationToken.None);
        var realDest = await (await engine.Segments.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(record.ArtData, CancellationToken.None);

        var compactionId = engine.Journal.AllocateCompactionId();
        _ = await engine.Journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, compactionId, sourceLoc.SegmentId, srcInfo.Generation),
            CancellationToken.None);
        _ = await engine.Journal.AppendRelocationIntentAsync(
            new JournalRelocationIntentRecord(
                1, compactionId, 1, record.ArtId, record.ArtHash, record.ArtSize, sourceLoc),
            CancellationToken.None);
        _ = await engine.Journal.AppendRelocationWrittenAsync(
            new JournalRelocationWrittenRecord(1, compactionId, 1, staleDest),
            CancellationToken.None);

        Assert.Equal(
            ArticleRelocateOutcome.Relocated,
            engine.Index.TryRelocate(record.ArtId, sourceLoc, realDest, record.ArtHash, record.ArtSize));

        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(realDest, after.Location);
    }

    [Fact]
    public async Task YZ_CommittedAndRetired_Replay()
    {
        using var dir = TempControlDir.Create();
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            _ = journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 3, new SegmentId(1), 1), CancellationToken.None);
            _ = journal.AppendCompactionCommittedAsync(new JournalCompactionCommittedRecord(1, 3), CancellationToken.None);
            _ = journal.AppendCompactionRetiredAsync(
                new JournalCompactionRetiredRecord(1, 3, new SegmentId(1), 1),
                CancellationToken.None);
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryGetCompaction(3, out var snap));
        Assert.True(snap.Committed);
        Assert.NotNull(snap.Retired);
        Assert.Empty(journalB.EnumerateOpenCompactions());
    }

    [Fact]
    public async Task AA_AcceptRecovery_Unchanged()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<cj-aa@seg.test>");
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            Assert.Single(engine.Journal.EnumerateIncomplete());
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Empty(engineB.Journal.EnumerateIncomplete());
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task AC_CompactionDoesNotAffectOutstandingRecoverableBytes()
    {
        using var dir = TempControlDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        var before = journal.OutstandingRecoverableBytes;
        _ = journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 1, new SegmentId(1), 1), CancellationToken.None);
        Assert.Equal(before, journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task AD_AbandonedDestination_CountsAsDead()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<cj-ad@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceLoc = meta.Location;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceLoc.SegmentId, out var srcInfo));
        var destLoc = await (await engine.Segments.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(record.ArtData, CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(destLoc.SegmentId, out var destBefore));
        Assert.True(destBefore.LiveBytes >= destLoc.Length);

        var compactionId = engine.Journal.AllocateCompactionId();
        _ = await engine.Journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, compactionId, sourceLoc.SegmentId, srcInfo.Generation),
            CancellationToken.None);
        _ = await engine.Journal.AppendRelocationIntentAsync(
            new JournalRelocationIntentRecord(
                1, compactionId, 1, record.ArtId, record.ArtHash, record.ArtSize, sourceLoc),
            CancellationToken.None);
        _ = await engine.Journal.AppendRelocationWrittenAsync(
            new JournalRelocationWrittenRecord(1, compactionId, 1, destLoc),
            CancellationToken.None);

        Assert.True(engine.TryEvict(record.ArtId));
        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(destLoc.SegmentId, out var destAfter));
        Assert.True(destAfter.DeadBytes >= destLoc.Length);
    }

    [Fact]
    public async Task AE_Restart_RebuildsFromIndex()
    {
        using var dir = TempStorageDir.Create();
        var keep = CreateRecord("<cj-ae-keep@seg.test>");
        var drop = CreateRecord("<cj-ae-drop@seg.test>");
        long size;
        StoredArticleLocation dropLoc;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(drop.ArtId, out var meta));
            dropLoc = meta.Location;
            Assert.True(engine.TryEvict(drop.ArtId));
            Assert.True(engine.Segments.TryGetSegmentInfo(dropLoc.SegmentId, out var info));
            size = info.SizeBytes;
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        Assert.True(engineB.Segments.TryGetSegmentInfo(dropLoc.SegmentId, out var after));
        Assert.Equal(size, after.SizeBytes);
        Assert.Equal(dropLoc.Length, after.DeadBytes);
        Assert.Equal(size - dropLoc.Length, after.LiveBytes);
    }

    [Fact]
    public async Task S_CorruptWrittenDestination_FailsClosed()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<cj-s@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceLoc = meta.Location;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceLoc.SegmentId, out var srcInfo));
        var destLoc = await (await engine.Segments.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(record.ArtData, CancellationToken.None);

        // Durable Written pointing at a non-proof location (offset past the real record).
        var bogusDest = new StoredArticleLocation(destLoc.SegmentId, destLoc.Offset + 1, destLoc.Length);

        var compactionId = engine.Journal.AllocateCompactionId();
        _ = await engine.Journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, compactionId, sourceLoc.SegmentId, srcInfo.Generation),
            CancellationToken.None);
        _ = await engine.Journal.AppendRelocationIntentAsync(
            new JournalRelocationIntentRecord(
                1, compactionId, 1, record.ArtId, record.ArtHash, record.ArtSize, sourceLoc),
            CancellationToken.None);
        _ = await engine.Journal.AppendRelocationWrittenAsync(
            new JournalRelocationWrittenRecord(1, compactionId, 1, bogusDest),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RecoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AB_Checkpoint_RetainsOpenCompaction()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<cj-ab@seg.test>");
        using var journal = FileArticleJournal.Open(dir.Options);
        Assert.True(journal.TryAppendNewAccept(
            record.ArtId, record.ArtHash, record.ArtSize, DateTimeOffset.UtcNow, record.ArtData, out var accept, out _));
        _ = journal.AppendPhysicalWrittenAsync(
            new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(1), 0, record.ArtSize)),
            CancellationToken.None);
        _ = journal.AppendIndexCommittedAsync(new JournalIndexCommittedRecord(1, accept.Sequence), CancellationToken.None);
        _ = journal.AppendCompactionBeginAsync(new JournalCompactionBeginRecord(1, 42, new SegmentId(7), 3), CancellationToken.None);
        _ = journal.CheckpointTruncateCommitted();
        Assert.True(journal.TryGetCompaction(42, out _));
        Assert.Equal(0, journal.OutstandingRecoverableBytes);
    }

    private static ArticleRecord CreateRecord(string messageId, string body = "line1\r\nline2\r\n")
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: compaction-journal\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class TempControlDir : IDisposable
    {
        private TempControlDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string Root { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempControlDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-cj-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            Directory.CreateDirectory(control);
            return new TempControlDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: control,
                    SegmentDir: Path.Combine(root, "cache"),
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
            catch
            {
                // best-effort
            }
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-cj-eng-" + Guid.NewGuid().ToString("N"));
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
            catch
            {
                // best-effort
            }
        }
    }
}
