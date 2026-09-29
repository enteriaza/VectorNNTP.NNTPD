using System.Buffers.Binary;
using System.IO.Hashing;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

public sealed class FileArticleIndexTests
{
    [Fact]
    public void A_OpenEmptyIndex()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        Assert.False(index.TryGet(ArtId("<a@example.test>"), out _));
        Assert.Equal(0, index.DurableWriteCount);
    }

    [Fact]
    public void B_InsertPresent()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<b@example.test>", hash: 11, size: 20, seg: 1, offset: 0, length: 64);
        Assert.True(index.TryCommitPresent(meta));
        Assert.Equal(1, index.DurableWriteCount);
    }

    [Fact]
    public void C_LookupExisting()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<c@example.test>", 1, 10, 2, 8, 40);
        Assert.True(index.TryCommitPresent(meta));
        Assert.True(index.TryGet(meta.ArtId, out var got));
        Assert.Equal(meta, got);
    }

    [Fact]
    public void D_LookupMissing()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        Assert.False(index.TryGet(ArtId("<missing@example.test>"), out _));
    }

    [Fact]
    public void E_IdempotentSameMetadata()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<e@example.test>", 5, 12, 1, 0, 50);
        Assert.True(index.TryCommitPresent(meta));
        Assert.True(index.TryCommitPresent(meta));
        Assert.Equal(1, index.DurableWriteCount);
    }

    [Fact]
    public void F_DifferentArtHash_Rejected()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var first = Present("<f@example.test>", 1, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(first));
        Assert.False(index.TryCommitPresent(first with { ArtHash = 2 }));
        Assert.True(index.TryGet(first.ArtId, out var got));
        Assert.Equal(1UL, got.ArtHash);
    }

    [Fact]
    public void G_DifferentArtSize_Rejected()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var first = Present("<g@example.test>", 1, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(first));
        Assert.False(index.TryCommitPresent(first with { ArtSize = 11 }));
    }

    [Fact]
    public void H_DifferentLocation_RejectedWithoutRelocate()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var first = Present("<h@example.test>", 1, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(first));
        var other = first with { Location = new StoredArticleLocation(new SegmentId(2), 100, 40) };
        Assert.False(index.TryCommitPresent(other));
        Assert.True(index.TryGet(first.ArtId, out var got));
        Assert.Equal(first.Location, got.Location);
    }

    [Fact]
    public void I_DisposeReopen_PreservesPresent()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<i@example.test>", 9, 15, 3, 16, 80);
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(meta));
        }

        using var indexB = FileArticleIndex.Open(dir.Options);
        Assert.True(indexB.TryGet(meta.ArtId, out var got));
        Assert.Equal(meta.ArtHash, got.ArtHash);
        Assert.Equal(meta.ArtSize, got.ArtSize);
        Assert.Equal(meta.Location, got.Location);
        Assert.Equal(ArticleStorageState.Present, got.State);
    }

    [Fact]
    public void J_MultipleEntries_SurviveRestart()
    {
        using var dir = TempControlDir.Create();
        var a = Present("<j1@example.test>", 1, 10, 1, 0, 40);
        var b = Present("<j2@example.test>", 2, 20, 1, 40, 50);
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(a));
            Assert.True(indexA.TryCommitPresent(b));
        }

        using var indexB = FileArticleIndex.Open(dir.Options);
        Assert.True(indexB.TryGet(a.ArtId, out _));
        Assert.True(indexB.TryGet(b.ArtId, out _));
    }

    [Fact]
    public void K_Eviction_PersistsAcrossRestart()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<k@example.test>", 1, 10, 1, 0, 40);
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(meta));
            Assert.True(indexA.TrySetState(meta.ArtId, ArticleStorageState.Evicted, DateTimeOffset.UtcNow));
        }

        using var indexB = FileArticleIndex.Open(dir.Options);
        Assert.True(indexB.TryGet(meta.ArtId, out var got));
        Assert.Equal(ArticleStorageState.Evicted, got.State);
        Assert.Equal(meta.Location, got.Location);
    }

    [Fact]
    public void L_Invalidation_PersistsAcrossRestart()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<l@example.test>", 1, 10, 1, 0, 40);
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(meta));
            Assert.True(indexA.TrySetState(meta.ArtId, ArticleStorageState.Invalid, DateTimeOffset.UtcNow));
        }

        using var indexB = FileArticleIndex.Open(dir.Options);
        Assert.True(indexB.TryGet(meta.ArtId, out var got));
        Assert.Equal(ArticleStorageState.Invalid, got.State);
    }

    [Fact]
    public void M_Relocation_Succeeds()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var oldLoc = new StoredArticleLocation(new SegmentId(1), 0, 40);
        var newLoc = new StoredArticleLocation(new SegmentId(2), 100, 40);
        var meta = Present("<m@example.test>", 7, 10, oldLoc);
        Assert.True(index.TryCommitPresent(meta));
        Assert.Equal(
            ArticleRelocateOutcome.Relocated,
            index.TryRelocate(meta.ArtId, oldLoc, newLoc, 7, 10));
        Assert.True(index.TryGet(meta.ArtId, out var got));
        Assert.Equal(newLoc, got.Location);
    }

    [Fact]
    public void N_Relocation_ExpectedMismatch()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var oldLoc = new StoredArticleLocation(new SegmentId(1), 0, 40);
        var wrong = new StoredArticleLocation(new SegmentId(9), 0, 40);
        var newLoc = new StoredArticleLocation(new SegmentId(2), 100, 40);
        var meta = Present("<n@example.test>", 7, 10, oldLoc);
        Assert.True(index.TryCommitPresent(meta));
        Assert.Equal(
            ArticleRelocateOutcome.ExpectedLocationMismatch,
            index.TryRelocate(meta.ArtId, wrong, newLoc, 7, 10));
        Assert.True(index.TryGet(meta.ArtId, out var got));
        Assert.Equal(oldLoc, got.Location);
    }

    [Fact]
    public void O_Relocation_WrongArticleId()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<o@example.test>", 7, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(meta));
        Assert.Equal(
            ArticleRelocateOutcome.NotPresent,
            index.TryRelocate(
                ArtId("<other@example.test>"),
                meta.Location,
                new StoredArticleLocation(new SegmentId(2), 0, 40),
                7,
                10));
    }

    [Fact]
    public void P_Relocation_WrongArtHash()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<p@example.test>", 7, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(meta));
        Assert.Equal(
            ArticleRelocateOutcome.IdentityMismatch,
            index.TryRelocate(
                meta.ArtId,
                meta.Location,
                new StoredArticleLocation(new SegmentId(2), 0, 40),
                99,
                10));
    }

    [Fact]
    public void Q_Relocation_Idempotent()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var oldLoc = new StoredArticleLocation(new SegmentId(1), 0, 40);
        var newLoc = new StoredArticleLocation(new SegmentId(2), 100, 40);
        var meta = Present("<q@example.test>", 7, 10, oldLoc);
        Assert.True(index.TryCommitPresent(meta));
        Assert.Equal(ArticleRelocateOutcome.Relocated, index.TryRelocate(meta.ArtId, oldLoc, newLoc, 7, 10));
        Assert.Equal(ArticleRelocateOutcome.IdempotentNoOp, index.TryRelocate(meta.ArtId, oldLoc, newLoc, 7, 10));
        Assert.Equal(ArticleRelocateOutcome.IdempotentNoOp, index.TryRelocate(meta.ArtId, newLoc, newLoc, 7, 10));
    }

    [Fact]
    public void R_Relocation_DoesNotChangeIdentityFields()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var oldLoc = new StoredArticleLocation(new SegmentId(1), 0, 40);
        var newLoc = new StoredArticleLocation(new SegmentId(2), 100, 40);
        var meta = Present("<r@example.test>", 7, 10, oldLoc);
        Assert.True(index.TryCommitPresent(meta));
        _ = index.TryRelocate(meta.ArtId, oldLoc, newLoc, 7, 10);
        Assert.True(index.TryGet(meta.ArtId, out var got));
        Assert.Equal(7UL, got.ArtHash);
        Assert.Equal(10, got.ArtSize);
        Assert.Equal(ArticleStorageState.Present, got.State);
    }

    [Fact]
    public void S_CorruptCrc_Detected()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<s@example.test>", 1, 10, 1, 0, 40);
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(meta));
            Assert.True(indexA.TryCommitPresent(Present("<s2@example.test>", 2, 10, 1, 40, 40)));
        }

        var path = Path.Combine(dir.ControlDir, FileArticleIndex.IndexFileName);
        var bytes = File.ReadAllBytes(path);
        bytes[ArticleIndexRecordCodec.RecordLength - 1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        _ = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
    }

    [Fact]
    public void S2_SoleCompleteFrame_CorruptCrc_FailsClosed()
    {
        // Fixed-size records: RecordLength bytes + bad CRC is a complete corrupt frame, not a torn write.
        // EOF alone does not justify truncating a previously durable mutation.
        using var dir = TempControlDir.Create();
        var meta = Present("<s2-sole@example.test>", 1, 10, 1, 0, 40);
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(meta));
        }

        var path = Path.Combine(dir.ControlDir, FileArticleIndex.IndexFileName);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, bytes.Length);
        bytes[^5] ^= 0xFF; // Flip a payload byte covered by CRC (not only the CRC field).
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("incomplete", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, new FileInfo(path).Length);
    }

    [Fact]
    public void S3_CorruptFinalCompleteFrame_AfterValidFrames_FailsClosed()
    {
        // Must not truncate the corrupt final frame and continue with only the prior valid prefix.
        using var dir = TempControlDir.Create();
        var first = Present("<s3-1@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<s3-2@example.test>", 2, 10, 1, 40, 40);
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(first));
            Assert.True(indexA.TryCommitPresent(second));
        }

        var path = Path.Combine(dir.ControlDir, FileArticleIndex.IndexFileName);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength * 2, bytes.Length);
        bytes[^5] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        _ = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));

        // File must remain untruncated (fail closed, not silent tail discard).
        Assert.Equal(ArticleIndexRecordCodec.RecordLength * 2, new FileInfo(path).Length);
    }

    [Fact]
    public void T_TruncatedFinalRecord_Recovered()
    {
        using var dir = TempControlDir.Create();
        var first = Present("<t1@example.test>", 1, 10, 1, 0, 40);
        var second = Present("<t2@example.test>", 2, 10, 1, 40, 40);
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(first));
            Assert.True(indexA.TryCommitPresent(second));
        }

        var path = Path.Combine(dir.ControlDir, FileArticleIndex.IndexFileName);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length - 7).ToArray());

        using var indexB = FileArticleIndex.Open(dir.Options);
        Assert.True(indexB.TryGet(first.ArtId, out _));
        Assert.False(indexB.TryGet(second.ArtId, out _));
    }

    [Fact]
    public void U_MidFileCorruption_FailsClosed()
    {
        using var dir = TempControlDir.Create();
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(Present("<u1@example.test>", 1, 10, 1, 0, 40)));
            Assert.True(indexA.TryCommitPresent(Present("<u2@example.test>", 2, 10, 1, 40, 40)));
        }

        var path = Path.Combine(dir.ControlDir, FileArticleIndex.IndexFileName);
        var bytes = File.ReadAllBytes(path);
        bytes[ArticleIndexRecordCodec.RecordLength - 1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength * 2, new FileInfo(path).Length);
    }

    [Fact]
    public void V_InvalidRecordLength_Rejected()
    {
        using var dir = TempControlDir.Create();
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(Present("<v@example.test>", 1, 10, 1, 0, 40)));
        }

        var path = Path.Combine(dir.ControlDir, FileArticleIndex.IndexFileName);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 1);
        File.WriteAllBytes(path, bytes);

        _ = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
    }

    [Fact]
    public void W_InvalidSchemaVersion_Rejected()
    {
        using var dir = TempControlDir.Create();
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(Present("<w@example.test>", 1, 10, 1, 0, 40)));
        }

        var path = Path.Combine(dir.ControlDir, FileArticleIndex.IndexFileName);
        var bytes = File.ReadAllBytes(path);
        bytes[4] = 99;
        var crc = System.IO.Hashing.Crc32.HashToUInt32(bytes.AsSpan(0, bytes.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4, 4), crc);
        File.WriteAllBytes(path, bytes);

        _ = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
    }

    [Fact]
    public void X_InvalidStateByte_Rejected()
    {
        using var dir = TempControlDir.Create();
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(Present("<x@example.test>", 1, 10, 1, 0, 40)));
        }

        var path = Path.Combine(dir.ControlDir, FileArticleIndex.IndexFileName);
        var bytes = File.ReadAllBytes(path);
        // State byte offset: 8 + 32 + 8 + 4 + 8 + 8 + 4 = 72
        bytes[72] = 0xFF;
        var crc = Crc32.HashToUInt32(bytes.AsSpan(0, bytes.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4, 4), crc);
        File.WriteAllBytes(path, bytes);

        _ = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
    }

    [Fact]
    public void Y_NegativeArtSize_RejectedOnEncode()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var bad = new StoredArticleMetadata(
            ArtId("<y@example.test>"),
            1,
            -1,
            new StoredArticleLocation(new SegmentId(1), 0, 0),
            ArticleStorageState.Present,
            DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentOutOfRangeException>(() => index.TryCommitPresent(bad));
    }

    [Fact]
    public void Z_ImpossibleOffset_RejectedOnEncode()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var bad = Present("<z@example.test>", 1, 10, 1, -5, 40);
        Assert.Throws<ArgumentOutOfRangeException>(() => index.TryCommitPresent(bad));
    }

    [Fact]
    public async Task AA_ConcurrentMutations()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var tasks = Enumerable.Range(0, 32)
            .Select(i => Task.Run(() =>
            {
                var meta = Present($"<aa-{i}@example.test>", (ulong)i, 10 + i, 1, i * 64L, 64);
                Assert.True(index.TryCommitPresent(meta));
            }))
            .ToArray();
        await Task.WhenAll(tasks);
        for (var i = 0; i < 32; i++)
        {
            Assert.True(index.TryGet(ArtId($"<aa-{i}@example.test>"), out _));
        }
    }

    [Fact]
    public async Task AB_ConcurrentLookupAndMutation()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var seed = Present("<ab-seed@example.test>", 1, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(seed));

        var writers = Enumerable.Range(0, 16)
            .Select(i => Task.Run(() =>
            {
                var meta = Present($"<ab-w-{i}@example.test>", (ulong)(100 + i), 10, 1, (i + 1) * 64L, 64);
                Assert.True(index.TryCommitPresent(meta));
            }));
        var readers = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() =>
            {
                Assert.True(index.TryGet(seed.ArtId, out var got));
                Assert.Equal(ArticleStorageState.Present, got.State);
            }));
        await Task.WhenAll(writers.Concat(readers));
    }

    [Fact]
    public void AC_ColdRestart_TwoIndependentInstances()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<ac@example.test>", 42, 18, 5, 200, 90);
        using (var a = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(a.TryCommitPresent(meta));
        }

        using var b = FileArticleIndex.Open(dir.Options);
        Assert.True(b.TryGet(meta.ArtId, out var got));
        Assert.Equal(meta, got);
    }

    [Fact]
    public void AD_FlushDurability_VisibleAfterDispose()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<ad@example.test>", 3, 11, 1, 0, 44);
        var index = FileArticleIndex.Open(dir.Options);
        Assert.True(index.TryCommitPresent(meta));
        Assert.Equal(1, index.DurableWriteCount);
        index.Dispose();

        using var reopened = FileArticleIndex.Open(dir.Options);
        Assert.True(reopened.TryGet(meta.ArtId, out var got));
        Assert.Equal(meta.Location, got.Location);
    }

    [Fact]
    public void AE_Relocation_SurvivesRestart()
    {
        using var dir = TempControlDir.Create();
        var oldLoc = new StoredArticleLocation(new SegmentId(1), 0, 40);
        var newLoc = new StoredArticleLocation(new SegmentId(2), 100, 40);
        var meta = Present("<ae@example.test>", 7, 10, oldLoc);
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(meta));
            Assert.Equal(ArticleRelocateOutcome.Relocated, indexA.TryRelocate(meta.ArtId, oldLoc, newLoc, 7, 10));
        }

        using var indexB = FileArticleIndex.Open(dir.Options);
        Assert.True(indexB.TryGet(meta.ArtId, out var got));
        Assert.Equal(newLoc, got.Location);
    }

    [Fact]
    public void AF_EvictionInvalidation_SurviveRestart()
    {
        using var dir = TempControlDir.Create();
        var a = Present("<af-e@example.test>", 1, 10, 1, 0, 40);
        var b = Present("<af-i@example.test>", 2, 10, 1, 40, 40);
        using (var indexA = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(indexA.TryCommitPresent(a));
            Assert.True(indexA.TryCommitPresent(b));
            Assert.True(indexA.TrySetState(a.ArtId, ArticleStorageState.Evicted, DateTimeOffset.UtcNow));
            Assert.True(indexA.TrySetState(b.ArtId, ArticleStorageState.Invalid, DateTimeOffset.UtcNow));
        }

        using var indexB = FileArticleIndex.Open(dir.Options);
        Assert.True(indexB.TryGet(a.ArtId, out var ea));
        Assert.True(indexB.TryGet(b.ArtId, out var ib));
        Assert.Equal(ArticleStorageState.Evicted, ea.State);
        Assert.Equal(ArticleStorageState.Invalid, ib.State);
    }

    [Fact]
    public void AG_RepeatedOpenClose_Cycles()
    {
        using var dir = TempControlDir.Create();
        var meta = Present("<ag@example.test>", 1, 10, 1, 0, 40);
        for (var i = 0; i < 5; i++)
        {
            using var index = FileArticleIndex.Open(dir.Options);
            if (i == 0)
            {
                Assert.True(index.TryCommitPresent(meta));
            }
            else
            {
                Assert.True(index.TryGet(meta.ArtId, out var got));
                Assert.Equal(meta.ArtId, got.ArtId);
            }
        }
    }

    [Fact]
    public void TouchHint_DoesNotDurableWrite()
    {
        using var dir = TempControlDir.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        var meta = Present("<touch@example.test>", 1, 10, 1, 0, 40);
        Assert.True(index.TryCommitPresent(meta));
        var durable = index.DurableWriteCount;
        index.TouchHint(meta.ArtId, DateTimeOffset.UtcNow);
        Assert.Equal(durable, index.DurableWriteCount);
        Assert.Equal(1, index.TouchHintCount);
    }

    private static ArticleId ArtId(string messageId) =>
        ArticleId.FromMessageId(System.Text.Encoding.ASCII.GetBytes(messageId));

    private static StoredArticleMetadata Present(
        string messageId,
        ulong hash,
        int size,
        ulong seg,
        long offset,
        int length) =>
        Present(messageId, hash, size, new StoredArticleLocation(new SegmentId(seg), offset, length));

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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-index-" + Guid.NewGuid().ToString("N"));
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
