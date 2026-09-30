using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Historical LiveBytes/DeadBytes accounting is per Closed segment, replaces dead bytes
/// from one complete proof, and never counts an unproved range as dead.
/// </summary>
public sealed class ClosedSegmentExtentAccountingTests
{
    [Fact]
    public async Task FullyValidClosed_ScanCompletes_AndBalances()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-valid@seg.test>");
        var location = await SeedClosedAsync(dir, record);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        Assert.False(engine.IsUnreferencedExtentAccountingComplete);
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var before));
        Assert.False(before.ExtentAccountingComplete);
        Assert.Equal(0, before.DeadBytes);

        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var after));
        Assert.True(after.ExtentAccountingComplete);
        Assert.Equal(after.SizeBytes, after.LiveBytes + after.DeadBytes);
        Assert.Equal(location.Length, after.LiveBytes);
        Assert.Equal(0, after.DeadBytes);
        Assert.True(engine.IsUnreferencedExtentAccountingComplete);
    }

    [Fact]
    public async Task CorruptMiddle_LeavesBitFalse_AndDoesNotCountUnprovedSuffix()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-mid@seg.test>");
        var location = await SeedClosedAsync(dir, record);
        var middle = SegmentRecordCodec.Encode(
            CreateRecord("<acct-mid-bad@seg.test>").ArtId,
            CreateRecord("<acct-mid-bad@seg.test>").ArtHash,
            CreateRecord("<acct-mid-bad@seg.test>").ArtData.Span);
        middle[^1] ^= 0xFF;
        var trailing = SegmentRecordCodec.Encode(
            CreateRecord("<acct-mid-tail@seg.test>", "tail-body\r\n").ArtId,
            CreateRecord("<acct-mid-tail@seg.test>", "tail-body\r\n").ArtHash,
            CreateRecord("<acct-mid-tail@seg.test>", "tail-body\r\n").ArtData.Span);
        AppendToClosed(dir, location.SegmentId, middle);
        AppendToClosed(dir, location.SegmentId, trailing);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var before));
        Assert.Equal(0, before.DeadBytes);

        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var after));
        Assert.False(after.ExtentAccountingComplete);
        Assert.Equal(before.DeadBytes, after.DeadBytes);
        Assert.Equal(location.Length, after.LiveBytes);
        Assert.True(after.SizeBytes > after.LiveBytes + after.DeadBytes);
        Assert.False(engine.IsUnreferencedExtentAccountingComplete);
    }

    [Fact]
    public async Task CorruptFinalRecord_DoesNotCountItDead()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-final@seg.test>");
        var location = await SeedClosedAsync(dir, record);
        var finalRecord = SegmentRecordCodec.Encode(
            CreateRecord("<acct-final-orphan@seg.test>").ArtId,
            CreateRecord("<acct-final-orphan@seg.test>").ArtHash,
            CreateRecord("<acct-final-orphan@seg.test>").ArtData.Span);
        finalRecord[^1] ^= 0xFF;
        AppendToClosed(dir, location.SegmentId, finalRecord);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.False(info.ExtentAccountingComplete);
        Assert.Equal(0, info.DeadBytes);
        Assert.Equal(location.Length, info.LiveBytes);
    }

    [Fact]
    public async Task IncompleteFinalRecord_DoesNotCountItDead()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-short@seg.test>");
        var location = await SeedClosedAsync(dir, record);
        AppendToClosed(dir, location.SegmentId, [0x01, 0x02, 0x03]);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.False(info.ExtentAccountingComplete);
        Assert.Equal(0, info.DeadBytes);
        Assert.True(info.SizeBytes > info.LiveBytes + info.DeadBytes);
    }

    [Fact]
    public async Task RepeatedScan_DoesNotAddOrphanBytesAgain()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-repeat@seg.test>");
        var location = await SeedClosedAsync(dir, record);
        var orphan = SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span);
        AppendToClosed(dir, location.SegmentId, orphan);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        engine.CompleteUnreferencedExtentAccounting();
        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.True(info.ExtentAccountingComplete);
        Assert.Equal(location.Length, info.LiveBytes);
        Assert.Equal(orphan.Length, info.DeadBytes);
        Assert.Equal(info.SizeBytes, info.LiveBytes + info.DeadBytes);
    }

    [Fact]
    public async Task ProvedDuplicate_NotNamedByIndex_IsDeadExactlyOnce()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-dup@seg.test>");
        var location = await SeedClosedAsync(dir, record);
        var duplicate = SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span);
        AppendToClosed(dir, location.SegmentId, duplicate);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(location, read.Metadata.Location);

        engine.CompleteUnreferencedExtentAccounting();
        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.Equal(duplicate.Length, info.DeadBytes);
        Assert.Equal(location.Length, info.LiveBytes);
        Assert.True(info.ExtentAccountingComplete);
    }

    [Fact]
    public async Task IndexLocationWithDifferentArticle_IsNotDoubleCounted()
    {
        using var dir = TempStorageDir.Create();
        var original = CreateRecord("<acct-ident-a@seg.test>");
        var other = CreateRecord("<acct-ident-b@seg.test>");
        var location = await SeedClosedAsync(dir, original);
        var replacement = SegmentRecordCodec.Encode(other.ArtId, other.ArtHash, other.ArtData.Span);
        Assert.Equal(location.Length, replacement.Length);
        Overwrite(ClosedPath(dir, location.SegmentId), location.Offset, replacement);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Index.TryGet(original.ArtId, out var meta));
        Assert.Equal(location, meta.Location);
        Assert.False(engine.Index.TryGet(other.ArtId, out _));
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.True(info.ExtentAccountingComplete);
        Assert.Equal(location.Length, info.LiveBytes);
        Assert.Equal(0, info.DeadBytes);
        Assert.Equal(info.SizeBytes, info.LiveBytes + info.DeadBytes);
    }

    [Fact]
    public async Task RecordFailingOwnHashProof_IsNotCountedDead()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-hash@seg.test>");
        var location = await SeedClosedAsync(dir, record);
        var encoded = SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            encoded.AsSpan(8 + ArticleId.Length),
            record.ArtHash + 1);
        var crc = System.IO.Hashing.Crc32.HashToUInt32(encoded.AsSpan(0, encoded.Length - 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(encoded.Length - 4), crc);
        AppendToClosed(dir, location.SegmentId, encoded);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.False(info.ExtentAccountingComplete);
        Assert.Equal(0, info.DeadBytes);
    }

    [Fact]
    public async Task ActiveSegment_IsNeverHistoricallyAccounted()
    {
        using var dir = TempStorageDir.Create();
        var indexed = CreateRecord("<acct-active-indexed@seg.test>");
        await using (var seed = FileArticleStorageEngine.Open(dir.Options))
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await seed.AcceptAsync(indexed, CancellationToken.None)).Outcome);
            await seed.DrainPendingAsync(CancellationToken.None);
        }

        var orphan = SegmentRecordCodec.Encode(
            CreateRecord("<acct-active-orphan@seg.test>").ArtId,
            CreateRecord("<acct-active-orphan@seg.test>").ArtHash,
            CreateRecord("<acct-active-orphan@seg.test>").ArtData.Span);
        var activePath = Directory.GetFiles(dir.Options.SegmentDir, "seg-*.active").Single();
        AppendFile(activePath, orphan);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();

        var active = engine.Catalogue.Snapshot().Single(info => info.State == SegmentState.Active);
        Assert.False(active.ExtentAccountingComplete);
        Assert.Equal(0, active.DeadBytes);
        Assert.True(active.SizeBytes > active.LiveBytes + active.DeadBytes);
        Assert.True(engine.IsUnreferencedExtentAccountingComplete);
    }

    [Fact]
    public async Task ConcurrentEviction_DoesNotExceedSize()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-evict-race@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var location = await AcceptAndCloseAsync(engine, record);
        using var barrier = new Barrier(2);
        engine.TestHookDuringClosedAccountingCommit = () =>
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(15)));
        var evict = Task.Run(() =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(15)));
            Assert.True(engine.TryEvict(record.ArtId));
        });

        engine.CompleteUnreferencedExtentAccounting();
        await evict.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.True(info.ExtentAccountingComplete);
        Assert.Equal(info.SizeBytes, info.LiveBytes + info.DeadBytes);
        Assert.True(info.LiveBytes + info.DeadBytes <= info.SizeBytes);
    }

    [Fact]
    public async Task ConcurrentRelocation_DoesNotDoubleCountSource()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-reloc-race@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var location = await AcceptAndCloseAsync(engine, record);
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var closed));
        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, location.SegmentId, closed.Generation),
                CancellationToken.None));

        using var barrier = new Barrier(2);
        engine.TestHookDuringClosedAccountingCommit = () =>
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(15)));
        var relocate = Task.Run(async () =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(15)));
            return await engine.RelocateArticleAsync(
                compactionId,
                1,
                location.SegmentId,
                closed.Generation,
                record.ArtId,
                CancellationToken.None);
        });

        engine.CompleteUnreferencedExtentAccounting();
        var result = await relocate.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(ArticleRelocationOutcome.Relocated, result.Outcome);
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.True(info.ExtentAccountingComplete);
        Assert.Equal(0, info.LiveBytes);
        Assert.Equal(info.SizeBytes, info.DeadBytes);
        Assert.Equal(info.SizeBytes, info.LiveBytes + info.DeadBytes);
    }

    [Fact]
    public async Task MutationsAfterAccounting_PreserveEquality_AndKeepBit()
    {
        using var dir = TempStorageDir.Create();
        var evict = CreateRecord("<acct-mut-evict@seg.test>");
        var invalidate = CreateRecord("<acct-mut-inv@seg.test>");
        var relocate = CreateRecord("<acct-mut-rel@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(evict, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(invalidate, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(relocate, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(relocate.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var accounted));
        Assert.True(accounted.ExtentAccountingComplete);
        Assert.Equal(accounted.SizeBytes, accounted.LiveBytes + accounted.DeadBytes);

        Assert.True(engine.TryEvict(evict.ArtId));
        AssertBalanced(engine, meta.Location.SegmentId);
        Assert.True(engine.TryInvalidate(invalidate.ArtId));
        AssertBalanced(engine, meta.Location.SegmentId);

        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var source));
        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, source.SegmentId, source.Generation),
                CancellationToken.None));
        var moved = await engine.RelocateArticleAsync(
            compactionId,
            1,
            source.SegmentId,
            source.Generation,
            relocate.ArtId,
            CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, moved.Outcome);
        AssertBalanced(engine, source.SegmentId);
    }

    [Fact]
    public async Task IndexRebuild_ClearsClosedAccountingBits_AndGlobalFlag()
    {
        using var dir = TempStorageDir.Create();
        var first = CreateRecord("<acct-rebuild-a@seg.test>");
        var second = CreateRecord("<acct-rebuild-b@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var firstLocation = await AcceptAndCloseAsync(engine, first);
        var secondLocation = await AcceptAndCloseAsync(engine, second);
        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.IsUnreferencedExtentAccountingComplete);
        Assert.True(engine.Segments.TryGetSegmentInfo(firstLocation.SegmentId, out var firstInfo));
        Assert.True(engine.Segments.TryGetSegmentInfo(secondLocation.SegmentId, out var secondInfo));
        Assert.True(firstInfo.ExtentAccountingComplete);
        Assert.True(secondInfo.ExtentAccountingComplete);

        engine.RebuildSegmentAccountingFromIndex();

        Assert.False(engine.IsUnreferencedExtentAccountingComplete);
        Assert.True(engine.Segments.TryGetSegmentInfo(firstLocation.SegmentId, out firstInfo));
        Assert.True(engine.Segments.TryGetSegmentInfo(secondLocation.SegmentId, out secondInfo));
        Assert.False(firstInfo.ExtentAccountingComplete);
        Assert.False(secondInfo.ExtentAccountingComplete);
    }

    [Fact]
    public async Task OneCorruptClosed_DoesNotBlockAnotherAccountedSegment()
    {
        using var dir = TempStorageDir.Create();
        var bad = CreateRecord("<acct-one-bad@seg.test>");
        var good = CreateRecord("<acct-one-good@seg.test>");
        SegmentId badId;
        SegmentId goodId;
        await using (var seed = FileArticleStorageEngine.Open(dir.Options))
        {
            var badLocation = await AcceptAndCloseAsync(seed, bad);
            badId = badLocation.SegmentId;
            var goodLocation = await AcceptAndCloseAsync(seed, good);
            goodId = goodLocation.SegmentId;
            Assert.True(seed.TryEvict(good.ArtId));
        }

        CorruptByte(ClosedPath(dir, badId), 0);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Segments.TryGetSegmentInfo(badId, out var badInfo));
        Assert.False(badInfo.ExtentAccountingComplete);
        Assert.True(engine.Segments.TryGetSegmentInfo(goodId, out var goodInfo));
        Assert.True(goodInfo.ExtentAccountingComplete);
        Assert.Equal(goodInfo.SizeBytes, goodInfo.LiveBytes + goodInfo.DeadBytes);
        Assert.False(engine.IsUnreferencedExtentAccountingComplete);

        var policy = new ArticleSegmentPolicy(enabled: true, minimumDeadBytes: 0, minimumDeadRatio: 0);
        Assert.Equal(CompactionEligibilityReason.AccountingIncomplete, policy.EvaluateCompaction(badInfo).Reason);
        Assert.True(policy.EvaluateCompaction(goodInfo).IsEligible);
        Assert.True(policy.TrySelectCompactionVictim([badInfo, goodInfo], out var victim));
        Assert.Equal(goodId, victim.SegmentId);
    }

    [Fact]
    public async Task CompactionPolicy_RequiresAccountingThenExistingThresholds()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-policy@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var location = await AcceptAndCloseAsync(engine, record);
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var unaccounted));
        Assert.True(unaccounted.DeadBytes > 0);

        var permissive = new ArticleSegmentPolicy(enabled: true, minimumDeadBytes: 0, minimumDeadRatio: 0);
        Assert.Equal(
            CompactionEligibilityReason.AccountingIncomplete,
            permissive.EvaluateCompaction(unaccounted).Reason);

        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var accounted));
        Assert.True(accounted.ExtentAccountingComplete);
        Assert.True(permissive.EvaluateCompaction(accounted).IsEligible);

        var strict = new ArticleSegmentPolicy(
            enabled: true,
            ArticleCompactionPolicyOptions.DefaultMinimumDeadBytes,
            ArticleCompactionPolicyOptions.DefaultMinimumDeadRatio);
        Assert.Equal(
            CompactionEligibilityReason.InsufficientDeadBytes,
            strict.EvaluateCompaction(accounted).Reason);
    }

    [Fact]
    public async Task Retirement_RefusesUnaccountedClosed_AndAllowsAccounted()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-retire@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var location = await AcceptAndCloseAsync(engine, record);
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var closed));
        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, location.SegmentId, closed.Generation),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionCommittedAsync(
                new JournalCompactionCommittedRecord(1, compactionId),
                CancellationToken.None));

        var refused = await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.RejectedAccountingIncomplete, refused.Outcome);
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var stillClosed));
        Assert.Equal(SegmentState.Closed, stillClosed.State);
        Assert.False(stillClosed.ExtentAccountingComplete);

        engine.CompleteUnreferencedExtentAccounting();
        var retired = await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retired.Outcome);
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var after));
        Assert.Equal(SegmentState.Retired, after.State);
    }

    [Fact]
    public async Task ReclaimRetired_SucceedsWithoutAccountingBit()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-reclaim@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var location = await AcceptAndCloseAsync(engine, record);
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var closed));
        Assert.False(closed.ExtentAccountingComplete);
        Assert.True(engine.Segments.Catalogue.TryRetire(location.SegmentId, closed.Generation, DateTimeOffset.UtcNow));

        var reclaimed = await engine.ReclaimRetiredSegmentAsync(location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimed.Outcome);
        Assert.False(engine.Segments.TryGetSegmentInfo(location.SegmentId, out _));
    }

    [Fact]
    public async Task ScanFailure_PreservesExistingDeadBytes()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-preserve@seg.test>");
        SegmentId segmentId;
        long indexDead;
        await using (var seed = FileArticleStorageEngine.Open(dir.Options))
        {
            var location = await AcceptAndCloseAsync(seed, record);
            segmentId = location.SegmentId;
            Assert.True(seed.TryEvict(record.ArtId));
            Assert.True(seed.Segments.TryGetSegmentInfo(segmentId, out var evicted));
            indexDead = evicted.DeadBytes;
            Assert.True(indexDead > 0);
        }

        AppendToClosed(dir, segmentId, [0x11, 0x22, 0x33, 0x44]);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(segmentId, out var before));
        Assert.Equal(indexDead, before.DeadBytes);
        Assert.False(before.ExtentAccountingComplete);

        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Segments.TryGetSegmentInfo(segmentId, out var after));
        Assert.Equal(indexDead, after.DeadBytes);
        Assert.False(after.ExtentAccountingComplete);
    }

    [Fact]
    public async Task GlobalFlag_TrueOnlyWhenEveryClosedSegmentIsAccounted()
    {
        using var dir = TempStorageDir.Create();
        var first = CreateRecord("<acct-global-a@seg.test>");
        var second = CreateRecord("<acct-global-b@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        _ = await AcceptAndCloseAsync(engine, first);
        Assert.False(engine.IsUnreferencedExtentAccountingComplete);
        _ = await AcceptAndCloseAsync(engine, second);
        Assert.False(engine.IsUnreferencedExtentAccountingComplete);

        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.IsUnreferencedExtentAccountingComplete);
        Assert.All(
            engine.Catalogue.Snapshot().Where(info => info.State == SegmentState.Closed),
            info => Assert.True(info.ExtentAccountingComplete));

        engine.RebuildSegmentAccountingFromIndex();
        Assert.False(engine.IsUnreferencedExtentAccountingComplete);
    }

    private static void AssertBalanced(FileArticleStorageEngine engine, SegmentId segmentId)
    {
        Assert.True(engine.Segments.TryGetSegmentInfo(segmentId, out var info));
        Assert.True(info.ExtentAccountingComplete);
        Assert.Equal(info.SizeBytes, info.LiveBytes + info.DeadBytes);
    }

    private static async Task<StoredArticleLocation> AcceptAndCloseAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        return meta.Location;
    }

    private static async Task<StoredArticleLocation> SeedClosedAsync(TempStorageDir dir, ArticleRecord record)
    {
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        return await AcceptAndCloseAsync(engine, record);
    }

    private static void AppendToClosed(TempStorageDir dir, SegmentId segmentId, byte[] bytes) =>
        AppendFile(ClosedPath(dir, segmentId), bytes);

    private static void AppendFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void Overwrite(string path, long offset, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Position = offset;
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void CorruptByte(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Position = offset;
        var value = stream.ReadByte();
        Assert.True(value >= 0);
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0xFF));
        stream.Flush(flushToDisk: true);
    }

    private static string ClosedPath(TempStorageDir dir, SegmentId segmentId) =>
        Path.Combine(dir.Options.SegmentDir, SegmentFileNames.Format(segmentId, SegmentFileKind.Closed));

    private static ArticleRecord CreateRecord(string messageId, string body = "line1\r\nline2\r\n")
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: accounting\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-acct-" + Guid.NewGuid().ToString("N"));
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
