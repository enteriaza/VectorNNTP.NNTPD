using System.Buffers.Binary;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

public sealed class FileArticleJournalTests
{
    [Fact]
    public void A_Accept_SurvivesProcessRestart()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<a-restart@example.test>", "restart-body\r\n");
        ulong sequence;
        long outstanding;

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                record.ArtId,
                record.ArtHash,
                record.ArtSize,
                DateTimeOffset.UtcNow,
                record.ArtData,
                out var accept,
                out _));
            sequence = accept.Sequence;
            outstanding = journalA.OutstandingRecoverableBytes;
            Assert.Equal(record.ArtSize, outstanding);
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryGetOutstanding(record.ArtId, out var recovered));
        Assert.Equal(sequence, recovered.Sequence);
        Assert.Equal(record.ArtId, recovered.ArtId);
        Assert.Equal(record.ArtHash, recovered.ArtHash);
        Assert.Equal(record.ArtSize, recovered.ArtSize);
        Assert.True(recovered.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(outstanding, journalB.OutstandingRecoverableBytes);
        Assert.Equal(sequence + 1, journalB.NextSequence);
    }

    [Fact]
    public async Task B_AcceptPlusPhysicalWritten_SurvivesRestart()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<b-pw@example.test>");
        var location = new StoredArticleLocation(new SegmentId(7), 128, record.ArtSize);
        ulong sequence;

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                record.ArtId,
                record.ArtHash,
                record.ArtSize,
                DateTimeOffset.UtcNow,
                record.ArtData,
                out var accept,
                out _));
            sequence = accept.Sequence;
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, sequence, location),
                    CancellationToken.None));
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        var incomplete = Assert.Single(journalB.EnumerateIncomplete());
        Assert.Equal(sequence, incomplete.Accept.Sequence);
        Assert.NotNull(incomplete.PhysicalWritten);
        Assert.Equal(location, incomplete.PhysicalWritten!.Value.Location);
        Assert.Equal(record.ArtSize, journalB.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task C_FullyCommitted_NotOutstandingAfterRestart()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<c-committed@example.test>");
        var location = new StoredArticleLocation(new SegmentId(1), 0, record.ArtSize);

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                record.ArtId,
                record.ArtHash,
                record.ArtSize,
                DateTimeOffset.UtcNow,
                record.ArtData,
                out var accept,
                out _));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendIndexCommittedAsync(
                    new JournalIndexCommittedRecord(1, accept.Sequence),
                    CancellationToken.None));
            Assert.Equal(0, journalA.OutstandingRecoverableBytes);
            Assert.Empty(journalA.EnumerateIncomplete());
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.False(journalB.TryGetOutstanding(record.ArtId, out _));
        Assert.Empty(journalB.EnumerateIncomplete());
        Assert.Equal(0, journalB.OutstandingRecoverableBytes);
    }

    [Fact]
    public void D_Sequence_ContinuesMonotonicallyAfterRestart()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<d-seq-1@example.test>");
        ulong firstSequence;

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                first.ArtId,
                first.ArtHash,
                first.ArtSize,
                DateTimeOffset.UtcNow,
                first.ArtData,
                out var accept,
                out _));
            firstSequence = accept.Sequence;
        }

        var second = CreateRecord("<d-seq-2@example.test>");
        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryAppendNewAccept(
            second.ArtId,
            second.ArtHash,
            second.ArtSize,
            DateTimeOffset.UtcNow,
            second.ArtData,
            out var next,
            out _));
        Assert.Equal(firstSequence + 1, next.Sequence);
        Assert.Equal(firstSequence + 2, journalB.NextSequence);
    }

    [Fact]
    public void E_DuplicateOutstandingAccept()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<e-dup@example.test>", "same\r\n");
        using var journal = FileArticleJournal.Open(dir.Options);
        Assert.True(journal.TryAppendNewAccept(
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            DateTimeOffset.UtcNow,
            record.ArtData,
            out var first,
            out _));
        Assert.False(journal.TryAppendNewAccept(
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            DateTimeOffset.UtcNow,
            record.ArtData,
            out var dup,
            out var outcome));
        Assert.Equal(ArticleAcceptOutcome.Duplicate, outcome);
        Assert.Equal(first.Sequence, dup.Sequence);
        Assert.Single(journal.EnumerateIncomplete());
    }

    [Fact]
    public void F_ConflictingOutstandingAccept()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<f-conflict@example.test>", "body-a\r\n");
        var conflict = CreateRecord("<f-conflict@example.test>", "body-b\r\n");
        Assert.Equal(first.ArtId, conflict.ArtId);
        Assert.NotEqual(first.ArtHash, conflict.ArtHash);

        using var journal = FileArticleJournal.Open(dir.Options);
        Assert.True(journal.TryAppendNewAccept(
            first.ArtId,
            first.ArtHash,
            first.ArtSize,
            DateTimeOffset.UtcNow,
            first.ArtData,
            out _,
            out _));
        Assert.False(journal.TryAppendNewAccept(
            conflict.ArtId,
            conflict.ArtHash,
            conflict.ArtSize,
            DateTimeOffset.UtcNow,
            conflict.ArtData,
            out _,
            out var outcome));
        Assert.Equal(ArticleAcceptOutcome.Conflict, outcome);
    }

    [Fact]
    public async Task G_PhysicalWritten_SameLocation_Idempotent()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<g-pw-idem@example.test>");
        var location = new StoredArticleLocation(new SegmentId(3), 10, record.ArtSize);
        using var journal = FileArticleJournal.Open(dir.Options);
        Assert.True(journal.TryAppendNewAccept(
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            DateTimeOffset.UtcNow,
            record.ArtData,
            out var accept,
            out _));
        var pw = new JournalPhysicalWrittenRecord(1, accept.Sequence, location);
        Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendPhysicalWrittenAsync(pw, CancellationToken.None));
        Assert.Equal(JournalAppendOutcome.IdempotentNoOp, await journal.AppendPhysicalWrittenAsync(pw, CancellationToken.None));
    }

    [Fact]
    public async Task H_PhysicalWritten_DifferentLocation_Rejected()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<h-pw-conflict@example.test>");
        using var journal = FileArticleJournal.Open(dir.Options);
        Assert.True(journal.TryAppendNewAccept(
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            DateTimeOffset.UtcNow,
            record.ArtData,
            out var accept,
            out _));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(1), 0, record.ArtSize)),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Conflict,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(2), 0, record.ArtSize)),
                CancellationToken.None));
    }

    [Fact]
    public async Task I_IndexCommitted_WithoutPhysicalWritten_Rejected()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<i-ic@example.test>");
        using var journal = FileArticleJournal.Open(dir.Options);
        Assert.True(journal.TryAppendNewAccept(
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            DateTimeOffset.UtcNow,
            record.ArtData,
            out var accept,
            out _));
        Assert.Equal(
            JournalAppendOutcome.Rejected,
            await journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, accept.Sequence),
                CancellationToken.None));
        Assert.Equal(record.ArtSize, journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task J_IndexCommitted_Replay_Idempotent()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<j-ic-idem@example.test>");
        var location = new StoredArticleLocation(new SegmentId(1), 0, record.ArtSize);
        using var journal = FileArticleJournal.Open(dir.Options);
        Assert.True(journal.TryAppendNewAccept(
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            DateTimeOffset.UtcNow,
            record.ArtData,
            out var accept,
            out _));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                CancellationToken.None));
        var ic = new JournalIndexCommittedRecord(1, accept.Sequence);
        Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendIndexCommittedAsync(ic, CancellationToken.None));
        Assert.Equal(JournalAppendOutcome.IdempotentNoOp, await journal.AppendIndexCommittedAsync(ic, CancellationToken.None));
        Assert.Equal(0, journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public void K_PartialFinalFrame_TruncatedOnOpen()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<k-partial-1@example.test>", "keep\r\n");
        var second = CreateRecord("<k-partial-2@example.test>", "torn\r\n");

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                first.ArtId,
                first.ArtHash,
                first.ArtSize,
                DateTimeOffset.UtcNow,
                first.ArtData,
                out _,
                out _));
            Assert.True(journalA.TryAppendNewAccept(
                second.ArtId,
                second.ArtHash,
                second.ArtSize,
                DateTimeOffset.UtcNow,
                second.ArtData,
                out _,
                out _));
        }

        var path = Path.Combine(dir.ControlDir, FileArticleJournal.JournalFileName);
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 16);
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length - 7).ToArray());

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryGetOutstanding(first.ArtId, out _));
        Assert.False(journalB.TryGetOutstanding(second.ArtId, out _));
        Assert.Equal(first.ArtSize, journalB.OutstandingRecoverableBytes);
        Assert.Single(journalB.EnumerateIncomplete());
    }

    [Fact]
    public void L_InvalidChecksumOnFinalFrame_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<l-crc-1@example.test>", "keep\r\n");
        var second = CreateRecord("<l-crc-2@example.test>", "badcrc\r\n");

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                first.ArtId,
                first.ArtHash,
                first.ArtSize,
                DateTimeOffset.UtcNow,
                first.ArtData,
                out _,
                out _));
            Assert.True(journalA.TryAppendNewAccept(
                second.ArtId,
                second.ArtHash,
                second.ArtSize,
                DateTimeOffset.UtcNow,
                second.ArtData,
                out _,
                out _));
        }

        var path = Path.Combine(dir.ControlDir, FileArticleJournal.JournalFileName);
        CorruptFinalFrameChecksum(path);
        var corrupted = File.ReadAllBytes(path);

        var ex = Assert.Throws<ArticleJournalCorruptException>(() => FileArticleJournal.Open(dir.Options));
        Assert.Contains("complete frame", ex.Message, StringComparison.Ordinal);
        Assert.Equal(corrupted, File.ReadAllBytes(path));
    }

    [Fact]
    public void M_CorruptionInMiddle_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<m-mid-1@example.test>", "one\r\n");
        var second = CreateRecord("<m-mid-2@example.test>", "two\r\n");

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                first.ArtId,
                first.ArtHash,
                first.ArtSize,
                DateTimeOffset.UtcNow,
                first.ArtData,
                out _,
                out _));
            Assert.True(journalA.TryAppendNewAccept(
                second.ArtId,
                second.ArtHash,
                second.ArtSize,
                DateTimeOffset.UtcNow,
                second.ArtData,
                out _,
                out _));
        }

        CorruptFirstFrameChecksumLeaveTrailing(Path.Combine(dir.ControlDir, FileArticleJournal.JournalFileName));

        var ex = Assert.Throws<ArticleJournalCorruptException>(() => FileArticleJournal.Open(dir.Options));
        Assert.Contains("trailing bytes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptLength_InMiddleWithTrailingFrames_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<cl-mid-1@example.test>", "one\r\n");
        var second = CreateRecord("<cl-mid-2@example.test>", "two\r\n");

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                first.ArtId,
                first.ArtHash,
                first.ArtSize,
                DateTimeOffset.UtcNow,
                first.ArtData,
                out _,
                out _));
            Assert.True(journalA.TryAppendNewAccept(
                second.ArtId,
                second.ArtHash,
                second.ArtSize,
                DateTimeOffset.UtcNow,
                second.ArtData,
                out _,
                out _));
        }

        PoisonFirstFrameLength(Path.Combine(dir.ControlDir, FileArticleJournal.JournalFileName), length: 1);

        var ex = Assert.Throws<ArticleJournalCorruptException>(() => FileArticleJournal.Open(dir.Options));
        Assert.Contains("corrupt length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptLength_InSmallJournal_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        var only = CreateRecord("<cl-small@example.test>", "x\r\n");

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                only.ArtId,
                only.ArtHash,
                only.ArtSize,
                DateTimeOffset.UtcNow,
                only.ArtData,
                out _,
                out _));
        }

        // Out-of-range length at the sole frame — must not truncate even for small journals.
        PoisonFirstFrameLength(Path.Combine(dir.ControlDir, FileArticleJournal.JournalFileName), length: 0);

        var ex = Assert.Throws<ArticleJournalCorruptException>(() => FileArticleJournal.Open(dir.Options));
        Assert.Contains("corrupt length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenuineTruncatedFinalFrame_StillTruncatesAndReopens()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<torn-keep@example.test>", "keep\r\n");
        var second = CreateRecord("<torn-drop@example.test>", "drop\r\n");

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                first.ArtId,
                first.ArtHash,
                first.ArtSize,
                DateTimeOffset.UtcNow,
                first.ArtData,
                out _,
                out _));
            Assert.True(journalA.TryAppendNewAccept(
                second.ArtId,
                second.ArtHash,
                second.ArtSize,
                DateTimeOffset.UtcNow,
                second.ArtData,
                out _,
                out _));
        }

        var path = Path.Combine(dir.ControlDir, FileArticleJournal.JournalFileName);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length - 9).ToArray());

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryGetOutstanding(first.ArtId, out _));
        Assert.False(journalB.TryGetOutstanding(second.ArtId, out _));
        Assert.Equal(first.ArtSize, journalB.OutstandingRecoverableBytes);
        Assert.Single(journalB.EnumerateIncomplete());
    }

    [Fact]
    public void N_MaximumArticleSize_Accepted()
    {
        using var dir = TempControlDir.Create();
        var artData = CreateExactSizeArtData(ArticleResourceLimits.MaxArticleBytes);
        var artId = ArticleId.FromMessageId("<n-max@example.test>"u8);
        var artHash = System.IO.Hashing.XxHash3.HashToUInt64(artData);

        using var journal = FileArticleJournal.Open(dir.Options);
        Assert.True(journal.TryAppendNewAccept(
            artId,
            artHash,
            artData.Length,
            DateTimeOffset.UtcNow,
            artData,
            out var accept,
            out var outcome));
        Assert.Equal(default, outcome);
        Assert.Equal(ArticleResourceLimits.MaxArticleBytes, accept.ArtSize);
        Assert.Equal(ArticleResourceLimits.MaxArticleBytes, journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public void O_ArticleLargerThanMax_Rejected()
    {
        using var dir = TempControlDir.Create();
        var artData = new byte[ArticleResourceLimits.MaxArticleBytes + 1];
        artData.AsSpan().Fill(0x61);
        var artId = ArticleId.FromMessageId("<o-oversize@example.test>"u8);

        using var journal = FileArticleJournal.Open(dir.Options);
        Assert.False(journal.TryAppendNewAccept(
            artId,
            1,
            artData.Length,
            DateTimeOffset.UtcNow,
            artData,
            out _,
            out var outcome));
        Assert.Equal(ArticleAcceptOutcome.RejectedInvalid, outcome);
        Assert.Equal(0, journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task P_Pressure_OutstandingReleasedOnIndexCommitted()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<p-pressure@example.test>");
        var options = dir.Options with
        {
            JournalSoftLimitBytes = Math.Max(1, record.ArtSize / 2),
            JournalHardLimitBytes = record.ArtSize * 2,
        };
        using var journal = FileArticleJournal.Open(options);
        Assert.True(journal.TryAppendNewAccept(
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            DateTimeOffset.UtcNow,
            record.ArtData,
            out var accept,
            out _));
        Assert.Equal(record.ArtSize, journal.OutstandingRecoverableBytes);
        Assert.Equal(StorageWritePressure.Elevated, journal.Pressure);

        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(1), 0, record.ArtSize)),
                CancellationToken.None));
        Assert.Equal(record.ArtSize, journal.OutstandingRecoverableBytes);

        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, accept.Sequence),
                CancellationToken.None));
        Assert.Equal(0, journal.OutstandingRecoverableBytes);
        Assert.Equal(StorageWritePressure.Normal, journal.Pressure);
    }

    [Fact]
    public async Task Q_JournalPhysicalBytes_DiffersFromOutstanding_WhenCommittedRemain()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<q-physical@example.test>");
        using var journal = FileArticleJournal.Open(dir.Options);
        Assert.True(journal.TryAppendNewAccept(
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            DateTimeOffset.UtcNow,
            record.ArtData,
            out var accept,
            out _));
        Assert.True(journal.JournalPhysicalBytes > journal.OutstandingRecoverableBytes
            || journal.JournalPhysicalBytes >= record.ArtSize);

        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(1), 0, record.ArtSize)),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, accept.Sequence),
                CancellationToken.None));

        Assert.Equal(0, journal.OutstandingRecoverableBytes);
        var physicalWhileCommitted = journal.JournalPhysicalBytes;
        Assert.True(physicalWhileCommitted > 0);

        var released = journal.CheckpointTruncateCommitted();
        Assert.True(released > 0);
        Assert.True(journal.JournalPhysicalBytes < physicalWhileCommitted);
        Assert.Equal(0, journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public void R_CrashReopenImmediatelyAfterAcceptDurability()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<r-crash@example.test>", "durable\r\n");
        ulong sequence;

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                record.ArtId,
                record.ArtHash,
                record.ArtSize,
                DateTimeOffset.UtcNow,
                record.ArtData,
                out var accept,
                out _));
            sequence = accept.Sequence;
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryGetOutstanding(record.ArtId, out var recovered));
        Assert.Equal(sequence, recovered.Sequence);
        Assert.True(recovered.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(sequence + 1, journalB.NextSequence);
    }

    [Fact]
    public async Task S_MultipleOutstanding_MixedStates()
    {
        using var dir = TempControlDir.Create();
        var acceptOnly = CreateRecord("<s-accept@example.test>", "a\r\n");
        var withPw = CreateRecord("<s-pw@example.test>", "b\r\n");
        var committed = CreateRecord("<s-done@example.test>", "c\r\n");

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                acceptOnly.ArtId,
                acceptOnly.ArtHash,
                acceptOnly.ArtSize,
                DateTimeOffset.UtcNow,
                acceptOnly.ArtData,
                out var a1,
                out _));
            Assert.True(journalA.TryAppendNewAccept(
                withPw.ArtId,
                withPw.ArtHash,
                withPw.ArtSize,
                DateTimeOffset.UtcNow,
                withPw.ArtData,
                out var a2,
                out _));
            Assert.True(journalA.TryAppendNewAccept(
                committed.ArtId,
                committed.ArtHash,
                committed.ArtSize,
                DateTimeOffset.UtcNow,
                committed.ArtData,
                out var a3,
                out _));

            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, a2.Sequence, new StoredArticleLocation(new SegmentId(1), 0, withPw.ArtSize)),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, a3.Sequence, new StoredArticleLocation(new SegmentId(1), withPw.ArtSize, committed.ArtSize)),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendIndexCommittedAsync(
                    new JournalIndexCommittedRecord(1, a3.Sequence),
                    CancellationToken.None));

            Assert.Equal(acceptOnly.ArtSize + withPw.ArtSize, journalA.OutstandingRecoverableBytes);
            Assert.Equal(2, journalA.EnumerateIncomplete().Count);
            _ = a1;
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        var incomplete = journalB.EnumerateIncomplete();
        Assert.Equal(2, incomplete.Count);
        Assert.Null(incomplete[0].PhysicalWritten);
        Assert.NotNull(incomplete[1].PhysicalWritten);
        Assert.False(journalB.TryGetOutstanding(committed.ArtId, out _));
        Assert.Equal(acceptOnly.ArtSize + withPw.ArtSize, journalB.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task Checkpoint_PreservesNextSequence_WhenOnlyCommittedRemain()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<fence@example.test>");
        ulong nextAfterAccept;

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                record.ArtId,
                record.ArtHash,
                record.ArtSize,
                DateTimeOffset.UtcNow,
                record.ArtData,
                out var accept,
                out _));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(1), 0, record.ArtSize)),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendIndexCommittedAsync(
                    new JournalIndexCommittedRecord(1, accept.Sequence),
                    CancellationToken.None));
            nextAfterAccept = journalA.NextSequence;
            _ = journalA.CheckpointTruncateCommitted();
            Assert.Equal(nextAfterAccept, journalA.NextSequence);
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.Equal(nextAfterAccept, journalB.NextSequence);
        var again = CreateRecord("<fence-2@example.test>");
        Assert.True(journalB.TryAppendNewAccept(
            again.ArtId,
            again.ArtHash,
            again.ArtSize,
            DateTimeOffset.UtcNow,
            again.ArtData,
            out var next,
            out _));
        Assert.Equal(nextAfterAccept, next.Sequence);
    }

    [Fact]
    public async Task Checkpoint_WithOutstandingAccept_SurvivesDisposeReopen()
    {
        using var dir = TempControlDir.Create();
        var committed = CreateRecord("<cp-committed@example.test>", "done\r\n");
        var outstanding = CreateRecord("<cp-outstanding@example.test>", "open\r\n");
        var location = new StoredArticleLocation(new SegmentId(9), 64, outstanding.ArtSize);
        ulong outstandingSequence;
        long outstandingBytes;

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                committed.ArtId,
                committed.ArtHash,
                committed.ArtSize,
                DateTimeOffset.UtcNow,
                committed.ArtData,
                out var cAccept,
                out _));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, cAccept.Sequence, new StoredArticleLocation(new SegmentId(1), 0, committed.ArtSize)),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendIndexCommittedAsync(
                    new JournalIndexCommittedRecord(1, cAccept.Sequence),
                    CancellationToken.None));

            Assert.True(journalA.TryAppendNewAccept(
                outstanding.ArtId,
                outstanding.ArtHash,
                outstanding.ArtSize,
                DateTimeOffset.UtcNow,
                outstanding.ArtData,
                out var oAccept,
                out _));
            outstandingSequence = oAccept.Sequence;
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, outstandingSequence, location),
                    CancellationToken.None));
            outstandingBytes = journalA.OutstandingRecoverableBytes;
            Assert.Equal(outstanding.ArtSize, outstandingBytes);

            _ = journalA.CheckpointTruncateCommitted();
            Assert.Equal(outstandingBytes, journalA.OutstandingRecoverableBytes);
            var incomplete = Assert.Single(journalA.EnumerateIncomplete());
            Assert.Equal(outstandingSequence, incomplete.Accept.Sequence);
            Assert.Equal(location, incomplete.PhysicalWritten!.Value.Location);
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryGetOutstanding(outstanding.ArtId, out var recovered));
        Assert.Equal(outstandingSequence, recovered.Sequence);
        Assert.True(recovered.ArtData.Span.SequenceEqual(outstanding.ArtData.Span));
        Assert.Equal(outstandingBytes, journalB.OutstandingRecoverableBytes);
        Assert.Equal(location, Assert.Single(journalB.EnumerateIncomplete()).PhysicalWritten!.Value.Location);
        Assert.Equal(outstandingSequence + 1, journalB.NextSequence);
    }

    [Fact]
    public async Task Checkpoint_SequenceFence_NextExceedsAllPreviouslyAllocated()
    {
        using var dir = TempControlDir.Create();
        ulong maxAllocated = 0;

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            for (var i = 0; i < 5; i++)
            {
                var record = CreateRecord($"<cp-fence-{i}@example.test>", $"b{i}\r\n");
                Assert.True(journalA.TryAppendNewAccept(
                    record.ArtId,
                    record.ArtHash,
                    record.ArtSize,
                    DateTimeOffset.UtcNow,
                    record.ArtData,
                    out var accept,
                    out _));
                maxAllocated = accept.Sequence;
                Assert.Equal(
                    JournalAppendOutcome.Applied,
                    await journalA.AppendPhysicalWrittenAsync(
                        new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(1), i * 100L, record.ArtSize)),
                        CancellationToken.None));
                Assert.Equal(
                    JournalAppendOutcome.Applied,
                    await journalA.AppendIndexCommittedAsync(
                        new JournalIndexCommittedRecord(1, accept.Sequence),
                        CancellationToken.None));
            }

            Assert.True(journalA.NextSequence > maxAllocated);
            _ = journalA.CheckpointTruncateCommitted();
            Assert.True(journalA.NextSequence > maxAllocated);
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.NextSequence > maxAllocated);
        var next = CreateRecord("<cp-fence-next@example.test>");
        Assert.True(journalB.TryAppendNewAccept(
            next.ArtId,
            next.ArtHash,
            next.ArtSize,
            DateTimeOffset.UtcNow,
            next.ArtData,
            out var allocated,
            out _));
        Assert.True(allocated.Sequence > maxAllocated);
    }

    [Fact]
    public async Task Checkpoint_OutstandingFence_SurvivesAndNextIsGreater()
    {
        using var dir = TempControlDir.Create();
        ulong outstandingSequence = 0;

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            for (var i = 0; i < 3; i++)
            {
                var record = CreateRecord($"<cp-out-fence-{i}@example.test>", $"c{i}\r\n");
                Assert.True(journalA.TryAppendNewAccept(
                    record.ArtId,
                    record.ArtHash,
                    record.ArtSize,
                    DateTimeOffset.UtcNow,
                    record.ArtData,
                    out var accept,
                    out _));
                Assert.Equal(
                    JournalAppendOutcome.Applied,
                    await journalA.AppendPhysicalWrittenAsync(
                        new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(1), i * 50L, record.ArtSize)),
                        CancellationToken.None));
                Assert.Equal(
                    JournalAppendOutcome.Applied,
                    await journalA.AppendIndexCommittedAsync(
                        new JournalIndexCommittedRecord(1, accept.Sequence),
                        CancellationToken.None));
            }

            var open = CreateRecord("<cp-out-fence-open@example.test>", "open\r\n");
            Assert.True(journalA.TryAppendNewAccept(
                open.ArtId,
                open.ArtHash,
                open.ArtSize,
                DateTimeOffset.UtcNow,
                open.ArtData,
                out var outstanding,
                out _));
            outstandingSequence = outstanding.Sequence;
            _ = journalA.CheckpointTruncateCommitted();
            Assert.True(journalA.TryGetOutstanding(open.ArtId, out _));
            Assert.True(journalA.NextSequence > outstandingSequence);
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.True(journalB.TryGetOutstanding(
            CreateRecord("<cp-out-fence-open@example.test>", "open\r\n").ArtId,
            out var recovered));
        Assert.Equal(outstandingSequence, recovered.Sequence);
        Assert.True(journalB.NextSequence > outstandingSequence);
        var again = CreateRecord("<cp-out-fence-again@example.test>");
        Assert.True(journalB.TryAppendNewAccept(
            again.ArtId,
            again.ArtHash,
            again.ArtSize,
            DateTimeOffset.UtcNow,
            again.ArtData,
            out var next,
            out _));
        Assert.True(next.Sequence > outstandingSequence);
    }

    [Fact]
    public async Task Checkpoint_FailureBeforeMove_PreservesUsableState()
    {
        using var dir = TempControlDir.Create();
        var committed = CreateRecord("<cp-fail-c@example.test>", "c\r\n");
        var outstanding = CreateRecord("<cp-fail-o@example.test>", "o\r\n");

        using var journal = FileArticleJournal.Open(dir.Options);
        Assert.True(journal.TryAppendNewAccept(
            committed.ArtId,
            committed.ArtHash,
            committed.ArtSize,
            DateTimeOffset.UtcNow,
            committed.ArtData,
            out var cAccept,
            out _));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, cAccept.Sequence, new StoredArticleLocation(new SegmentId(1), 0, committed.ArtSize)),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, cAccept.Sequence),
                CancellationToken.None));
        Assert.True(journal.TryAppendNewAccept(
            outstanding.ArtId,
            outstanding.ArtHash,
            outstanding.ArtSize,
            DateTimeOffset.UtcNow,
            outstanding.ArtData,
            out var oAccept,
            out _));

        var physicalBefore = journal.JournalPhysicalBytes;
        var outstandingBefore = journal.OutstandingRecoverableBytes;
        journal.CheckpointTestFault = _ => throw new IOException("injected-checkpoint-fault-before-move");

        var fault = Assert.Throws<IOException>(() => journal.CheckpointTruncateCommitted());
        Assert.Contains("injected-checkpoint-fault-before-move", fault.Message, StringComparison.Ordinal);

        journal.CheckpointTestFault = null;
        Assert.Equal(physicalBefore, journal.JournalPhysicalBytes);
        Assert.Equal(outstandingBefore, journal.OutstandingRecoverableBytes);
        Assert.True(journal.TryGetOutstanding(outstanding.ArtId, out var stillOpen));
        Assert.Equal(oAccept.Sequence, stillOpen.Sequence);

        // Live journal remains usable after failed checkpoint.
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, oAccept.Sequence, new StoredArticleLocation(new SegmentId(2), 0, outstanding.ArtSize)),
                CancellationToken.None));
    }

    [Fact]
    public async Task Checkpoint_FailureAfterMoveBeforeReopen_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        var committed = CreateRecord("<cp-fail2-c@example.test>", "c\r\n");
        var outstanding = CreateRecord("<cp-fail2-o@example.test>", "o\r\n");

        var journal = FileArticleJournal.Open(dir.Options);
        Assert.True(journal.TryAppendNewAccept(
            committed.ArtId,
            committed.ArtHash,
            committed.ArtSize,
            DateTimeOffset.UtcNow,
            committed.ArtData,
            out var cAccept,
            out _));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, cAccept.Sequence, new StoredArticleLocation(new SegmentId(1), 0, committed.ArtSize)),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, cAccept.Sequence),
                CancellationToken.None));
        Assert.True(journal.TryAppendNewAccept(
            outstanding.ArtId,
            outstanding.ArtHash,
            outstanding.ArtSize,
            DateTimeOffset.UtcNow,
            outstanding.ArtData,
            out var oAccept,
            out _));

        journal.CheckpointTestFault = point =>
        {
            if (point == FileArticleJournal.CheckpointFaultPoint.AfterMoveBeforeReopen)
            {
                throw new IOException("injected-after-move");
            }
        };

        _ = Assert.ThrowsAny<Exception>(() => journal.CheckpointTruncateCommitted());

        // Instance must not remain apparently usable with divergent state.
        Assert.Throws<ObjectDisposedException>(() => journal.TryGetOutstanding(outstanding.ArtId, out _));

        // Fresh open sees the installed replacement (outstanding preserved; committed truncated).
        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.True(reopened.TryGetOutstanding(outstanding.ArtId, out var recovered));
        Assert.Equal(oAccept.Sequence, recovered.Sequence);
        Assert.Equal(outstanding.ArtSize, reopened.OutstandingRecoverableBytes);
        Assert.True(reopened.NextSequence > oAccept.Sequence);
    }

    [Fact]
    public async Task Checkpoint_Repeated_RemainsCorrectAfterReopen()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<cp-rep-1@example.test>", "1\r\n");
        var second = CreateRecord("<cp-rep-2@example.test>", "2\r\n");
        ulong nextAfter;

        using (var journalA = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journalA.TryAppendNewAccept(
                first.ArtId,
                first.ArtHash,
                first.ArtSize,
                DateTimeOffset.UtcNow,
                first.ArtData,
                out var a1,
                out _));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, a1.Sequence, new StoredArticleLocation(new SegmentId(1), 0, first.ArtSize)),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendIndexCommittedAsync(
                    new JournalIndexCommittedRecord(1, a1.Sequence),
                    CancellationToken.None));
            _ = journalA.CheckpointTruncateCommitted();

            Assert.True(journalA.TryAppendNewAccept(
                second.ArtId,
                second.ArtHash,
                second.ArtSize,
                DateTimeOffset.UtcNow,
                second.ArtData,
                out var a2,
                out _));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, a2.Sequence, new StoredArticleLocation(new SegmentId(1), first.ArtSize, second.ArtSize)),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journalA.AppendIndexCommittedAsync(
                    new JournalIndexCommittedRecord(1, a2.Sequence),
                    CancellationToken.None));
            nextAfter = journalA.NextSequence;
            _ = journalA.CheckpointTruncateCommitted();
            Assert.Equal(0, journalA.CheckpointTruncateCommitted());
            Assert.Equal(nextAfter, journalA.NextSequence);
            Assert.Equal(0, journalA.OutstandingRecoverableBytes);
        }

        using var journalB = FileArticleJournal.Open(dir.Options);
        Assert.Equal(nextAfter, journalB.NextSequence);
        Assert.Equal(0, journalB.OutstandingRecoverableBytes);
        Assert.Empty(journalB.EnumerateIncomplete());
    }

    private static void PoisonFirstFrameLength(string path, uint length)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), length);
        File.WriteAllBytes(path, bytes);
    }

    private static void CorruptFinalFrameChecksum(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 4);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    private static void CorruptFirstFrameChecksumLeaveTrailing(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 8);
        var firstLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        Assert.True(firstLength >= ArticleJournalFrameCodec.MinimumFrameLength);
        Assert.True(firstLength < bytes.Length);
        bytes[(int)firstLength - 1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    private static byte[] CreateExactSizeArtData(int size)
    {
        var prefix = Encoding.ASCII.GetBytes(
            "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: <n-max@example.test>\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: max\r\n\r\n");
        if (prefix.Length > size)
        {
            throw new InvalidOperationException("Prefix larger than requested size.");
        }

        var data = new byte[size];
        prefix.CopyTo(data.AsSpan());
        data.AsSpan(prefix.Length).Fill((byte)'x');
        return data;
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
        _ = builder.Append("Subject: storage\r\n");
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

        public string ControlDir => Options.ControlDir;

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempControlDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-journal-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            Directory.CreateDirectory(control);
            var options = new ArticleStorageRuntimeOptions(
                ControlDir: control,
                SegmentDir: Path.Combine(root, "cache"),
                JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
            return new TempControlDir(root, options);
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
                // Best-effort cleanup for locked temp files.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
