using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

public sealed class FileArticleIndexSnapshotReplayTests
{
    [Fact]
    public void LegacyIndex_WithoutSnapshot_ReplaysFromZero()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<legacy@example.test>", 1, 10, 1, 0, 40);
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(meta));
        }

        Assert.False(File.Exists(SnapshotPath(dir)));
        using var reopened = FileArticleIndex.Open(dir.Options);
        Assert.True(reopened.TryGet(meta.ArtId, out var got));
        Assert.Equal(meta, got);
    }

    [Fact]
    public void EmptyDelta_StartupMatchesFullReplay()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<empty-delta@example.test>", 4, 10, 2, 8, 40);
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(meta));
            _ = index.WriteSnapshot();
        }

        var before = File.ReadAllBytes(IndexPath(dir));
        var withSnapshot = Projection(dir);
        Assert.Equal(before, File.ReadAllBytes(IndexPath(dir)));
        AssertEqual(withSnapshot, ProjectionWithoutSnapshot(dir));
        Assert.Equal(meta, withSnapshot[meta.ArtId]);
    }

    [Fact]
    public void OneDeltaFrame_StartupMatchesFullReplay()
    {
        using var dir = TempControlDir.Create();
        var first = Present("<one-delta-a@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<one-delta-b@example.test>", 2, 11, 1, 40, 44);
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(first));
            _ = index.WriteSnapshot();
            Assert.True(index.TryCommitPresent(second));
        }

        var before = File.ReadAllBytes(IndexPath(dir));
        var withSnapshot = Projection(dir);
        Assert.Equal(before, File.ReadAllBytes(IndexPath(dir)));
        Assert.Equal(ArticleIndexRecordCodec.RecordLength * 2, before.Length);
        AssertEqual(withSnapshot, ProjectionWithoutSnapshot(dir));
        Assert.Equal(first, withSnapshot[first.ArtId]);
        Assert.Equal(second, withSnapshot[second.ArtId]);
    }

    [Fact]
    public void StateTransitions_SnapshotPlusDelta_MatchFullReplay()
    {
        using var dir = TempControlDir.Create();
        var source = new StoredArticleLocation(new SegmentId(1), 0, 40);
        var moved = new StoredArticleLocation(new SegmentId(2), 100, 40);
        var movedAgain = new StoredArticleLocation(new SegmentId(3), 200, 40);
        var kept = Present("<st-kept@example.test>", 1, 10, 1, 0, 40);
        var toEvict = Present("<st-evict@example.test>", 2, 10, 1, 40, 40);
        var toInvalid = Present("<st-invalid@example.test>", 3, 10, 1, 80, 40);
        var toMove = Present("<st-move@example.test>", 4, 10, source);
        var evictedThenAccepted = Present("<st-reaccept-e@example.test>", 5, 10, 1, 120, 40);
        var invalidThenAccepted = Present("<st-reaccept-i@example.test>", 6, 10, 1, 160, 40);
        var moveThenEvict = Present("<st-move-evict@example.test>", 7, 10, 1, 200, 40);
        var evictAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var invalidAt = new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero);
        var reacceptEvicted = evictedThenAccepted with
        {
            ArtHash = 50,
            Location = new StoredArticleLocation(new SegmentId(8), 1, 40),
        };
        var reacceptInvalid = invalidThenAccepted with
        {
            ArtHash = 60,
            Location = new StoredArticleLocation(new SegmentId(9), 2, 40),
        };

        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(kept));
            Assert.True(index.TryCommitPresent(toEvict));
            Assert.True(index.TryCommitPresent(toInvalid));
            Assert.True(index.TryCommitPresent(toMove));
            Assert.True(index.TryCommitPresent(evictedThenAccepted));
            Assert.True(index.TrySetState(evictedThenAccepted.ArtId, ArticleStorageState.Evicted, evictAt));
            Assert.True(index.TryCommitPresent(invalidThenAccepted));
            Assert.True(index.TrySetState(invalidThenAccepted.ArtId, ArticleStorageState.Invalid, invalidAt));
            Assert.True(index.TryCommitPresent(moveThenEvict));
            _ = index.WriteSnapshot();

            Assert.True(index.TrySetState(toEvict.ArtId, ArticleStorageState.Evicted, evictAt));
            Assert.True(index.TrySetState(toInvalid.ArtId, ArticleStorageState.Invalid, invalidAt));
            Assert.Equal(ArticleRelocateOutcome.Relocated, index.TryRelocate(toMove.ArtId, source, moved, 4, 10));
            Assert.Equal(ArticleRelocateOutcome.Relocated, index.TryRelocate(toMove.ArtId, moved, movedAgain, 4, 10));
            Assert.True(index.TryCommitPresent(reacceptEvicted));
            Assert.True(index.TryCommitPresent(reacceptInvalid));
            Assert.Equal(
                ArticleRelocateOutcome.Relocated,
                index.TryRelocate(moveThenEvict.ArtId, moveThenEvict.Location, moved, 7, 10));
            Assert.True(index.TrySetState(moveThenEvict.ArtId, ArticleStorageState.Evicted, evictAt));
        }

        var before = File.ReadAllBytes(IndexPath(dir));
        var withSnapshot = Projection(dir);
        Assert.Equal(before, File.ReadAllBytes(IndexPath(dir)));
        AssertEqual(withSnapshot, ProjectionWithoutSnapshot(dir));
        Assert.Equal(ArticleStorageState.Present, withSnapshot[kept.ArtId].State);
        Assert.Equal(ArticleStorageState.Evicted, withSnapshot[toEvict.ArtId].State);
        Assert.Equal(ArticleStorageState.Invalid, withSnapshot[toInvalid.ArtId].State);
        Assert.Equal(movedAgain, withSnapshot[toMove.ArtId].Location);
        Assert.Equal(ArticleStorageState.Present, withSnapshot[reacceptEvicted.ArtId].State);
        Assert.Equal(50UL, withSnapshot[reacceptEvicted.ArtId].ArtHash);
        Assert.Equal(ArticleStorageState.Present, withSnapshot[reacceptInvalid.ArtId].State);
        Assert.Equal(ArticleStorageState.Evicted, withSnapshot[moveThenEvict.ArtId].State);
        Assert.Equal(moved, withSnapshot[moveThenEvict.ArtId].Location);
    }

    [Fact]
    public void CorruptSnapshot_FailsClosed_AndLeavesTheIndexUntouched()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<corrupt-snap@example.test>", 1, 10, 1, 0, 40);
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(meta));
            _ = index.WriteSnapshot();
        }

        var indexBytes = File.ReadAllBytes(IndexPath(dir));
        var snapshotBytes = File.ReadAllBytes(SnapshotPath(dir));

        CorruptHeaderCrc(SnapshotPath(dir));
        var header = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("header CRC", header.Message, StringComparison.Ordinal);
        Assert.Equal(indexBytes, File.ReadAllBytes(IndexPath(dir)));

        File.WriteAllBytes(SnapshotPath(dir), snapshotBytes);
        CorruptFrame(SnapshotPath(dir));
        var frame = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("corrupt frame", frame.Message, StringComparison.Ordinal);
        Assert.Equal(indexBytes, File.ReadAllBytes(IndexPath(dir)));

        File.WriteAllBytes(SnapshotPath(dir), snapshotBytes);
        CorruptTrailer(SnapshotPath(dir));
        var trailer = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("trailer CRC", trailer.Message, StringComparison.Ordinal);
        Assert.Equal(indexBytes, File.ReadAllBytes(IndexPath(dir)));
        Assert.True(File.Exists(SnapshotPath(dir)));
    }

    [Fact]
    public void CoverageBeyondIndexOrUnaligned_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(Present("<coverage@example.test>", 1, 10, 1, 0, 40)));
            _ = index.WriteSnapshot();
        }

        var indexBytes = File.ReadAllBytes(IndexPath(dir));
        var snapshotBytes = File.ReadAllBytes(SnapshotPath(dir));
        PatchCoveredLength(SnapshotPath(dir), indexBytes.Length + ArticleIndexRecordCodec.RecordLength);
        var beyond = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("exceeds index length", beyond.Message, StringComparison.Ordinal);
        Assert.Equal(indexBytes, File.ReadAllBytes(IndexPath(dir)));

        File.WriteAllBytes(SnapshotPath(dir), snapshotBytes);
        PatchCoveredLength(SnapshotPath(dir), 4);
        var unaligned = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("not aligned", unaligned.Message, StringComparison.Ordinal);
        Assert.Equal(indexBytes, File.ReadAllBytes(IndexPath(dir)));
    }

    [Fact]
    public void CorruptDeltaFrame_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(Present("<delta-crc-a@example.test>", 1, 10, 1, 0, 40)));
            _ = index.WriteSnapshot();
            Assert.True(index.TryCommitPresent(Present("<delta-crc-b@example.test>", 2, 10, 1, 40, 40)));
        }

        var bytes = File.ReadAllBytes(IndexPath(dir));
        bytes[^5] ^= 0xFF;
        File.WriteAllBytes(IndexPath(dir), bytes);

        var ex = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes.Length, new FileInfo(IndexPath(dir)).Length);
        Assert.True(File.Exists(SnapshotPath(dir)));
    }

    [Fact]
    public void TornDeltaTail_TruncatesOnlyTheTail_AndMatchesFullReplay()
    {
        using var withSnapshot = TempControlDir.Create();
        using var fullReplay = TempControlDir.Create();
        var first = Present("<torn-a@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<torn-b@example.test>", 2, 10, 1, 40, 40);
        using (var index = FileArticleIndex.Open(withSnapshot.Options))
        {
            Assert.True(index.TryCommitPresent(first));
            _ = index.WriteSnapshot();
            Assert.True(index.TryCommitPresent(second));
        }

        var snapshotBytes = File.ReadAllBytes(SnapshotPath(withSnapshot));
        var indexBytes = File.ReadAllBytes(IndexPath(withSnapshot));
        var torn = indexBytes.AsSpan(0, indexBytes.Length - 7).ToArray();
        File.WriteAllBytes(IndexPath(withSnapshot), torn);
        File.WriteAllBytes(IndexPath(fullReplay), torn);

        var fromSnapshot = Projection(withSnapshot);
        Assert.Equal(snapshotBytes, File.ReadAllBytes(SnapshotPath(withSnapshot)));
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, new FileInfo(IndexPath(withSnapshot)).Length);
        Assert.True(fromSnapshot.ContainsKey(first.ArtId));
        Assert.False(fromSnapshot.ContainsKey(second.ArtId));

        var fromFullReplay = Projection(fullReplay);
        AssertEqual(fromSnapshot, fromFullReplay);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, new FileInfo(IndexPath(fullReplay)).Length);
    }

    [Fact]
    public void ReplayStartAboveIntMaxValue_IsNotNarrowed()
    {
        var meta = Present("<long-delta@example.test>", 9, 10, 1, 0, 40);
        var frame = ArticleIndexRecordCodec.Encode(meta);
        var start = (long)int.MaxValue + ArticleIndexRecordCodec.RecordLength;
        var stream = new LongOffsetStream(frame, start);
        var entries = new Dictionary<ArticleId, StoredArticleMetadata>();

        ArticleIndexReplayer.Replay(
            stream,
            start + frame.Length,
            entries,
            (_, _, error) => throw new ArticleIndexCorruptException(error.ToString()),
            start);

        Assert.Equal(start, stream.SeekOffset);
        Assert.True(stream.MaxRequested <= ArticleIndexRecordCodec.RecordLength);
        Assert.Equal(meta, entries[meta.ArtId]);
    }

    private static Dictionary<ArticleId, StoredArticleMetadata> Projection(TempControlDir dir)
    {
        using var index = FileArticleIndex.Open(dir.Options);
        return index.Snapshot().ToDictionary(static row => row.ArtId);
    }

    private static Dictionary<ArticleId, StoredArticleMetadata> ProjectionWithoutSnapshot(TempControlDir dir)
    {
        var path = SnapshotPath(dir);
        var aside = path + ".aside";
        File.Move(path, aside);
        try
        {
            return Projection(dir);
        }
        finally
        {
            File.Move(aside, path);
        }
    }

    private static void AssertEqual(
        Dictionary<ArticleId, StoredArticleMetadata> actual,
        Dictionary<ArticleId, StoredArticleMetadata> expected)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (id, row) in expected)
        {
            Assert.Equal(row, actual[id]);
        }
    }

    private static void CorruptHeaderCrc(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[ArticleIndexSnapshotCodec.HeaderLength - 1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    private static void CorruptFrame(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[ArticleIndexSnapshotCodec.HeaderLength + 10] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    private static void CorruptTrailer(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    private static void PatchCoveredLength(string path, long coveredIndexLength)
    {
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24, 8), (ulong)coveredIndexLength);
        var crc = Crc32.HashToUInt32(bytes.AsSpan(0, ArticleIndexSnapshotCodec.HeaderLength - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(ArticleIndexSnapshotCodec.HeaderLength - 4, 4), crc);
        File.WriteAllBytes(path, bytes);
    }

    private static string IndexPath(TempControlDir dir) =>
        Path.Combine(dir.ControlDir, FileArticleIndex.IndexFileName);

    private static string SnapshotPath(TempControlDir dir) =>
        Path.Combine(dir.ControlDir, FileArticleIndex.SnapshotFileName);

    private static ArticleId ArtId(string messageId) =>
        ArticleId.FromMessageId(Encoding.ASCII.GetBytes(messageId));

    private static StoredArticleMetadata Present(
        string messageId,
        ulong hash,
        int size,
        ulong segment,
        long offset,
        int length) =>
        Present(messageId, hash, size, new StoredArticleLocation(new SegmentId(segment), offset, length));

    private static StoredArticleMetadata Present(
        string messageId,
        ulong hash,
        int size,
        StoredArticleLocation location) =>
        new(
            ArtId(messageId),
            hash,
            size,
            location,
            ArticleStorageState.Present,
            new DateTimeOffset(2024, 8, 23, 7, 30, 10, TimeSpan.Zero));

    private sealed class LongOffsetStream : Stream
    {
        private readonly byte[] _frame;
        private readonly long _start;
        private long _position;
        private int _framePos;

        public LongOffsetStream(byte[] frame, long start)
        {
            _frame = frame;
            _start = start;
            Length = start + frame.Length;
        }

        public long SeekOffset { get; private set; } = -1;

        public int MaxRequested { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length { get; }

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            MaxRequested = Math.Max(MaxRequested, count);
            if (_position < _start || _framePos >= _frame.Length)
            {
                return 0;
            }

            var n = Math.Min(count, _frame.Length - _framePos);
            _frame.AsSpan(_framePos, n).CopyTo(buffer.AsSpan(offset, n));
            _framePos += n;
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin != SeekOrigin.Begin)
            {
                throw new NotSupportedException();
            }

            SeekOffset = offset;
            _position = offset;
            _framePos = 0;
            return offset;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-index-replay-" + Guid.NewGuid().ToString("N"));
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
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
