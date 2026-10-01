using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

public sealed class FileArticleIndexCheckpointTests
{
    [Fact]
    public void EmptyIndex_CheckpointInstallsHeaderOnlyReplacement()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var result = index.Checkpoint();

        Assert.Equal(1UL, result.Snapshot.Generation);
        Assert.Equal(0L, result.Snapshot.CoveredIndexLength);
        Assert.Equal(0UL, result.Snapshot.RecordCount);
        Assert.Equal(0L, result.DeltaBytes);
        Assert.Equal(0, index.DurableWriteCount);
        AssertHeaderOnly(index.CopyIndexBytes(), generation: 1);
        Assert.False(File.Exists(ReplPath(dir)));
        Assert.False(File.Exists(SnapTempPath(dir)));

        index.Dispose();
        using var reopened = FileArticleIndex.Open(dir.Options);
        Assert.Empty(reopened.Snapshot());
    }

    [Fact]
    public void OneArticle_CheckpointRetiresItsFrame()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<ckpt-one@example.test>", 4, 10, 2, 8, 40);
        using var index = FileArticleIndex.Open(dir.Options);
        Assert.True(index.TryCommitPresent(meta));
        var before = index.Snapshot().ToDictionary(static row => row.ArtId);
        var prefix = index.CopyIndexBytes();

        var result = index.Checkpoint();

        Assert.Equal(prefix.Length, result.Snapshot.CoveredIndexLength);
        Assert.Equal(0L, result.DeltaBytes);
        Assert.Equal(before, index.Snapshot().ToDictionary(static row => row.ArtId));
        var replaced = index.CopyIndexBytes();
        AssertHeaderOnly(replaced, generation: 1);
        Assert.NotEqual(prefix, replaced);
        Assert.Equal(1, index.DurableWriteCount);

        index.Dispose();
        var recovered = Projection(dir);
        Assert.Equal(meta, recovered[meta.ArtId]);
    }

    [Fact]
    public void ManyArticles_CheckpointDropsEveryCoveredFrame()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var rows = new StoredArticleMetadata[40];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = Present($"<ckpt-many-{i}@example.test>", (ulong)i + 1, 10 + i, 1, i * 40, 40);
            Assert.True(index.TryCommitPresent(rows[i]));
        }

        var before = index.Snapshot().ToDictionary(static row => row.ArtId);
        var result = index.Checkpoint();
        Assert.Equal(0L, result.DeltaBytes);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength * rows.Length, result.Snapshot.CoveredIndexLength);
        Assert.Equal(before, index.Snapshot().ToDictionary(static row => row.ArtId));
        Assert.Equal(ArticleIndexDeltaFile.HeaderLength, index.CopyIndexBytes().Length);

        index.Dispose();
        Assert.Equal(before, Projection(dir));
    }

    [Fact]
    public void EmptyDelta_MatchesFullReplayOfThePreCheckpointIndex()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<ckpt-empty-delta@example.test>", 8, 12, 3, 16, 48);
        Assert.True(index.TryCommitPresent(meta));
        var prefix = index.CopyIndexBytes();
        var memory = index.Snapshot().ToDictionary(static row => row.ArtId);

        var result = index.Checkpoint();
        Assert.Equal(0L, result.DeltaBytes);
        Assert.Equal(memory, index.Snapshot().ToDictionary(static row => row.ArtId));
        index.Dispose();

        using var full = CloneIndex(prefix);
        Assert.Equal(Projection(full), Projection(dir));
    }

    [Fact]
    public void NonEmptyDelta_IsExactlyTheBytesAfterTheCoveredLength()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var first = Present("<ckpt-delta-a@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<ckpt-delta-b@example.test>", 2, 11, 1, 40, 44);
        var third = Present("<ckpt-delta-c@example.test>", 3, 12, 1, 80, 46);
        Assert.True(index.TryCommitPresent(first));
        var prefix = index.CopyIndexBytes();
        index.TestDuringSnapshotWrite = () =>
        {
            Assert.True(index.TryCommitPresent(second));
            Assert.True(index.TryCommitPresent(third));
        };

        var result = index.Checkpoint();

        Assert.Equal(prefix.Length, result.Snapshot.CoveredIndexLength);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength * 2, result.DeltaBytes);
        Assert.Equal(1UL, result.Snapshot.RecordCount);
        var file = index.CopyIndexBytes();
        Assert.Equal(ArticleIndexDeltaFile.HeaderLength + result.DeltaBytes, file.Length);
        var payload = file.AsSpan(ArticleIndexDeltaFile.HeaderLength).ToArray();
        var expected = ArticleIndexRecordCodec.Encode(second)
            .Concat(ArticleIndexRecordCodec.Encode(third))
            .ToArray();
        Assert.Equal(expected, payload);
        Assert.Equal(-1, payload.AsSpan().IndexOf(prefix));
        Assert.Equal(1UL, ReadGeneration(file));

        index.Dispose();
        var recovered = Projection(dir);
        Assert.Equal(first, recovered[first.ArtId]);
        Assert.Equal(second, recovered[second.ArtId]);
        Assert.Equal(third, recovered[third.ArtId]);
    }

    [Fact]
    public void StateTransitions_CheckpointMatchesInMemoryAndFullReplay()
    {
        using var dir = TempControlDir.Create();
        var source = new StoredArticleLocation(new SegmentId(1), 0, 40);
        var moved = new StoredArticleLocation(new SegmentId(2), 100, 40);
        var movedAgain = new StoredArticleLocation(new SegmentId(3), 200, 40);
        var kept = Present("<ckpt-kept@example.test>", 1, 10, 1, 0, 40);
        var toEvict = Present("<ckpt-evict@example.test>", 2, 10, 1, 40, 40);
        var toInvalid = Present("<ckpt-invalid@example.test>", 3, 10, 1, 80, 40);
        var toMove = Present("<ckpt-move@example.test>", 4, 10, source);
        var evictedThenAccepted = Present("<ckpt-reaccept-e@example.test>", 5, 10, 1, 120, 40);
        var invalidThenAccepted = Present("<ckpt-reaccept-i@example.test>", 6, 10, 1, 160, 40);
        var repeated = Present("<ckpt-repeat@example.test>", 7, 10, 1, 200, 40);
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

        using var index = FileArticleIndex.Open(dir.Options);
        Assert.True(index.TryCommitPresent(kept));
        Assert.True(index.TryCommitPresent(toEvict));
        Assert.True(index.TryCommitPresent(toInvalid));
        Assert.True(index.TryCommitPresent(toMove));
        Assert.True(index.TryCommitPresent(evictedThenAccepted));
        Assert.True(index.TrySetState(evictedThenAccepted.ArtId, ArticleStorageState.Evicted, evictAt));
        Assert.True(index.TryCommitPresent(invalidThenAccepted));
        Assert.True(index.TrySetState(invalidThenAccepted.ArtId, ArticleStorageState.Invalid, invalidAt));
        Assert.True(index.TryCommitPresent(repeated));
        Assert.Equal(ArticleRelocateOutcome.Relocated, index.TryRelocate(repeated.ArtId, repeated.Location, moved, 7, 10));
        Assert.Equal(ArticleRelocateOutcome.Relocated, index.TryRelocate(repeated.ArtId, moved, movedAgain, 7, 10));
        Assert.True(index.TrySetState(toEvict.ArtId, ArticleStorageState.Evicted, evictAt));
        Assert.True(index.TrySetState(toInvalid.ArtId, ArticleStorageState.Invalid, invalidAt));
        Assert.Equal(ArticleRelocateOutcome.Relocated, index.TryRelocate(toMove.ArtId, source, moved, 4, 10));
        Assert.Equal(ArticleRelocateOutcome.Relocated, index.TryRelocate(toMove.ArtId, moved, movedAgain, 4, 10));
        Assert.True(index.TryCommitPresent(reacceptEvicted));
        Assert.True(index.TryCommitPresent(reacceptInvalid));

        var prefix = index.CopyIndexBytes();
        var memory = index.Snapshot().ToDictionary(static row => row.ArtId);
        var result = index.Checkpoint();
        Assert.Equal(0L, result.DeltaBytes);
        Assert.Equal(memory, index.Snapshot().ToDictionary(static row => row.ArtId));
        Assert.True(result.Snapshot.CoveredIndexLength > index.CopyIndexBytes().Length);

        index.Dispose();
        using var full = CloneIndex(prefix);
        var recovered = Projection(dir);
        Assert.Equal(Projection(full), recovered);
        Assert.Equal(ArticleStorageState.Present, recovered[kept.ArtId].State);
        Assert.Equal(ArticleStorageState.Evicted, recovered[toEvict.ArtId].State);
        Assert.Equal(evictAt, recovered[toEvict.ArtId].LastAccessUtc);
        Assert.Equal(ArticleStorageState.Invalid, recovered[toInvalid.ArtId].State);
        Assert.Equal(movedAgain, recovered[toMove.ArtId].Location);
        Assert.Equal(50UL, recovered[reacceptEvicted.ArtId].ArtHash);
        Assert.Equal(ArticleStorageState.Present, recovered[reacceptInvalid.ArtId].State);
        Assert.Equal(movedAgain, recovered[repeated.ArtId].Location);
        Assert.Equal(ArticleStorageState.Present, recovered[repeated.ArtId].State);
    }

    [Fact]
    public void TouchHintCapturedByTheSnapshot_SurvivesRestart()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<ckpt-touch@example.test>", 1, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(meta));
        var hinted = new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.Zero);
        index.TouchHint(meta.ArtId, hinted);
        var writes = index.DurableWriteCount;

        _ = index.Checkpoint();
        Assert.Equal(writes, index.DurableWriteCount);
        Assert.True(index.TryGet(meta.ArtId, out var live));
        Assert.Equal(hinted, live.LastAccessUtc);

        index.Dispose();
        var recovered = Projection(dir);
        Assert.Equal(hinted, recovered[meta.ArtId].LastAccessUtc);
        Assert.Equal(meta.ArtHash, recovered[meta.ArtId].ArtHash);
    }

    [Fact]
    public void ReplacementLongerThanCoveredLength_ReplaysPayloadNotTheOldOffset()
    {
        using var dir = TempControlDir.Create();
        var first = Present("<ckpt-long-a@example.test>", 1, 10, 1, 0, 40);
        var extra = new StoredArticleMetadata[4];
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(first));
            _ = index.Checkpoint();
            for (var i = 0; i < extra.Length; i++)
            {
                extra[i] = Present($"<ckpt-long-b{i}@example.test>", (ulong)i + 2, 10, 1, 40L * (i + 1), 40);
                Assert.True(index.TryCommitPresent(extra[i]));
            }
        }

        var snap = ArticleIndexSnapshotCodec.Read(SnapPath(dir));
        var indexLength = new FileInfo(IndexPath(dir)).Length;
        Assert.True(snap.CoveredIndexLength > ArticleIndexDeltaFile.HeaderLength);
        Assert.True(indexLength > snap.CoveredIndexLength);
        Assert.Single(snap.Entries);

        var recovered = Projection(dir);
        Assert.Equal(first, recovered[first.ArtId]);
        foreach (var row in extra)
        {
            Assert.Equal(row, recovered[row.ArtId]);
        }
    }

    [Fact]
    public void SecondCheckpoint_RetiresThePreviousReplacementBody()
    {
        using var dir = TempControlDir.Create();
        var first = Present("<ckpt-second-a@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<ckpt-second-b@example.test>", 2, 10, 1, 40, 40);
        using var index = FileArticleIndex.Open(dir.Options);
        Assert.True(index.TryCommitPresent(first));
        Assert.Equal(1UL, index.Checkpoint().Snapshot.Generation);
        Assert.True(index.TryCommitPresent(second));

        var result = index.Checkpoint();
        Assert.Equal(2UL, result.Snapshot.Generation);
        Assert.Equal(0L, result.DeltaBytes);
        AssertHeaderOnly(index.CopyIndexBytes(), generation: 2);
        Assert.Equal(2UL, result.Snapshot.RecordCount);

        index.Dispose();
        var recovered = Projection(dir);
        Assert.Equal(first, recovered[first.ArtId]);
        Assert.Equal(second, recovered[second.ArtId]);
    }

    [Fact]
    public void AppendDuringSnapshot_IsPreservedInTheReplacement()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var first = Present("<ckpt-during-snap-a@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<ckpt-during-snap-b@example.test>", 2, 12, 1, 80, 48);
        Assert.True(index.TryCommitPresent(first));
        index.TestDuringSnapshotWrite = () => Assert.True(index.TryCommitPresent(second));

        var result = index.Checkpoint();
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, result.DeltaBytes);
        Assert.DoesNotContain(second.ArtId, ArticleIndexSnapshotCodec.Read(SnapPath(dir)).Entries.Select(static row => row.ArtId));

        index.Dispose();
        var recovered = Projection(dir);
        Assert.Equal(first, recovered[first.ArtId]);
        Assert.Equal(second, recovered[second.ArtId]);
    }

    [Fact]
    public void AppendDuringReplacement_IsPreservedInTheCatchUp()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var first = Present("<ckpt-during-repl-a@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<ckpt-during-repl-b@example.test>", 2, 12, 1, 80, 48);
        Assert.True(index.TryCommitPresent(first));
        index.TestDuringReplacementWrite = () =>
        {
            Assert.True(index.TryGet(first.ArtId, out var got));
            Assert.Equal(first, got);
            Assert.True(index.TryCommitPresent(second));
        };

        var result = index.Checkpoint();
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, result.DeltaBytes);
        var payload = index.CopyIndexBytes().AsSpan(ArticleIndexDeltaFile.HeaderLength).ToArray();
        Assert.Equal(ArticleIndexRecordCodec.Encode(second), payload);

        index.Dispose();
        var recovered = Projection(dir);
        Assert.Equal(first, recovered[first.ArtId]);
        Assert.Equal(second, recovered[second.ArtId]);
    }

    [Fact]
    public async Task AppendDuringFinalHandoff_LandsOnTheReplacement()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var first = Present("<ckpt-handoff-a@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<ckpt-handoff-b@example.test>", 2, 10, 1, 40, 40);
        Assert.True(index.TryCommitPresent(first));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        index.TestBeforeReplacementInstall = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };

        var checkpoint = Task.Run(() => index.Checkpoint());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var append = Task.Run(() => index.TryCommitPresent(second));
        release.Set();

        var result = await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await append.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0L, result.DeltaBytes);
        Assert.True(index.TryGet(second.ArtId, out var live));
        Assert.Equal(second, live);

        index.Dispose();
        var recovered = Projection(dir);
        Assert.Equal(first, recovered[first.ArtId]);
        Assert.Equal(second, recovered[second.ArtId]);
        Assert.Equal(ArticleIndexDeltaFile.HeaderLength + ArticleIndexRecordCodec.RecordLength, new FileInfo(IndexPath(dir)).Length);
    }

    [Fact]
    public void MutationsDuringReplacement_ArePreservedAfterRestart()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var stay = Present("<ckpt-mut-stay@example.test>", 1, 10, 1, 0, 40);
        var source = new StoredArticleLocation(new SegmentId(1), 40, 40);
        var destination = new StoredArticleLocation(new SegmentId(4), 9, 40);
        var toMove = Present("<ckpt-mut-move@example.test>", 2, 10, source);
        var toEvict = Present("<ckpt-mut-evict@example.test>", 3, 10, 1, 80, 40);
        var toInvalid = Present("<ckpt-mut-invalid@example.test>", 4, 10, 1, 120, 40);
        var toReaccept = Present("<ckpt-mut-re@example.test>", 5, 10, 1, 160, 40);
        var evictAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var invalidAt = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);
        var reaccepted = toReaccept with
        {
            ArtHash = 90,
            Location = new StoredArticleLocation(new SegmentId(6), 3, 40),
        };

        Assert.True(index.TryCommitPresent(stay));
        Assert.True(index.TryCommitPresent(toMove));
        Assert.True(index.TryCommitPresent(toEvict));
        Assert.True(index.TryCommitPresent(toInvalid));
        Assert.True(index.TryCommitPresent(toReaccept));
        Assert.True(index.TrySetState(toReaccept.ArtId, ArticleStorageState.Evicted, evictAt));
        index.TestDuringReplacementWrite = () =>
        {
            Assert.True(index.TryGet(stay.ArtId, out _));
            Assert.Equal(ArticleRelocateOutcome.Relocated, index.TryRelocate(toMove.ArtId, source, destination, 2, 10));
            Assert.True(index.TrySetState(toEvict.ArtId, ArticleStorageState.Evicted, evictAt));
            Assert.True(index.TrySetState(toInvalid.ArtId, ArticleStorageState.Invalid, invalidAt));
            Assert.True(index.TryCommitPresent(reaccepted));
        };

        _ = index.Checkpoint();
        var memory = index.Snapshot().ToDictionary(static row => row.ArtId);
        index.Dispose();
        var recovered = Projection(dir);
        Assert.Equal(memory, recovered);
        Assert.Equal(destination, recovered[toMove.ArtId].Location);
        Assert.Equal(ArticleStorageState.Evicted, recovered[toEvict.ArtId].State);
        Assert.Equal(ArticleStorageState.Invalid, recovered[toInvalid.ArtId].State);
        Assert.Equal(ArticleStorageState.Present, recovered[reaccepted.ArtId].State);
        Assert.Equal(90UL, recovered[reaccepted.ArtId].ArtHash);
    }

    [Fact]
    public async Task ConcurrentCheckpoints_AreSingleFlight()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        Assert.True(index.TryCommitPresent(Present("<ckpt-flight@example.test>", 1, 10, 1, 0, 40)));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        index.TestDuringReplacementWrite = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        index.TestOnSnapshotFlightContended = () => contended.TrySetResult();

        var first = Task.Run(() => index.Checkpoint());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Task.Run(() => index.Checkpoint());
        await contended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, index.MaxSnapshotWriters);

        release.Set();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, index.MaxSnapshotWriters);
        Assert.Equal(1UL, results[0].Snapshot.Generation);
        Assert.Equal(2UL, results[1].Snapshot.Generation);
        Assert.False(File.Exists(ReplPath(dir)));
        AssertHeaderOnly(index.CopyIndexBytes(), generation: 2);
    }

    [Fact]
    public void SnapshotWriteFailure_LeavesTheIndexUntouched()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<ckpt-snap-write@example.test>", 1, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(meta));
        var before = index.CopyIndexBytes();
        index.TestDuringSnapshotWrite = () => throw new IOException("snapshot write interrupted");

        var ex = Assert.Throws<IOException>(() => index.Checkpoint());
        Assert.Contains("snapshot write interrupted", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, index.CopyIndexBytes());
        Assert.False(File.Exists(SnapPath(dir)));
        Assert.False(File.Exists(SnapTempPath(dir)));
        Assert.False(File.Exists(ReplPath(dir)));
        Assert.False(StartsWithReplacementMagic(before));

        index.Dispose();
        Assert.Equal(meta, Projection(dir)[meta.ArtId]);
    }

    [Fact]
    public void SnapshotFlushFailure_LeavesTheIndexUntouched()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<ckpt-snap-flush@example.test>", 1, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(meta));
        var before = index.CopyIndexBytes();
        index.TestBeforeSnapshotFlush = () => throw new IOException("snapshot flush interrupted");

        var ex = Assert.Throws<IOException>(() => index.Checkpoint());
        Assert.Contains("snapshot flush interrupted", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, index.CopyIndexBytes());
        Assert.False(File.Exists(SnapPath(dir)));
        Assert.False(File.Exists(ReplPath(dir)));

        index.Dispose();
        Assert.Equal(meta, Projection(dir)[meta.ArtId]);
    }

    [Fact]
    public void SnapshotInstallFailure_LeavesThePreviousIndexAndSnapshot()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<ckpt-snap-install@example.test>", 1, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(meta));
        _ = index.WriteSnapshot();
        var indexBefore = index.CopyIndexBytes();
        var snapBefore = File.ReadAllBytes(SnapPath(dir));
        index.TestBeforeSnapshotInstall = () => throw new IOException("snapshot install interrupted");

        var ex = Assert.Throws<IOException>(() => index.Checkpoint());
        Assert.Contains("snapshot install interrupted", ex.Message, StringComparison.Ordinal);
        Assert.Equal(indexBefore, index.CopyIndexBytes());
        Assert.Equal(snapBefore, File.ReadAllBytes(SnapPath(dir)));
        Assert.False(File.Exists(ReplPath(dir)));
        Assert.False(StartsWithReplacementMagic(indexBefore));

        index.Dispose();
        Assert.Equal(meta, Projection(dir)[meta.ArtId]);
    }

    [Fact]
    public void ReplacementWriteFailure_KeepsTheOldIndexAndInstalledSnapshot()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<ckpt-repl-write@example.test>", 1, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(meta));
        var before = index.CopyIndexBytes();
        index.TestDuringReplacementWrite = () => throw new IOException("replacement write interrupted");

        var ex = Assert.Throws<IOException>(() => index.Checkpoint());
        Assert.Contains("replacement write interrupted", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, index.CopyIndexBytes());
        Assert.False(File.Exists(ReplPath(dir)));
        Assert.True(File.Exists(SnapPath(dir)));
        Assert.False(StartsWithReplacementMagic(before));

        index.Dispose();
        Assert.Equal(meta, Projection(dir)[meta.ArtId]);
        Assert.Equal(before.Length, new FileInfo(IndexPath(dir)).Length);
    }

    [Fact]
    public void ReplacementFlushFailure_KeepsTheOldIndex()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<ckpt-repl-flush@example.test>", 1, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(meta));
        var before = index.CopyIndexBytes();
        index.TestBeforeReplacementFlush = () => throw new IOException("replacement flush interrupted");

        var ex = Assert.Throws<IOException>(() => index.Checkpoint());
        Assert.Contains("replacement flush interrupted", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, index.CopyIndexBytes());
        Assert.False(File.Exists(ReplPath(dir)));

        index.Dispose();
        Assert.Equal(meta, Projection(dir)[meta.ArtId]);
    }

    [Fact]
    public void ReplacementInstallFailure_LeavesARecoverableFlushedReplacementUnused()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var first = Present("<ckpt-repl-install-a@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<ckpt-repl-install-b@example.test>", 2, 10, 1, 40, 40);
        Assert.True(index.TryCommitPresent(first));
        index.TestDuringSnapshotWrite = () => Assert.True(index.TryCommitPresent(second));
        byte[]? flushed = null;
        index.TestBeforeReplacementInstall = () =>
        {
            flushed = ReadShared(ReplPath(dir));
            throw new IOException("replacement install interrupted");
        };

        var ex = Assert.Throws<IOException>(() => index.Checkpoint());
        Assert.Contains("replacement install interrupted", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(flushed);
        Assert.False(File.Exists(ReplPath(dir)));
        var live = index.CopyIndexBytes();
        Assert.False(StartsWithReplacementMagic(live));
        Assert.Equal(ArticleIndexRecordCodec.RecordLength * 2, live.Length);
        var payload = flushed.AsSpan(ArticleIndexDeltaFile.HeaderLength).ToArray();
        Assert.Equal(ArticleIndexRecordCodec.Encode(second), payload);
        Assert.Equal(1UL, ReadGeneration(flushed));

        var memory = index.Snapshot().ToDictionary(static row => row.ArtId);
        index.Dispose();
        Assert.Equal(memory, Projection(dir));

        using var installed = TempControlDir.Create();
        File.Copy(SnapPath(dir), SnapPath(installed));
        File.WriteAllBytes(IndexPath(installed), flushed);
        Assert.Equal(memory, Projection(installed));
        Assert.Equal(ArticleIndexDeltaFile.HeaderLength + ArticleIndexRecordCodec.RecordLength, new FileInfo(IndexPath(installed)).Length);
    }

    [Fact]
    public void TemporaryReplacement_IsIgnoredOnOpen()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<ckpt-tmp@example.test>", 4, 10, 1, 0, 40);
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(meta));
            _ = index.Checkpoint();
        }

        var indexBytes = File.ReadAllBytes(IndexPath(dir));
        File.WriteAllBytes(ReplPath(dir), [0x01, 0x02, 0x03, 0x04, 0x05]);
        using (var reopened = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(reopened.TryGet(meta.ArtId, out var got));
            Assert.Equal(meta, got);
        }

        Assert.Equal(indexBytes, File.ReadAllBytes(IndexPath(dir)));
        Assert.Equal(5, new FileInfo(ReplPath(dir)).Length);
    }

    [Fact]
    public void NewerSnapshotThanReplacement_ReplaysFromTheCoveredOffset()
    {
        using var dir = TempControlDir.Create();
        var first = Present("<ckpt-newer-snap-a@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<ckpt-newer-snap-b@example.test>", 2, 10, 1, 40, 40);
        var hinted = new DateTimeOffset(2026, 8, 1, 1, 2, 3, TimeSpan.Zero);
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(first));
            _ = index.Checkpoint();
            Assert.True(index.TryCommitPresent(second));
            index.TouchHint(second.ArtId, hinted);
            var written = index.WriteSnapshot();
            Assert.Equal(2UL, written.Generation);
            Assert.Equal(new FileInfo(index.IndexPath).Length, written.CoveredIndexLength);
        }

        var file = File.ReadAllBytes(IndexPath(dir));
        Assert.Equal(1UL, ReadGeneration(file));
        Assert.Equal(2UL, ArticleIndexSnapshotCodec.Read(SnapPath(dir)).Generation);
        var recovered = Projection(dir);
        Assert.Equal(first, recovered[first.ArtId]);
        Assert.Equal(hinted, recovered[second.ArtId].LastAccessUtc);
        Assert.Equal(second.ArtHash, recovered[second.ArtId].ArtHash);
        Assert.Equal(file, File.ReadAllBytes(IndexPath(dir)));
    }

    [Fact]
    public void ReplacementNewerThanSnapshot_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<ckpt-newer-file@example.test>", 1, 10, 1, 0, 40);
        byte[] olderSnap;
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(meta));
            _ = index.Checkpoint();
            olderSnap = File.ReadAllBytes(SnapPath(dir));
            Assert.True(index.TryCommitPresent(Present("<ckpt-newer-file-b@example.test>", 2, 10, 1, 40, 40)));
            _ = index.Checkpoint();
        }

        var indexBytes = File.ReadAllBytes(IndexPath(dir));
        File.WriteAllBytes(SnapPath(dir), olderSnap);
        var ex = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("newer than snapshot", ex.Message, StringComparison.Ordinal);
        Assert.Equal(indexBytes, File.ReadAllBytes(IndexPath(dir)));
    }

    [Fact]
    public void InvalidSnapshot_FailsClosedAfterCheckpoint()
    {
        using var dir = TempControlDir.Create();
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(Present("<ckpt-bad-snap@example.test>", 1, 10, 1, 0, 40)));
            _ = index.Checkpoint();
        }

        var indexBytes = File.ReadAllBytes(IndexPath(dir));
        var snap = File.ReadAllBytes(SnapPath(dir));
        snap[ArticleIndexSnapshotCodec.HeaderLength - 1] ^= 0xFF;
        File.WriteAllBytes(SnapPath(dir), snap);

        var ex = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("header CRC", ex.Message, StringComparison.Ordinal);
        Assert.Equal(indexBytes, File.ReadAllBytes(IndexPath(dir)));
    }

    [Fact]
    public void InvalidReplacementHeaderOrPayload_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(Present("<ckpt-bad-repl-a@example.test>", 1, 10, 1, 0, 40)));
            index.TestDuringSnapshotWrite = () =>
                Assert.True(index.TryCommitPresent(Present("<ckpt-bad-repl-b@example.test>", 2, 10, 1, 40, 40)));
            _ = index.Checkpoint();
        }

        var original = File.ReadAllBytes(IndexPath(dir));
        var header = original.ToArray();
        header[ArticleIndexDeltaFile.HeaderLength - 1] ^= 0xFF;
        File.WriteAllBytes(IndexPath(dir), header);
        var headerEx = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("header CRC", headerEx.Message, StringComparison.Ordinal);
        Assert.Equal(header, File.ReadAllBytes(IndexPath(dir)));

        var payload = original.ToArray();
        payload[ArticleIndexDeltaFile.HeaderLength + 10] ^= 0xFF;
        File.WriteAllBytes(IndexPath(dir), payload);
        var payloadEx = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("checksum", payloadEx.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(payload.Length, new FileInfo(IndexPath(dir)).Length);
    }

    [Fact]
    public void ReplacementWithoutSnapshot_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        using (var stream = File.Create(IndexPath(dir)))
        {
            ArticleIndexDeltaFile.WriteHeader(stream, generation: 1);
        }

        var ex = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("no installed snapshot", ex.Message, StringComparison.Ordinal);

        using var truncated = TempControlDir.Create();
        File.WriteAllBytes(IndexPath(truncated), "VNID"u8.ToArray());
        var torn = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(truncated.Options));
        Assert.Contains("truncated", torn.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TornReplacementTail_TruncatesOnlyTheTail()
    {
        using var dir = TempControlDir.Create();
        var first = Present("<ckpt-torn-a@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<ckpt-torn-b@example.test>", 2, 10, 1, 40, 40);
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(first));
            index.TestDuringSnapshotWrite = () => Assert.True(index.TryCommitPresent(second));
            _ = index.Checkpoint();
        }

        var bytes = File.ReadAllBytes(IndexPath(dir));
        var snap = File.ReadAllBytes(SnapPath(dir));
        File.WriteAllBytes(IndexPath(dir), bytes.AsSpan(0, bytes.Length - 7).ToArray());

        var recovered = Projection(dir);
        Assert.Equal(snap, File.ReadAllBytes(SnapPath(dir)));
        Assert.Equal(ArticleIndexDeltaFile.HeaderLength, new FileInfo(IndexPath(dir)).Length);
        Assert.Equal(first, recovered[first.ArtId]);
        Assert.False(recovered.ContainsKey(second.ArtId));
    }

    [Fact]
    public void AppendAfterCheckpoint_IsReplayableFromTheReplacement()
    {
        using var dir = TempControlDir.Create();
        var first = Present("<ckpt-after-a@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<ckpt-after-b@example.test>", 2, 10, 1, 40, 40);
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(first));
            _ = index.Checkpoint();
            Assert.True(index.TryCommitPresent(second));
        }

        var recovered = Projection(dir);
        Assert.Equal(first, recovered[first.ArtId]);
        Assert.Equal(second, recovered[second.ArtId]);
        Assert.Equal(ArticleIndexDeltaFile.HeaderLength + ArticleIndexRecordCodec.RecordLength, new FileInfo(IndexPath(dir)).Length);
    }

    private static void AssertHeaderOnly(byte[] file, ulong generation)
    {
        Assert.Equal(ArticleIndexDeltaFile.HeaderLength, file.Length);
        Assert.True(StartsWithReplacementMagic(file));
        Assert.Equal(generation, ReadGeneration(file));
    }

    private static bool StartsWithReplacementMagic(byte[] file) =>
        file.Length >= ArticleIndexDeltaFile.Magic.Length
        && file.AsSpan(0, ArticleIndexDeltaFile.Magic.Length).SequenceEqual(ArticleIndexDeltaFile.Magic);

    private static ulong ReadGeneration(byte[] file)
    {
        using var stream = new MemoryStream(file, writable: false);
        Assert.True(ArticleIndexDeltaFile.TryReadHeader(stream, file.Length, out var generation));
        return generation;
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[(int)stream.Length];
        var filled = 0;
        while (filled < bytes.Length)
        {
            var read = stream.Read(bytes, filled, bytes.Length - filled);
            if (read == 0)
            {
                throw new EndOfStreamException(path);
            }

            filled += read;
        }

        return bytes;
    }

    private static Dictionary<ArticleId, StoredArticleMetadata> Projection(TempControlDir dir)
    {
        using var index = FileArticleIndex.Open(dir.Options);
        return index.Snapshot().ToDictionary(static row => row.ArtId);
    }

    private static TempControlDir CloneIndex(byte[] indexBytes)
    {
        var dir = TempControlDir.Create();
        File.WriteAllBytes(IndexPath(dir), indexBytes);
        return dir;
    }

    private static string IndexPath(TempControlDir dir) =>
        Path.Combine(dir.ControlDir, FileArticleIndex.IndexFileName);

    private static string SnapPath(TempControlDir dir) =>
        Path.Combine(dir.ControlDir, FileArticleIndex.SnapshotFileName);

    private static string SnapTempPath(TempControlDir dir) =>
        Path.Combine(dir.ControlDir, FileArticleIndex.SnapshotTempFileName);

    private static string ReplPath(TempControlDir dir) =>
        Path.Combine(dir.ControlDir, FileArticleIndex.ReplacementTempFileName);

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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-index-ckpt-" + Guid.NewGuid().ToString("N"));
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
