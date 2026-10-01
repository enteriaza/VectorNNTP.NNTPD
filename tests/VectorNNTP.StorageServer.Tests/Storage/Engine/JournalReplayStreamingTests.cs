using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Startup replay reads one legal frame at a time and keeps the previous journal result.
/// </summary>
public sealed class JournalReplayStreamingTests
{
    [Fact]
    public void EmptyJournal_ReplaysWithoutAFrameBuffer()
    {
        using var dir = TempControlDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        Assert.Equal(0, journal.JournalPhysicalBytes);
        Assert.Equal(1UL, journal.NextSequence);
        Assert.Equal(0, journal.OutstandingRecoverableBytes);
        Assert.Empty(journal.EnumerateIncomplete());
        Assert.Equal(0, journal.LargestReplayFrameBufferBytes);
    }

    [Fact]
    public void SingleAccept_ReplaysTheSameRecord()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<stream-one@example.test>", "one\r\n");
        JournalAcceptRecord written;
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journal.TryAppendNewAccept(
                record.ArtId,
                record.ArtHash,
                record.ArtSize,
                DateTimeOffset.UtcNow,
                record.ArtData,
                out written,
                out _));
        }

        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.Equal(written.Sequence + 1, reopened.NextSequence);
        Assert.Equal(record.ArtSize, reopened.OutstandingRecoverableBytes);
        var recovered = Assert.Single(reopened.EnumerateIncomplete());
        Assert.Equal(written.Sequence, recovered.Accept.Sequence);
        Assert.True(record.ArtData.Span.SequenceEqual(recovered.Accept.ArtData.Span));
        Assert.Equal(reopened.JournalPhysicalBytes, reopened.LargestReplayFrameBufferBytes);
        Assert.True(reopened.LargestReplayFrameBufferBytes <= ArticleJournalFrameCodec.MaxFrameLength);
    }

    [Fact]
    public void MultipleAccepts_ReplayBufferIsTheLargestFrame()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<stream-a@example.test>", "aa\r\n");
        var second = CreateRecord("<stream-b@example.test>", "bbbbbbbb\r\n");
        var third = CreateRecord("<stream-c@example.test>", "c\r\n");
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, first, out _));
            Assert.True(Append(journal, second, out _));
            Assert.True(Append(journal, third, out _));
            Assert.Equal(4UL, journal.NextSequence);
            Assert.Equal(first.ArtSize + second.ArtSize + third.ArtSize, journal.OutstandingRecoverableBytes);
        }

        var path = JournalPath(dir);
        var fileLength = new FileInfo(path).Length;
        var largestFrame = LargestDeclaredFrame(path);
        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.Equal(4UL, reopened.NextSequence);
        Assert.Equal(first.ArtSize + second.ArtSize + third.ArtSize, reopened.OutstandingRecoverableBytes);
        Assert.Equal(3, reopened.EnumerateIncomplete().Count);
        AssertFrameSizedReplay(reopened, fileLength, largestFrame);
    }

    [Fact]
    public async Task AcceptPhysicalWrittenAndIndexCommitted_ReplaysTheSameOutstandingState()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<stream-committed@example.test>", "committed\r\n");
        var location = new StoredArticleLocation(new SegmentId(4), 80, record.ArtSize);
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, record, out var accept));
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
            Assert.Equal(0, journal.OutstandingRecoverableBytes);
            Assert.False(journal.TryGetOutstanding(record.ArtId, out _));
            Assert.Equal(2UL, journal.NextSequence);
        }

        var path = JournalPath(dir);
        var fileLength = new FileInfo(path).Length;
        var largestFrame = LargestDeclaredFrame(path);
        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.Equal(2UL, reopened.NextSequence);
        Assert.Equal(0, reopened.OutstandingRecoverableBytes);
        Assert.False(reopened.TryGetOutstanding(record.ArtId, out _));
        Assert.Empty(reopened.EnumerateIncomplete());
        AssertFrameSizedReplay(reopened, fileLength, largestFrame);
    }

    [Fact]
    public async Task SequenceFence_PreservesNextSequence()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<stream-fence@example.test>", "fence\r\n");
        ulong next;
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, record, out var accept));
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
            next = journal.NextSequence;
            _ = journal.CheckpointTruncateCommitted();
            Assert.Equal(next, journal.NextSequence);
        }

        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.Equal(next, reopened.NextSequence);
        Assert.Equal(0, reopened.OutstandingRecoverableBytes);
        Assert.Empty(reopened.EnumerateIncomplete());
        Assert.True(reopened.LargestReplayFrameBufferBytes > 0);
        Assert.True(reopened.LargestReplayFrameBufferBytes <= ArticleJournalFrameCodec.MaxFrameLength);
    }

    [Fact]
    public async Task CompactionFrames_ReplayTheOpenCompaction()
    {
        using var dir = TempControlDir.Create();
        var art = CreateRecord("<stream-compact@example.test>");
        var begin = new JournalCompactionBeginRecord(1, 7, new SegmentId(3), 11);
        var source = new StoredArticleLocation(new SegmentId(3), 0, art.ArtSize);
        var destination = new StoredArticleLocation(new SegmentId(9), 40, art.ArtSize);
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendCompactionBeginAsync(begin, CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendRelocationIntentAsync(
                    new JournalRelocationIntentRecord(1, 7, 1, art.ArtId, art.ArtHash, art.ArtSize, source),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendRelocationWrittenAsync(
                    new JournalRelocationWrittenRecord(1, 7, 1, destination),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendCompactionCommittedAsync(
                    new JournalCompactionCommittedRecord(1, 7),
                    CancellationToken.None));
        }

        var path = JournalPath(dir);
        var fileLength = new FileInfo(path).Length;
        var largestFrame = LargestDeclaredFrame(path);
        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.True(reopened.TryGetCompaction(7, out var snap));
        Assert.Equal(begin.SourceSegmentId, snap.Begin.SourceSegmentId);
        Assert.Equal(begin.SourceGeneration, snap.Begin.SourceGeneration);
        Assert.True(snap.Committed);
        var relocation = Assert.Single(snap.Relocations);
        Assert.Equal(destination, relocation.Written!.Value.DestinationLocation);
        Assert.Equal(0, reopened.OutstandingRecoverableBytes);
        AssertFrameSizedReplay(reopened, fileLength, largestFrame);
    }

    [Fact]
    public async Task MixedFrames_ReplaySequenceAndCompactionTogether()
    {
        using var dir = TempControlDir.Create();
        var kept = CreateRecord("<stream-mix-keep@example.test>", "keep\r\n");
        var done = CreateRecord("<stream-mix-done@example.test>", "done\r\n");
        var begin = new JournalCompactionBeginRecord(1, 4, new SegmentId(2), 1);
        ulong keptSequence;
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, kept, out var keptAccept));
            keptSequence = keptAccept.Sequence;
            Assert.Equal(JournalAppendOutcome.Applied, await journal.AppendCompactionBeginAsync(begin, CancellationToken.None));
            Assert.True(Append(journal, done, out var doneAccept));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, doneAccept.Sequence, new StoredArticleLocation(new SegmentId(2), 0, done.ArtSize)),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendIndexCommittedAsync(
                    new JournalIndexCommittedRecord(1, doneAccept.Sequence),
                    CancellationToken.None));
        }

        var path = JournalPath(dir);
        var fileLength = new FileInfo(path).Length;
        var largestFrame = LargestDeclaredFrame(path);
        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.Equal(3UL, reopened.NextSequence);
        Assert.Equal(kept.ArtSize, reopened.OutstandingRecoverableBytes);
        var incomplete = Assert.Single(reopened.EnumerateIncomplete());
        Assert.Equal(keptSequence, incomplete.Accept.Sequence);
        Assert.True(kept.ArtData.Span.SequenceEqual(incomplete.Accept.ArtData.Span));
        Assert.False(reopened.TryGetOutstanding(done.ArtId, out _));
        Assert.True(reopened.TryGetCompaction(4, out var snap));
        Assert.False(snap.Committed);
        AssertFrameSizedReplay(reopened, fileLength, largestFrame);
    }

    [Fact]
    public void IncompleteFinalFrame_TruncatesToTheLastGoodOffset()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<stream-torn-1@example.test>", "keep\r\n");
        var second = CreateRecord("<stream-torn-2@example.test>", "torn\r\n");
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, first, out _));
            Assert.True(Append(journal, second, out _));
        }

        var path = JournalPath(dir);
        var bytes = File.ReadAllBytes(path);
        var firstLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length - 7).ToArray());

        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.True(reopened.TryGetOutstanding(first.ArtId, out _));
        Assert.False(reopened.TryGetOutstanding(second.ArtId, out _));
        Assert.Equal(first.ArtSize, reopened.OutstandingRecoverableBytes);
        Assert.Equal(firstLength, reopened.JournalPhysicalBytes);
        Assert.Equal(firstLength, reopened.LargestReplayFrameBufferBytes);
        Assert.True(reopened.LargestReplayFrameBufferBytes < bytes.Length);
    }

    [Fact]
    public void CorruptCompleteFrameBeforeEof_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<stream-mid-1@example.test>", "one\r\n");
        var second = CreateRecord("<stream-mid-2@example.test>", "two\r\n");
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, first, out _));
            Assert.True(Append(journal, second, out _));
        }

        var path = JournalPath(dir);
        CorruptFirstFrameChecksum(path);
        var corrupted = File.ReadAllBytes(path);
        var ex = Assert.Throws<ArticleJournalCorruptException>(() => FileArticleJournal.Open(dir.Options));
        Assert.Contains("trailing bytes", ex.Message, StringComparison.Ordinal);
        Assert.Equal(corrupted, File.ReadAllBytes(path));
    }

    [Fact]
    public void CorruptFinalFrameAtEof_TruncatesTheTornTail()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<stream-tail-1@example.test>", "keep\r\n");
        var second = CreateRecord("<stream-tail-2@example.test>", "bad\r\n");
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, first, out _));
            Assert.True(Append(journal, second, out _));
        }

        var path = JournalPath(dir);
        var bytes = File.ReadAllBytes(path);
        var firstLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.True(reopened.TryGetOutstanding(first.ArtId, out _));
        Assert.False(reopened.TryGetOutstanding(second.ArtId, out _));
        Assert.Equal(first.ArtSize, reopened.OutstandingRecoverableBytes);
        Assert.Equal(firstLength, reopened.JournalPhysicalBytes);
    }

    [Fact]
    public void CorruptFrameWithTrailingBytes_FailsClosedAndLeavesTheFile()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<stream-trail-1@example.test>", "one\r\n");
        var second = CreateRecord("<stream-trail-2@example.test>", "two\r\n");
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, first, out _));
            Assert.True(Append(journal, second, out _));
        }

        var path = JournalPath(dir);
        CorruptFirstFrameChecksum(path);
        var before = new FileInfo(path).Length;
        var ex = Assert.Throws<ArticleJournalCorruptException>(() => FileArticleJournal.Open(dir.Options));
        Assert.Contains("trailing bytes", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, new FileInfo(path).Length);
    }

    [Fact]
    public void InvalidFrameLength_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<stream-length@example.test>", "len\r\n");
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, record, out _));
        }

        var path = JournalPath(dir);
        var before = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(before.AsSpan(0, 4), uint.MaxValue);
        File.WriteAllBytes(path, before);

        var ex = Assert.Throws<ArticleJournalCorruptException>(() => FileArticleJournal.Open(dir.Options));
        Assert.Contains("corrupt length", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void DuplicateAcceptSequence_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<stream-dup@example.test>", "dup\r\n");
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, record, out _));
        }

        var path = JournalPath(dir);
        var frame = File.ReadAllBytes(path);
        File.WriteAllBytes(path, [.. frame, .. frame]);
        var ex = Assert.Throws<ArticleJournalCorruptException>(() => FileArticleJournal.Open(dir.Options));
        Assert.Contains("Duplicate Accept sequence", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictingPhysicalWritten_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<stream-conflict@example.test>", "conflict\r\n");
        ulong sequence;
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, record, out var accept));
            sequence = accept.Sequence;
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, sequence, new StoredArticleLocation(new SegmentId(1), 0, record.ArtSize)),
                    CancellationToken.None));
        }

        var extra = ArticleJournalFrameCodec.EncodePhysicalWritten(
            new JournalPhysicalWrittenRecord(1, sequence, new StoredArticleLocation(new SegmentId(2), 10, record.ArtSize)));
        File.AppendAllBytes(JournalPath(dir), extra);
        var ex = Assert.Throws<ArticleJournalCorruptException>(() => FileArticleJournal.Open(dir.Options));
        Assert.Contains("Conflicting PhysicalWritten", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousFlush_NoBytes_StaysAbsentAfterRestart()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<stream-absent@example.test>", "absent\r\n");
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            journal.TestAfterWriteBeforeFlush = (stream, start, _) => stream.SetLength(start);
            journal.TestBeforeDurableFlush = () => throw new IOException("flush");
            var ex = Assert.Throws<IOException>(() => Append(journal, record, out _));
            Assert.DoesNotContain("not durable", ex.Message, StringComparison.Ordinal);
        }

        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.False(reopened.TryGetOutstanding(record.ArtId, out _));
        Assert.Equal(0, reopened.JournalPhysicalBytes);
        Assert.Equal(1UL, reopened.NextSequence);
        Assert.Equal(0, reopened.LargestReplayFrameBufferBytes);
    }

    [Fact]
    public void AmbiguousFlush_CompleteFrame_IsReplayedAfterRestart()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<stream-flushed@example.test>", "flushed\r\n");
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            journal.TestBeforeDurableFlush = () => throw new IOException("flush");
            var ex = Assert.Throws<UnreconciledDurableTailException>(() => Append(journal, record, out _));
            Assert.Contains("not durable", ex.Message, StringComparison.Ordinal);
        }

        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.True(reopened.TryGetOutstanding(record.ArtId, out var recovered));
        Assert.True(record.ArtData.Span.SequenceEqual(recovered.ArtData.Span));
        Assert.Equal(record.ArtSize, reopened.OutstandingRecoverableBytes);
        Assert.Equal(2UL, reopened.NextSequence);
        Assert.Equal(reopened.JournalPhysicalBytes, reopened.LargestReplayFrameBufferBytes);
    }

    [Fact]
    public void AmbiguousFlush_IncompleteFinalFrame_TruncatesOnRestart()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<stream-partial-flush@example.test>", "partial\r\n");
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, record, out _));
        }

        var path = JournalPath(dir);
        var durableLength = new FileInfo(path).Length;
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.Write(new byte[] { 1, 2, 3 }, 0, 3);
        }

        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.True(reopened.TryGetOutstanding(record.ArtId, out _));
        Assert.Equal(durableLength, reopened.JournalPhysicalBytes);
        Assert.Equal(durableLength, reopened.LargestReplayFrameBufferBytes);
    }

    [Fact]
    public void JournalLargerThanIntMaxValue_FailsClosedWithTheSameCeiling()
    {
        using var dir = TempControlDir.Create();
        var path = JournalPath(dir);
        var ceiling = (long)int.MaxValue + 1;
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(ceiling);
        }

        var ex = Assert.Throws<ArticleJournalCorruptException>(() => FileArticleJournal.Open(dir.Options));
        Assert.Contains($"exceeds supported size ({ceiling} bytes)", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0L, ex.Offset);
        Assert.Equal(ceiling, new FileInfo(path).Length);
    }

    [Fact]
    public async Task MultiAcceptSequenceState_MatchesThePreRestartJournal()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<stream-state-1@example.test>", "one-body\r\n");
        var second = CreateRecord("<stream-state-2@example.test>", "two-body-longer\r\n");
        var third = CreateRecord("<stream-state-3@example.test>", "three\r\n");
        var location = new StoredArticleLocation(new SegmentId(8), 100, second.ArtSize);
        ulong next;
        long outstanding;
        JournalIncompleteSequence[] before;
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, first, out _));
            Assert.True(Append(journal, second, out var secondAccept));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, secondAccept.Sequence, location),
                    CancellationToken.None));
            Assert.True(Append(journal, third, out var thirdAccept));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, thirdAccept.Sequence, new StoredArticleLocation(new SegmentId(8), 200, third.ArtSize)),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendIndexCommittedAsync(
                    new JournalIndexCommittedRecord(1, thirdAccept.Sequence),
                    CancellationToken.None));
            next = journal.NextSequence;
            outstanding = journal.OutstandingRecoverableBytes;
            before = journal.EnumerateIncomplete().ToArray();
        }

        var path = JournalPath(dir);
        var fileLength = new FileInfo(path).Length;
        var largestFrame = LargestDeclaredFrame(path);
        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.Equal(next, reopened.NextSequence);
        Assert.Equal(outstanding, reopened.OutstandingRecoverableBytes);
        Assert.Equal(first.ArtSize + second.ArtSize, outstanding);
        var after = reopened.EnumerateIncomplete();
        Assert.Equal(before.Length, after.Count);
        for (var i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i].Accept.Sequence, after[i].Accept.Sequence);
            Assert.Equal(before[i].Accept.ArtId, after[i].Accept.ArtId);
            Assert.Equal(before[i].Accept.ArtHash, after[i].Accept.ArtHash);
            Assert.Equal(before[i].Accept.ArtSize, after[i].Accept.ArtSize);
            Assert.True(before[i].Accept.ArtData.Span.SequenceEqual(after[i].Accept.ArtData.Span));
            Assert.Equal(before[i].PhysicalWritten, after[i].PhysicalWritten);
        }

        Assert.Equal(location, after[1].PhysicalWritten!.Value.Location);
        AssertFrameSizedReplay(reopened, fileLength, largestFrame);
    }

    [Fact]
    public void FragmentedReads_ReplayTheSameSequenceState()
    {
        using var dir = TempControlDir.Create();
        var first = CreateRecord("<stream-frag-1@example.test>", "one\r\n");
        var second = CreateRecord("<stream-frag-2@example.test>", "two-longer\r\n");
        JournalIncompleteSequence[] normal;
        ulong next;
        long outstanding;
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, first, out _));
            Assert.True(Append(journal, second, out _));
        }

        var path = JournalPath(dir);
        var fileLength = new FileInfo(path).Length;
        var firstFrame = (int)ReadLengthPrefix(path);
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            next = journal.NextSequence;
            outstanding = journal.OutstandingRecoverableBytes;
            normal = journal.EnumerateIncomplete().ToArray();
        }

        var reads = new List<ReplayRead>();
        using var fragmented = FileArticleJournal.Open(
            dir.Options,
            requested =>
            {
                var allowed = requested > 0 ? 1 : 0;
                reads.Add(new ReplayRead(requested, allowed));
                return allowed;
            });

        Assert.Equal(fileLength, reads.Count);
        Assert.Equal(new ReplayRead(4, 1), reads[0]);
        Assert.Equal(new ReplayRead(3, 1), reads[1]);
        Assert.Equal(new ReplayRead(2, 1), reads[2]);
        Assert.Equal(new ReplayRead(1, 1), reads[3]);
        Assert.True(reads[4].Requested > 1);
        Assert.Equal(1, reads[4].Allowed);
        Assert.Equal(1, reads[5].Allowed);
        Assert.Equal(new ReplayRead(4, 1), reads[firstFrame]);
        Assert.Equal(next, fragmented.NextSequence);
        Assert.Equal(outstanding, fragmented.OutstandingRecoverableBytes);
        var after = fragmented.EnumerateIncomplete();
        Assert.Equal(normal.Length, after.Count);
        for (var i = 0; i < normal.Length; i++)
        {
            Assert.Equal(normal[i].Accept.Sequence, after[i].Accept.Sequence);
            Assert.Equal(normal[i].Accept.ArtId, after[i].Accept.ArtId);
            Assert.Equal(normal[i].Accept.ArtHash, after[i].Accept.ArtHash);
            Assert.Equal(normal[i].Accept.ArtSize, after[i].Accept.ArtSize);
            Assert.True(normal[i].Accept.ArtData.Span.SequenceEqual(after[i].Accept.ArtData.Span));
            Assert.Equal(normal[i].PhysicalWritten, after[i].PhysicalWritten);
        }
    }

    [Fact]
    public void ZeroReadBeforeSpanComplete_FailsOpenWithoutApplying()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<stream-zero-read@example.test>", "keep\r\n");
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, record, out _));
        }

        var path = JournalPath(dir);
        var before = File.ReadAllBytes(path);
        var calls = 0;
        var ex = Assert.Throws<IOException>(() => FileArticleJournal.Open(
            dir.Options,
            _ =>
            {
                calls++;
                return calls == 1 ? 1 : 0;
            }));
        Assert.Contains("Short read", ex.Message, StringComparison.Ordinal);
        Assert.True(calls >= 2);
        Assert.Equal(before, File.ReadAllBytes(path));

        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.True(reopened.TryGetOutstanding(record.ArtId, out var recovered));
        Assert.True(record.ArtData.Span.SequenceEqual(recovered.ArtData.Span));
        Assert.Equal(record.ArtSize, reopened.OutstandingRecoverableBytes);
        Assert.Equal(2UL, reopened.NextSequence);
    }

    [Fact]
    public void MaximumAcceptFrame_ReplaysAtMaxFrameLength()
    {
        using var dir = TempControlDir.Create();
        var artData = CreateExactSizeArtData(ArticleResourceLimits.MaxArticleBytes);
        var artId = ArticleId.FromMessageId("<stream-max@example.test>"u8);
        var artHash = XxHash3.HashToUInt64(artData);
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(journal.TryAppendNewAccept(
                artId,
                artHash,
                artData.Length,
                DateTimeOffset.UtcNow,
                artData,
                out var accept,
                out var outcome));
            Assert.Equal(default, outcome);
            Assert.Equal(1UL, accept.Sequence);
        }

        var path = JournalPath(dir);
        Assert.Equal((uint)ArticleJournalFrameCodec.MaxFrameLength, ReadLengthPrefix(path));
        Assert.Equal(ArticleJournalFrameCodec.MaxFrameLength, new FileInfo(path).Length);

        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.Equal(2UL, reopened.NextSequence);
        Assert.Equal(artData.Length, reopened.OutstandingRecoverableBytes);
        var recovered = Assert.Single(reopened.EnumerateIncomplete());
        Assert.Equal(1UL, recovered.Accept.Sequence);
        Assert.Equal(artId, recovered.Accept.ArtId);
        Assert.Equal(artHash, recovered.Accept.ArtHash);
        Assert.Equal(artData.Length, recovered.Accept.ArtSize);
        Assert.True(artData.AsSpan().SequenceEqual(recovered.Accept.ArtData.Span));
        Assert.Null(recovered.PhysicalWritten);
        Assert.Equal(ArticleJournalFrameCodec.MaxFrameLength, reopened.LargestReplayFrameBufferBytes);
    }

    [Fact]
    public void LengthOneAboveMaxFrame_FailsClosedWithoutAFrameBuffer()
    {
        using var dir = TempControlDir.Create();
        var record = CreateRecord("<stream-over-max@example.test>", "x\r\n");
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            Assert.True(Append(journal, record, out _));
        }

        var path = JournalPath(dir);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(0, 4),
            (uint)(ArticleJournalFrameCodec.MaxFrameLength + 1));
        File.WriteAllBytes(path, bytes);

        var requests = new List<int>();
        var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<ArticleJournalCorruptException>(() => FileArticleJournal.Open(
            dir.Options,
            requested =>
            {
                requests.Add(requested);
                return requested;
            }));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        Assert.Contains("corrupt length", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0L, ex.Offset);
        Assert.Equal([4], requests);
        Assert.True(allocated < ArticleJournalFrameCodec.MaxFrameLength, $"allocated {allocated} bytes");
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private readonly record struct ReplayRead(int Requested, int Allowed);

    private static uint ReadLengthPrefix(string path)
    {
        Span<byte> prefix = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        Assert.Equal(4, stream.Read(prefix));
        return BinaryPrimitives.ReadUInt32LittleEndian(prefix);
    }

    private static byte[] CreateExactSizeArtData(int size)
    {
        var prefix = Encoding.ASCII.GetBytes(
            "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: <stream-max@example.test>\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: max\r\n\r\n");
        var data = new byte[size];
        prefix.CopyTo(data.AsSpan());
        data.AsSpan(prefix.Length).Fill((byte)'x');
        return data;
    }

    private static void AssertFrameSizedReplay(FileArticleJournal journal, long fileLength, int largestFrame)
    {
        Assert.Equal(fileLength, journal.JournalPhysicalBytes);
        Assert.True(fileLength > largestFrame);
        Assert.Equal(largestFrame, journal.LargestReplayFrameBufferBytes);
        Assert.True(journal.LargestReplayFrameBufferBytes <= ArticleJournalFrameCodec.MaxFrameLength);
    }

    private static int LargestDeclaredFrame(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var largest = 0;
        var offset = 0;
        while (offset < bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            Assert.InRange(length, (uint)ArticleJournalFrameCodec.MinimumFrameLength, (uint)ArticleJournalFrameCodec.MaxFrameLength);
            offset += (int)length;
            largest = Math.Max(largest, (int)length);
        }

        Assert.Equal(bytes.Length, offset);
        return largest;
    }

    private static void CorruptFirstFrameChecksum(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var firstLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        bytes[(int)firstLength - 1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    private static bool Append(FileArticleJournal journal, ArticleRecord record, out JournalAcceptRecord accept) =>
        journal.TryAppendNewAccept(
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            DateTimeOffset.UtcNow,
            record.ArtData,
            out accept,
            out _);

    private static string JournalPath(TempControlDir dir) =>
        Path.Combine(dir.ControlDir, FileArticleJournal.JournalFileName);

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

        public string ControlDir => Options.ControlDir;

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempControlDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-stream-" + Guid.NewGuid().ToString("N"));
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
            }
        }

        private string Root { get; }
    }
}
