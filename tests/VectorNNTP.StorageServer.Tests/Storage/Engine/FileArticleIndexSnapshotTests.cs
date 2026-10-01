using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

public sealed class FileArticleIndexSnapshotTests
{
    [Fact]
    public void EmptyIndex_RoundTrips()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var before = index.CopyIndexBytes();
        var written = index.WriteSnapshot();

        Assert.Equal(1UL, written.Generation);
        Assert.Equal(ArticleIndexSnapshotCodec.LegacyDeltaGeneration, written.CoveredDeltaGeneration);
        Assert.Equal(0L, written.CoveredIndexLength);
        Assert.Equal(0UL, written.RecordCount);
        Assert.Equal(before, index.CopyIndexBytes());
        Assert.Equal(0, index.DurableWriteCount);

        var snapshot = ArticleIndexSnapshotCodec.Read(SnapshotPath(dir));
        Assert.Equal(written.Generation, snapshot.Generation);
        Assert.Empty(snapshot.Entries);
        Assert.False(File.Exists(TempPath(dir)));
    }

    [Fact]
    public void SinglePresent_RoundTripsEveryField()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<snap-one@example.test>", 11, 20, 3, 16, 64);
        Assert.True(index.TryCommitPresent(meta));
        var indexBytes = index.CopyIndexBytes();

        var written = index.WriteSnapshot();
        Assert.Equal(indexBytes, index.CopyIndexBytes());
        Assert.Equal(1, index.DurableWriteCount);
        Assert.Equal(indexBytes.Length, written.CoveredIndexLength);

        var snapshot = ArticleIndexSnapshotCodec.Read(SnapshotPath(dir));
        var row = Assert.Single(snapshot.Entries);
        Assert.Equal(meta, row);
    }

    [Fact]
    public void MultipleEntries_AreAllPresent()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var rows = new[]
        {
            Present("<snap-m1@example.test>", 1, 10, 1, 0, 40),
            Present("<snap-m2@example.test>", 2, 11, 1, 40, 41),
            Present("<snap-m3@example.test>", 3, 12, 2, 80, 42),
        };
        foreach (var row in rows)
        {
            Assert.True(index.TryCommitPresent(row));
        }

        _ = index.WriteSnapshot();
        var snapshot = ArticleIndexSnapshotCodec.Read(SnapshotPath(dir));
        Assert.Equal(rows.ToDictionary(static row => row.ArtId), snapshot.Entries.ToDictionary(static row => row.ArtId));
    }

    [Fact]
    public void LastWriteWins_SnapshotKeepsOnlyTheCurrentRow()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var source = new StoredArticleLocation(new SegmentId(1), 0, 40);
        var relocated = new StoredArticleLocation(new SegmentId(2), 128, 40);
        var original = Present("<snap-lww@example.test>", 11, 10, source);
        var reaccepted = original with
        {
            ArtHash = 77,
            Location = new StoredArticleLocation(new SegmentId(9), 900, 40),
        };

        Assert.True(index.TryCommitPresent(original));
        Assert.Equal(
            ArticleRelocateOutcome.Relocated,
            index.TryRelocate(original.ArtId, source, relocated, original.ArtHash, original.ArtSize));
        Assert.True(index.TrySetState(original.ArtId, ArticleStorageState.Evicted, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(index.TryCommitPresent(reaccepted));

        var indexLength = new FileInfo(index.IndexPath).Length;
        Assert.Equal(ArticleIndexRecordCodec.RecordLength * 4, indexLength);
        var written = index.WriteSnapshot();
        Assert.Equal(1UL, written.RecordCount);
        Assert.Equal(indexLength, written.CoveredIndexLength);
        Assert.Equal(indexLength, new FileInfo(index.IndexPath).Length);

        var row = Assert.Single(ArticleIndexSnapshotCodec.Read(SnapshotPath(dir)).Entries);
        Assert.Equal(ArticleStorageState.Present, row.State);
        Assert.Equal(77UL, row.ArtHash);
        Assert.Equal(reaccepted.Location, row.Location);
    }

    [Fact]
    public void EvictedInvalidAndRelocated_RoundTrip()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var evicted = Present("<snap-evict@example.test>", 1, 10, 1, 0, 40);
        var invalid = Present("<snap-invalid@example.test>", 2, 10, 1, 40, 40);
        var source = new StoredArticleLocation(new SegmentId(1), 80, 40);
        var destination = new StoredArticleLocation(new SegmentId(5), 10, 40);
        var moved = Present("<snap-move@example.test>", 3, 10, source);
        var evictAt = new DateTimeOffset(2026, 3, 1, 1, 2, 3, TimeSpan.Zero);
        var invalidAt = new DateTimeOffset(2026, 3, 2, 1, 2, 3, TimeSpan.Zero);

        Assert.True(index.TryCommitPresent(evicted));
        Assert.True(index.TryCommitPresent(invalid));
        Assert.True(index.TryCommitPresent(moved));
        Assert.True(index.TrySetState(evicted.ArtId, ArticleStorageState.Evicted, evictAt));
        Assert.True(index.TrySetState(invalid.ArtId, ArticleStorageState.Invalid, invalidAt));
        Assert.Equal(ArticleRelocateOutcome.Relocated, index.TryRelocate(moved.ArtId, source, destination, 3, 10));

        _ = index.WriteSnapshot();
        var byId = ArticleIndexSnapshotCodec.Read(SnapshotPath(dir)).Entries.ToDictionary(static row => row.ArtId);
        Assert.Equal(ArticleStorageState.Evicted, byId[evicted.ArtId].State);
        Assert.Equal(evictAt, byId[evicted.ArtId].LastAccessUtc);
        Assert.Equal(evicted.Location, byId[evicted.ArtId].Location);
        Assert.Equal(ArticleStorageState.Invalid, byId[invalid.ArtId].State);
        Assert.Equal(invalidAt, byId[invalid.ArtId].LastAccessUtc);
        Assert.Equal(ArticleStorageState.Present, byId[moved.ArtId].State);
        Assert.Equal(destination, byId[moved.ArtId].Location);
    }

    [Fact]
    public void TouchHint_IsCapturedWithoutADurableIndexWrite()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<snap-touch@example.test>", 1, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(meta));
        var hinted = new DateTimeOffset(2026, 4, 5, 6, 7, 8, TimeSpan.Zero);
        var writes = index.DurableWriteCount;
        var length = new FileInfo(index.IndexPath).Length;
        index.TouchHint(meta.ArtId, hinted);

        _ = index.WriteSnapshot();
        Assert.Equal(writes, index.DurableWriteCount);
        Assert.Equal(length, new FileInfo(index.IndexPath).Length);
        var row = Assert.Single(ArticleIndexSnapshotCodec.Read(SnapshotPath(dir)).Entries);
        Assert.Equal(hinted, row.LastAccessUtc);
        Assert.Equal(meta.ArtHash, row.ArtHash);
    }

    [Fact]
    public void HeaderCrc_TrailerCrc_Truncation_AndFrameCorruption_FailClosed()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        Assert.True(index.TryCommitPresent(Present("<snap-crc@example.test>", 1, 10, 1, 0, 40)));
        _ = index.WriteSnapshot();
        var path = SnapshotPath(dir);
        var original = File.ReadAllBytes(path);

        var header = original.ToArray();
        header[ArticleIndexSnapshotCodec.HeaderLength - 1] ^= 0xFF;
        File.WriteAllBytes(path, header);
        var headerEx = Assert.Throws<ArticleIndexCorruptException>(() => ArticleIndexSnapshotCodec.Read(path));
        Assert.Contains("header CRC", headerEx.Message, StringComparison.Ordinal);

        var trailer = original.ToArray();
        trailer[^1] ^= 0xFF;
        File.WriteAllBytes(path, trailer);
        var trailerEx = Assert.Throws<ArticleIndexCorruptException>(() => ArticleIndexSnapshotCodec.Read(path));
        Assert.Contains("trailer CRC", trailerEx.Message, StringComparison.Ordinal);

        var frame = original.ToArray();
        frame[ArticleIndexSnapshotCodec.HeaderLength + 10] ^= 0xFF;
        File.WriteAllBytes(path, frame);
        var frameEx = Assert.Throws<ArticleIndexCorruptException>(() => ArticleIndexSnapshotCodec.Read(path));
        Assert.Contains("corrupt frame", frameEx.Message, StringComparison.Ordinal);

        File.WriteAllBytes(path, original.AsSpan(0, original.Length - 1).ToArray());
        var truncated = Assert.Throws<ArticleIndexCorruptException>(() => ArticleIndexSnapshotCodec.Read(path));
        Assert.Contains("does not match record count", truncated.Message, StringComparison.Ordinal);

        File.WriteAllBytes(path, original);
        var restored = ArticleIndexSnapshotCodec.Read(path);
        Assert.Equal(1UL, restored.Generation);
    }

    [Fact]
    public void InstallFailure_LeavesIndexAndPreviousSnapshot()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<snap-fail@example.test>", 1, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(meta));
        var first = index.WriteSnapshot();
        var indexBytes = index.CopyIndexBytes();
        var snapBytes = File.ReadAllBytes(SnapshotPath(dir));

        index.TestBeforeSnapshotInstall = () => throw new IOException("snapshot install interrupted");
        var ex = Assert.Throws<IOException>(() => index.WriteSnapshot());
        Assert.Contains("interrupted", ex.Message, StringComparison.Ordinal);

        Assert.Equal(indexBytes, index.CopyIndexBytes());
        Assert.Equal(snapBytes, File.ReadAllBytes(SnapshotPath(dir)));
        Assert.False(File.Exists(TempPath(dir)));
        Assert.True(index.TryGet(meta.ArtId, out var got));
        Assert.Equal(meta, got);
        Assert.Equal(first.Generation, ArticleIndexSnapshotCodec.Read(SnapshotPath(dir)).Generation);
    }

    [Fact]
    public void LeftoverTemp_IsIgnoredByOpen()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<snap-tmp@example.test>", 4, 10, 1, 0, 40);
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(meta));
        }

        File.WriteAllBytes(TempPath(dir), [0x01, 0x02, 0x03, 0x04]);
        using var reopened = FileArticleIndex.Open(dir.Options);
        Assert.True(reopened.TryGet(meta.ArtId, out var got));
        Assert.Equal(meta, got);
        Assert.Equal(4, new FileInfo(TempPath(dir)).Length);
        _ = Assert.Throws<ArticleIndexCorruptException>(() => ArticleIndexSnapshotCodec.Read(TempPath(dir)));
    }

    [Fact]
    public void Open_ReplaysArticleIndex_WhenSnapshotIsStale()
    {
        using var dir = TempControlDir.Create();
        var first = Present("<snap-stale-a@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<snap-stale-b@example.test>", 2, 10, 1, 40, 40);
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(first));
            _ = index.WriteSnapshot();
            Assert.True(index.TryCommitPresent(second));
        }

        var snapshot = ArticleIndexSnapshotCodec.Read(SnapshotPath(dir));
        Assert.Single(snapshot.Entries);
        Assert.Equal(first.ArtId, snapshot.Entries[0].ArtId);

        using var reopened = FileArticleIndex.Open(dir.Options);
        Assert.True(reopened.TryGet(first.ArtId, out _));
        Assert.True(reopened.TryGet(second.ArtId, out _));
        Assert.Equal(ArticleIndexRecordCodec.RecordLength * 2, new FileInfo(reopened.IndexPath).Length);
    }

    [Fact]
    public void Generation_IncrementsAcrossRestart()
    {
        using var dir = TempControlDir.Create();
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.Equal(1UL, index.WriteSnapshot().Generation);
        }

        using var reopened = FileArticleIndex.Open(dir.Options);
        var second = reopened.WriteSnapshot();
        Assert.Equal(2UL, second.Generation);
        Assert.Equal(2UL, ArticleIndexSnapshotCodec.Read(SnapshotPath(dir)).Generation);
    }

    [Fact]
    public void AppendDuringSnapshot_StaysOutsideTheCapturedBoundary()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var first = Present("<snap-boundary-a@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<snap-boundary-b@example.test>", 2, 12, 1, 80, 48);
        Assert.True(index.TryCommitPresent(first));
        var covered = new FileInfo(index.IndexPath).Length;
        var writesBefore = index.DurableWriteCount;

        index.TestDuringSnapshotWrite = () => Assert.True(index.TryCommitPresent(second));
        var written = index.WriteSnapshot();

        Assert.Equal(covered, written.CoveredIndexLength);
        Assert.Equal(1UL, written.RecordCount);
        Assert.Equal(writesBefore + 1, index.DurableWriteCount);
        Assert.Equal(covered + ArticleIndexRecordCodec.RecordLength, new FileInfo(index.IndexPath).Length);
        var row = Assert.Single(ArticleIndexSnapshotCodec.Read(SnapshotPath(dir)).Entries);
        Assert.Equal(first.ArtId, row.ArtId);
        Assert.True(index.TryGet(second.ArtId, out var live));
        Assert.Equal(second, live);
    }

    [Fact]
    public async Task ConcurrentSnapshots_AreSingleFlight()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        Assert.True(index.TryCommitPresent(Present("<snap-flight@example.test>", 1, 10, 1, 0, 40)));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        index.TestDuringSnapshotWrite = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        index.TestOnSnapshotFlightContended = () => contended.TrySetResult();

        var first = Task.Run(() => index.WriteSnapshot());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Task.Run(() => index.WriteSnapshot());
        await contended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, index.MaxSnapshotWriters);

        release.Set();
        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, index.MaxSnapshotWriters);
        Assert.Equal(1UL, results[0].Generation);
        Assert.Equal(2UL, results[1].Generation);
        Assert.Equal(2UL, ArticleIndexSnapshotCodec.Read(SnapshotPath(dir)).Generation);
        Assert.False(File.Exists(TempPath(dir)));
    }

    private static string SnapshotPath(TempControlDir dir) =>
        Path.Combine(dir.ControlDir, FileArticleIndex.SnapshotFileName);

    private static string TempPath(TempControlDir dir) =>
        Path.Combine(dir.ControlDir, FileArticleIndex.SnapshotTempFileName);

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
            new DateTimeOffset(2024, 8, 23, 7, 30, 10, TimeSpan.Zero),
            1UL);

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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-index-snap-" + Guid.NewGuid().ToString("N"));
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
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
