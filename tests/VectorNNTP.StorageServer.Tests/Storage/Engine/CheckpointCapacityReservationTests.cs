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

/// <summary>Checkpoint temporary-file reservations on the process-local capacity ledger.</summary>
public sealed class CheckpointCapacityReservationTests
{
    [Fact]
    public void Ledger_IncludesCheckpoint_AtExactBoundary_AndOneByteOver()
    {
        var ledger = new ProcessLocalCapacityLedger();
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(1_000, 0.80);
        Assert.Equal(800, ceiling);
        Assert.True(ProcessLocalCapacityLedger.WouldFit(780, 0, 0, 1_000, 20, 0.80));
        Assert.False(ProcessLocalCapacityLedger.WouldFit(781, 0, 0, 1_000, 20, 0.80));

        var id = ledger.ReserveCheckpoint(20);
        ledger.TentativeAddArticle(5);
        ledger.ReserveCompaction(3, 1, 10);
        Assert.Equal(35, ledger.ReservedBytes);
        Assert.False(ledger.WouldFit(780, 1_000, 0, 0.80));
        Assert.True(ledger.WouldFit(765, 1_000, 0, 0.80));
        Assert.True(ledger.WouldFit(780, 1_000, 85, 0.90));
        Assert.False(ledger.WouldFit(780, 1_000, 86, 0.90));
        Assert.Equal(
            16,
            ProcessLocalCapacityLedger.ComputeAdmissionRecoveryTargetBytes(
                780, 5, 10, 1_000, 0.80, 1, checkpointReservedBytes: 20));

        Assert.True(ledger.TryIncreaseCheckpoint(id, 1));
        Assert.Equal(36, ledger.ReservedBytes);
        Assert.True(ledger.ReleaseCheckpoint(id));
        Assert.Equal(15, ledger.ReservedBytes);
        Assert.False(ledger.ReleaseCheckpoint(id));
    }

    [Fact]
    public async Task Journal_ReservesExactImage_IncludingFencePhysicalWrittenAndCompaction()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        var tiny = await AppendAcceptAsync(journal, "<ckpt-1b@example.test>", [0x7A]);
        var location = new StoredArticleLocation(new SegmentId(4), 0, tiny.ArtSize);
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, tiny.Sequence, location),
                CancellationToken.None));

        var retiredId = journal.AllocateCompactionId();
        var retired = new JournalCompactionBeginRecord(1, retiredId, new SegmentId(1), 1);
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendCompactionBeginAsync(retired, CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendCompactionCommittedAsync(
                new JournalCompactionCommittedRecord(1, retiredId),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendCompactionRetiredAsync(
                new JournalCompactionRetiredRecord(1, retiredId, retired.SourceSegmentId, retired.SourceGeneration),
                CancellationToken.None));

        var openId = journal.AllocateCompactionId();
        var begin = new JournalCompactionBeginRecord(1, openId, new SegmentId(2), 3);
        var intent = new JournalRelocationIntentRecord(
            1,
            openId,
            1,
            tiny.ArtId,
            tiny.ArtHash,
            tiny.ArtSize,
            location);
        var written = new JournalRelocationWrittenRecord(1, openId, 1, location);
        var committed = new JournalCompactionCommittedRecord(1, openId);
        Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendCompactionBeginAsync(begin, CancellationToken.None));
        Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendRelocationIntentAsync(intent, CancellationToken.None));
        Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendRelocationWrittenAsync(written, CancellationToken.None));
        Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendCompactionCommittedAsync(committed, CancellationToken.None));

        var incomplete = Assert.Single(journal.EnumerateIncomplete());
        var fence = ArticleJournalFrameCodec.EncodeSequenceFence(incomplete.Accept.Sequence + 1).Length;
        var accept = ArticleJournalFrameCodec.EncodeAccept(incomplete.Accept).Length;
        var physical = ArticleJournalFrameCodec.EncodePhysicalWritten(incomplete.PhysicalWritten!.Value).Length;
        var openBytes = ArticleJournalFrameCodec.EncodeCompactionBegin(begin).Length
            + ArticleJournalFrameCodec.EncodeRelocationIntent(intent).Length
            + ArticleJournalFrameCodec.EncodeRelocationWritten(written).Length
            + ArticleJournalFrameCodec.EncodeCompactionCommitted(committed).Length;
        var retiredBytes = ArticleJournalFrameCodec.EncodeCompactionBegin(retired).Length
            + ArticleJournalFrameCodec.EncodeCompactionCommitted(new JournalCompactionCommittedRecord(1, retiredId)).Length
            + ArticleJournalFrameCodec.EncodeCompactionRetired(
                new JournalCompactionRetiredRecord(1, retiredId, retired.SourceSegmentId, retired.SourceGeneration)).Length;
        var expected = fence + accept + physical + openBytes;
        Assert.True(accept > tiny.ArtSize);
        Assert.Equal(1, tiny.ArtSize);
        Assert.True(retiredBytes > 0);

        var capacity = new LedgerCapacity();
        journal.CheckpointCapacity = capacity.Create();
        var released = journal.CheckpointTruncateCommitted();
        Assert.Equal(expected, Assert.Single(capacity.ReserveCalls));
        Assert.Equal(expected, journal.JournalPhysicalBytes);
        journal.Dispose();
        Assert.Equal((byte)ArticleJournalFrameType.SequenceFence, File.ReadAllBytes(JournalPath(dir))[4]);
        Assert.True(released > 0);
        Assert.Equal(0, capacity.Ledger.CheckpointReservedBytes);
        Assert.Empty(TempJournals(dir));
    }

    [Fact]
    public async Task Journal_RefusesAtExactBoundaryAndOneByteOver_WithoutCreatingTemp()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        var accept = await AppendAcceptAsync(journal, "<ckpt-bound@example.test>", [1, 2, 3]);
        await CommitAcceptAsync(journal, accept);
        var image = ArticleJournalFrameCodec.EncodeSequenceFence(accept.Sequence + 1).Length;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(1_000, 0.80);
        var before = journal.JournalPhysicalBytes;

        var denied = new LedgerCapacity { Used = ceiling - image + 1, Total = 1_000 };
        journal.CheckpointCapacity = denied.Create();
        var ex = Assert.Throws<CheckpointCapacityDeniedException>(() => journal.CheckpointTruncateCommitted());
        Assert.Equal(image, ex.RequiredBytes);
        Assert.Equal(before, journal.JournalPhysicalBytes);
        Assert.Empty(TempJournals(dir));
        Assert.Equal(0, denied.Ledger.CheckpointReservedBytes);

        var admitted = new LedgerCapacity { Used = ceiling - image, Total = 1_000 };
        journal.CheckpointCapacity = admitted.Create();
        Assert.True(journal.CheckpointTruncateCommitted() > 0);
        Assert.Equal(image, new FileInfo(JournalPath(dir)).Length);
        Assert.Equal(0, admitted.Ledger.CheckpointReservedBytes);
        Assert.Empty(TempJournals(dir));
    }

    [Fact]
    public async Task Journal_HoldsReservationWhileBothFilesExist_AndReleasesOnFailure()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        var accept = await AppendAcceptAsync(journal, "<ckpt-hold@example.test>", [9]);
        await CommitAcceptAsync(journal, accept);
        var image = ArticleJournalFrameCodec.EncodeSequenceFence(accept.Sequence + 1).Length;
        var capacity = new LedgerCapacity();
        journal.CheckpointCapacity = capacity.Create();
        var held = 0L;
        var tempsWhileHeld = 0;
        journal.CheckpointTestFault = point =>
        {
            if (point != FileArticleJournal.CheckpointFaultPoint.AfterTempFlushed)
            {
                return;
            }

            held = capacity.Ledger.CheckpointReservedBytes;
            tempsWhileHeld = TempJournals(dir).Length;
            Assert.True(File.Exists(JournalPath(dir)));
            throw new IOException("flush-fault");
        };

        Assert.Throws<IOException>(() => journal.CheckpointTruncateCommitted());
        Assert.Equal(image, held);
        Assert.Equal(1, tempsWhileHeld);
        Assert.Equal(0, capacity.Ledger.CheckpointReservedBytes);
        Assert.Empty(TempJournals(dir));
    }

    [Fact]
    public async Task Engine_AdmissionSeesJournalAndSnapshotReservationsTogether()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: reader);
        var first = CreateRecord("<ckpt-adm-1@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);

        var journalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseJournal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Journal.CheckpointTestFault = point =>
        {
            if (point != FileArticleJournal.CheckpointFaultPoint.AfterTempFlushed)
            {
                return;
            }

            journalEntered.TrySetResult();
            releaseJournal.Task.GetAwaiter().GetResult();
        };
        engine.Index.TestBeforeSnapshotFlush = () =>
        {
            snapshotEntered.TrySetResult();
            releaseSnapshot.Task.GetAwaiter().GetResult();
        };

        var fence = ArticleJournalFrameCodec.EncodeSequenceFence(2).Length;
        var snapshotBytes = ArticleIndexSnapshotCodec.EncodedLength(1);
        var firstRequired = SegmentRecordCodec.RecordLengthForArtSize(first.ArtSize);
        var journalHeld = ArticleJournalFrameCodec.SequenceReservationBytes(first.ArtSize);
        var indexHeldBytes = (long)ArticleIndexRecordCodec.RecordLength;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(10_000, 0.80);
        reader.TotalBytes = 10_000;
        reader.UsedBytes = ceiling - fence - snapshotBytes - firstRequired - journalHeld - indexHeldBytes;
        Assert.True(reader.UsedBytes >= 0);
        var journalTask = Task.Run(() => engine.CheckpointTruncateCommitted());
        var journalEnteredOrTimeout = await Task.WhenAny(journalEntered.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(journalEntered.Task, journalEnteredOrTimeout);
        var snapshotTask = Task.Run(() => engine.Index.WriteSnapshot());
        var snapshotEnteredOrTimeout = await Task.WhenAny(snapshotEntered.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(snapshotEntered.Task, snapshotEnteredOrTimeout);
        Assert.Equal(fence + snapshotBytes, engine.ProcessLocalCheckpointReservedBytes);

        var rejected = await engine.AcceptAsync(CreateRecord("<ckpt-adm-2@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, rejected.Outcome);
        Assert.Equal(firstRequired, engine.ProcessLocalArticleReservedBytes);

        releaseSnapshot.TrySetResult();
        var snapshot = await snapshotTask;
        Assert.Equal(1UL, snapshot.RecordCount);
        releaseJournal.TrySetResult();
        Assert.True(await journalTask > 0);
        Assert.Equal(snapshotBytes, engine.ProcessLocalCheckpointReservedBytes);
        Assert.Empty(TempJournals(dir));
        Assert.False(File.Exists(SnapTempPath(dir)));
    }

    [Fact]
    public async Task Engine_CompactionSeesCheckpointReservation()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 0.80, compactionHeadroom: 0.10),
            capacityReader: reader);
        var record = CreateRecord("<ckpt-cmp-0@seg.test>");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, sourceId, info.Generation),
                CancellationToken.None));

        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var snapshotBytes = ArticleIndexSnapshotCodec.EncodedLength(1);
        var articleHeld = engine.ProcessLocalArticleReservedBytes;
        var journalHeld = engine.ProcessLocalJournalReservedBytes;
        var indexHeld = engine.ProcessLocalIndexReservedBytes;
        long total = 0;
        for (var candidate = required; candidate < required * 40; candidate++)
        {
            var snapshotFits = ProcessLocalCapacityLedger.WouldFit(
                0, articleHeld, 0, candidate, snapshotBytes, 0.80, journalReservedBytes: journalHeld, indexReservedBytes: indexHeld);
            var intentHeld = journalHeld + ArticleJournalFrameCodec.RelocationIntentFrameLength;
            var duringRejects = !ProcessLocalCapacityLedger.WouldFit(
                0, articleHeld, 0, candidate, required, 0.90, snapshotBytes, intentHeld, indexHeld);
            if (snapshotFits && duringRejects)
            {
                total = candidate;
                break;
            }
        }

        Assert.True(total > 0);

        ArticleRelocationResult? during = null;
        engine.Index.TestBeforeSnapshotFlush = () =>
        {
            during = engine.RelocateArticleAsync(
                    compactionId,
                    1,
                    sourceId,
                    info.Generation,
                    record.ArtId,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        };

        reader.TotalBytes = total;
        reader.UsedBytes = 0;
        var header = engine.Index.WriteSnapshot();
        Assert.Equal(1UL, header.RecordCount);
        Assert.NotNull(during);
        Assert.Equal(ArticleRelocationOutcome.RejectedCapacity, during.Value.Outcome);
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(snapshotBytes, engine.ProcessLocalCheckpointReservedBytes);

        var after = await engine.RelocateArticleAsync(
            compactionId,
            1,
            sourceId,
            info.Generation,
            record.ArtId,
            CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedCapacity, after.Outcome);
        Assert.Equal(snapshotBytes, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public async Task Engine_CheckpointDenied_LeavesJournalAndAdmissionUsable()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 1_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: reader);
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<ckpt-deny@seg.test>"), CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        var before = engine.Journal.JournalPhysicalBytes;
        var image = ArticleJournalFrameCodec.EncodeSequenceFence(2).Length;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(reader.TotalBytes, 0.80);
        reader.UsedBytes = ceiling - image + 1;

        Assert.Equal(0, engine.CheckpointTruncateCommitted());
        Assert.Equal(before, engine.Journal.JournalPhysicalBytes);
        Assert.Empty(TempJournals(dir));
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);

        reader.UsedBytes = 0;
        reader.TotalBytes = 1_000_000;
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<ckpt-deny-ok@seg.test>"), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Engine_CapacityDisabled_CheckpointDoesNotReadCapacity()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 1, used: 1) { ThrowOnRead = true };
        await using var engine = FileArticleStorageEngine.Open(
            dir.Options with { CapacityAdmissionEnabled = false },
            capacityReader: reader);
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<ckpt-off@seg.test>"), CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        var snapshot = engine.Index.WriteSnapshot();
        Assert.Equal(1UL, snapshot.RecordCount);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public void IndexSnapshot_ReservesCodecLength_NotLiveFileLength()
    {
        using var dir = TempStorageDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        const int count = 20;
        for (var i = 0; i < count; i++)
        {
            Assert.True(index.TryCommitPresent(Present($"<snap-{i}@example.test>", (ulong)i + 1)));
        }

        var anchor = Present("<snap-0@example.test>", 1);
        var location = anchor.Location;
        for (var i = 0; i < 15; i++)
        {
            var next = location with { Offset = location.Offset + ArticleIndexRecordCodec.RecordLength };
            Assert.Equal(
                ArticleRelocateOutcome.Relocated,
                index.TryRelocate(anchor.ArtId, location, next, anchor.ArtHash, anchor.ArtSize));
            location = next;
        }

        var fileLength = index.CopyIndexBytes().Length;
        var encoded = ArticleIndexSnapshotCodec.EncodedLength(count);
        Assert.Equal(
            ArticleIndexSnapshotCodec.HeaderLength + 4 + ((long)ArticleIndexRecordCodec.RecordLength * count),
            encoded);
        Assert.NotEqual(fileLength, encoded);
        Assert.True(fileLength > encoded);

        var reservedDuringWrite = 0L;
        var capacity = new LedgerCapacity();
        index.AttachCheckpointCapacity(capacity.Create());
        index.TestDuringSnapshotWrite = () =>
        {
            Assert.False(File.Exists(SnapTempPath(dir)));
            reservedDuringWrite = capacity.Ledger.CheckpointReservedBytes;
        };

        var header = index.WriteSnapshot();
        Assert.Equal(encoded, reservedDuringWrite);
        Assert.Equal(encoded, capacity.ReserveCalls[0]);
        Assert.Equal((ulong)count, header.RecordCount);
        Assert.Equal(encoded, capacity.Ledger.CheckpointReservedBytes);
        Assert.False(File.Exists(SnapTempPath(dir)));
        Assert.True(File.Exists(SnapPath(dir)));
    }

    [Fact]
    public void IndexSnapshot_RefusesAtBoundary_ReleasesOnFailure_AndPreservesPriorSnapshot()
    {
        using var dir = TempStorageDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        Assert.True(index.TryCommitPresent(Present("<snap-keep@example.test>", 1)));
        var encoded = ArticleIndexSnapshotCodec.EncodedLength(1);
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(1_000, 0.80);
        var capacity = new LedgerCapacity { Used = ceiling - encoded, Total = 1_000 };
        index.AttachCheckpointCapacity(capacity.Create());
        var installed = index.WriteSnapshot();
        Assert.Equal(encoded, capacity.ReserveCalls[0]);
        Assert.Equal(encoded, capacity.Ledger.CheckpointReservedBytes);

        capacity.Used = ceiling - encoded + 1;
        capacity.ReserveCalls.Clear();
        var denied = Assert.Throws<CheckpointCapacityDeniedException>(() => index.WriteSnapshot());
        Assert.Equal(encoded, denied.RequiredBytes);
        Assert.False(File.Exists(SnapTempPath(dir)));
        Assert.Equal(encoded, capacity.Ledger.CheckpointReservedBytes);
        Assert.Equal(installed.Generation, ArticleIndexSnapshotCodec.Read(SnapPath(dir)).Generation);

        capacity.Used = 0;
        index.TestBeforeSnapshotFlush = () => throw new IOException("snapshot-fault");
        Assert.Throws<IOException>(() => index.WriteSnapshot());
        Assert.Equal(encoded, capacity.Ledger.CheckpointReservedBytes);
        Assert.False(File.Exists(SnapTempPath(dir)));
        Assert.Equal(installed.Generation, ArticleIndexSnapshotCodec.Read(SnapPath(dir)).Generation);
    }

    [Fact]
    public void IndexReplacement_ReservesHeaderPlusTail_AndCatchUpBeforeWrite()
    {
        using var dir = TempStorageDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        Assert.True(index.TryCommitPresent(Present("<repl-a@example.test>", 1)));
        var before = index.CopyIndexBytes();
        var capacity = new LedgerCapacity();
        long replLengthAtIncrease = -1;
        long increaseBytes = -1;
        capacity.OnIncrease = extra =>
        {
            increaseBytes = extra;
            replLengthAtIncrease = new FileInfo(ReplPath(dir)).Length;
        };
        index.AttachCheckpointCapacity(capacity.Create());
        index.TestDuringReplacementWrite = () =>
        {
            Assert.True(index.TryCommitPresent(Present("<repl-b@example.test>", 2)));
        };

        var result = index.Checkpoint();
        Assert.Equal(ArticleIndexSnapshotCodec.EncodedLength(1), capacity.ReserveCalls[0]);
        Assert.Equal(ArticleIndexDeltaFile.HeaderLength, capacity.ReserveCalls[1]);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, increaseBytes);
        Assert.Equal(ArticleIndexDeltaFile.HeaderLength, replLengthAtIncrease);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, result.DeltaBytes);
        Assert.Equal(ArticleIndexSnapshotCodec.EncodedLength(1), capacity.Ledger.CheckpointReservedBytes);
        Assert.False(File.Exists(ReplPath(dir)));
        Assert.NotEqual(before, index.CopyIndexBytes());
    }

    [Fact]
    public void IndexReplacement_CatchUpDenialAndWriteFailure_ReleaseReservation()
    {
        using var dir = TempStorageDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        Assert.True(index.TryCommitPresent(Present("<repl-deny@example.test>", 1)));
        var before = index.CopyIndexBytes();
        var capacity = new LedgerCapacity { DenyIncrease = true };
        long replLengthAtIncrease = -1;
        capacity.OnIncrease = _ => replLengthAtIncrease = new FileInfo(ReplPath(dir)).Length;
        index.AttachCheckpointCapacity(capacity.Create());
        index.TestDuringReplacementWrite = () =>
        {
            Assert.True(index.TryCommitPresent(Present("<repl-grow@example.test>", 2)));
        };

        Assert.Throws<CheckpointCapacityDeniedException>(() => index.Checkpoint());
        Assert.Equal(ArticleIndexDeltaFile.HeaderLength, replLengthAtIncrease);
        Assert.Equal(before.Length + ArticleIndexRecordCodec.RecordLength, index.CopyIndexBytes().Length);
        Assert.False(File.Exists(ReplPath(dir)));
        Assert.Equal(ArticleIndexSnapshotCodec.EncodedLength(1), capacity.Ledger.CheckpointReservedBytes);

        var afterDenial = index.CopyIndexBytes();
        capacity.DenyIncrease = false;
        index.TestDuringReplacementWrite = null;
        index.TestBeforeReplacementFlush = () => throw new IOException("repl-fault");
        Assert.Throws<IOException>(() => index.Checkpoint());
        Assert.Equal(afterDenial, index.CopyIndexBytes());
        Assert.False(File.Exists(ReplPath(dir)));
        Assert.Equal(ArticleIndexSnapshotCodec.EncodedLength(2), capacity.Ledger.CheckpointReservedBytes);
    }

    [Fact]
    public async Task CrashLeftovers_AreNotReservedAtStartupOrAddedToTheNextReservation()
    {
        using var dir = TempStorageDir.Create();
        var snapTmp = SnapTempPath(dir);
        var replTmp = ReplPath(dir);
        await File.WriteAllBytesAsync(snapTmp, new byte[100]);
        await File.WriteAllBytesAsync(replTmp, new byte[50]);
        var orphan = Path.Combine(dir.Options.ControlDir, ".article.journal.deadbeef.tmp");
        await File.WriteAllBytesAsync(orphan, new byte[80]);

        var reader = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: reader);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);

        var reserved = new List<long>();
        engine.TestBeforeCheckpointReserve = reserved.Add;
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<ckpt-left@seg.test>"), CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        _ = engine.Index.Checkpoint();

        Assert.Equal(ArticleJournalFrameCodec.EncodeSequenceFence(2).Length, reserved[0]);
        Assert.Contains(ArticleIndexSnapshotCodec.EncodedLength(1), reserved);
        Assert.Contains((long)ArticleIndexDeltaFile.HeaderLength, reserved);
        Assert.DoesNotContain(100L, reserved);
        Assert.DoesNotContain(50L, reserved);
        Assert.DoesNotContain(80L, reserved);
        Assert.Equal(ArticleIndexSnapshotCodec.EncodedLength(1), engine.ProcessLocalCheckpointReservedBytes);
    }

    private static async Task CommitAcceptAsync(FileArticleJournal journal, JournalAcceptRecord accept)
    {
        var location = new StoredArticleLocation(new SegmentId(4), 0, accept.ArtSize);
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, accept.Sequence),
                CancellationToken.None));
    }

    private static async Task<JournalAcceptRecord> AppendAcceptAsync(
        FileArticleJournal journal,
        string messageId,
        byte[] body)
    {
        Assert.True(journal.TryAppendNewAccept(
            ArticleId.FromMessageId(Encoding.ASCII.GetBytes(messageId)),
            1,
            body.Length,
            DateTimeOffset.UtcNow,
            body,
            out var accept,
            out _));
        await Task.CompletedTask;
        return accept;
    }

    private static string JournalPath(TempStorageDir dir) =>
        Path.Combine(dir.Options.ControlDir, FileArticleJournal.JournalFileName);

    private static string[] TempJournals(TempStorageDir dir) =>
        Directory.GetFiles(dir.Options.ControlDir, ".article.journal.*.tmp");

    private static string SnapTempPath(TempStorageDir dir) =>
        Path.Combine(dir.Options.ControlDir, FileArticleIndex.SnapshotTempFileName);

    private static string SnapPath(TempStorageDir dir) =>
        Path.Combine(dir.Options.ControlDir, FileArticleIndex.SnapshotFileName);

    private static string ReplPath(TempStorageDir dir) =>
        Path.Combine(dir.Options.ControlDir, FileArticleIndex.ReplacementTempFileName);

    private static StoredArticleMetadata Present(string messageId, ulong hash) =>
        new(
            ArticleId.FromMessageId(Encoding.ASCII.GetBytes(messageId)),
            hash,
            8,
            new StoredArticleLocation(new SegmentId(1), (long)hash * 8, 8),
            ArticleStorageState.Present,
            new DateTimeOffset(2024, 8, 23, 7, 30, 10, TimeSpan.Zero),
            1UL);

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: checkpoint-capacity\r\n");
        _ = builder.Append("\r\nline1\r\nline2\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private static ArticleStorageRuntimeOptions WithCapacity(
        ArticleStorageRuntimeOptions options,
        double maximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization,
        double compactionHeadroom = ArticleCapacityOptions.DefaultCompactionHeadroom) =>
        options with
        {
            CapacityAdmissionEnabled = true,
            CapacityMaximumUtilization = maximumUtilization,
            CapacityCompactionHeadroom = compactionHeadroom,
        };

    private sealed class LedgerCapacity
    {
        public ProcessLocalCapacityLedger Ledger { get; } = new();

        public long Used { get; set; }

        public long Total { get; set; } = 1_000_000;

        public bool DenyIncrease { get; set; }

        public List<long> ReserveCalls { get; } = [];

        public Action<long>? OnIncrease { get; set; }

        public CheckpointCapacityReservation Create() =>
            new()
            {
                TryReserve = bytes =>
                {
                    ReserveCalls.Add(bytes);
                    if (!Ledger.WouldFit(Used, Total, bytes, 0.80))
                    {
                        return null;
                    }

                    return Ledger.ReserveCheckpoint(bytes);
                },
                TryIncrease = (id, extra) =>
                {
                    OnIncrease?.Invoke(extra);
                    if (DenyIncrease || !Ledger.WouldFit(Used, Total, extra, 0.80))
                    {
                        return false;
                    }

                    return Ledger.TryIncreaseCheckpoint(id, extra);
                },
                Release = id => Ledger.ReleaseCheckpoint(id),
            };
    }

    private sealed class MutableCapacityReader(long total, long used) : IStorageCapacityReader
    {
        public long TotalBytes { get; set; } = total;

        public long UsedBytes { get; set; } = used;

        public bool ThrowOnRead { get; set; }

        public int Reads { get; private set; }

        public StorageCapacitySnapshot Read()
        {
            Reads++;
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("capacity read is disabled");
            }

            return new StorageCapacitySnapshot(TotalBytes, UsedBytes, Math.Max(0L, TotalBytes - UsedBytes));
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-ckpt-" + Guid.NewGuid().ToString("N"));
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
