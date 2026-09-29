using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>Phase 4B.3: live single-article relocation via compaction journal.</summary>
public sealed class FileArticleRelocationTests
{
    [Fact]
    public async Task A_Through_K_SuccessfulRelocation_PreservesIdentityAndAccounting()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-a@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, sourceLoc) = await AcceptCloseAndLocateAsync(engine, record);
        var sourceBytes = await ReadSegmentBytesAsync(dir.Options.SegmentDir, sourceLoc);

        var compactionId = await BeginAsync(engine, sourceId, generation);
        var beforeAppend = engine.PhysicalAppendCount;
        var result = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);

        Assert.Equal(ArticleRelocationOutcome.Relocated, result.Outcome);
        Assert.NotNull(result.DestinationLocation);
        var dest = result.DestinationLocation!.Value;
        Assert.Equal(beforeAppend + 1, engine.PhysicalAppendCount);

        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(record.ArtId, meta.ArtId);
        Assert.Equal(record.ArtHash, meta.ArtHash);
        Assert.Equal(record.ArtSize, meta.ArtSize);
        Assert.Equal(dest, meta.Location);

        Assert.True(engine.Segments.TryReadProven(dest, record.ArtId, record.ArtHash, record.ArtSize, out var destData));
        Assert.True(destData.Span.SequenceEqual(record.ArtData.Span));

        var afterSourceBytes = await ReadSegmentBytesAsync(dir.Options.SegmentDir, sourceLoc);
        Assert.True(afterSourceBytes.AsSpan().SequenceEqual(sourceBytes));

        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var srcInfo));
        Assert.Equal(SegmentState.Closed, srcInfo.State);
        Assert.Equal(0, srcInfo.LiveBytes);
        Assert.True(srcInfo.DeadBytes >= sourceLoc.Length);
        Assert.True(SegmentLifecycle.IsReclaimable(srcInfo));

        Assert.True(engine.Segments.TryGetSegmentInfo(dest.SegmentId, out var destInfo));
        Assert.True(destInfo.LiveBytes >= dest.Length);
        Assert.True(destInfo.DeadBytes < dest.Length || destInfo.LiveBytes > 0);

        var closedNames = Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.closed").ToArray();
        Assert.Contains(closedNames, p => p.Contains(sourceId.Value.ToString("D20"), StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.retired"));
    }

    [Fact]
    public async Task L_ClosedSourceRequired_M_ActiveRejected()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-lm@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var active));
        Assert.Equal(SegmentState.Active, active.State);

        var compactionId = engine.Journal.AllocateCompactionId();
        // Begin against Active is itself invalid for compaction; simulate fence with wrong state path:
        // Relocate without Closed rejects even if we forge Begin after close mismatch.
        _ = await engine.Journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, compactionId, meta.Location.SegmentId, active.Generation),
            CancellationToken.None);
        var rejected = await engine.RelocateArticleAsync(
            compactionId, 1, meta.Location.SegmentId, active.Generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedSourceNotClosed, rejected.Outcome);
    }

    [Fact]
    public async Task N_RetiredSourceRejected()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-n@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, _) = await AcceptCloseAndLocateAsync(engine, record);
        Assert.True(engine.Catalogue.TryRetire(sourceId, generation, DateTimeOffset.UtcNow));
        var compactionId = await BeginAsync(engine, sourceId, generation);
        var result = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedSourceNotClosed, result.Outcome);
    }

    [Fact]
    public async Task O_GenerationMismatchRejected()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-o@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, _) = await AcceptCloseAndLocateAsync(engine, record);
        var compactionId = await BeginAsync(engine, sourceId, generation);
        var result = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation + 99, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedGenerationMismatch, result.Outcome);
    }

    [Fact]
    public async Task P_MissingSourceRejected()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-p@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, _) = await AcceptCloseAndLocateAsync(engine, record);
        var missing = new SegmentId(sourceId.Value + 999);
        var compactionId = engine.Journal.AllocateCompactionId();
        _ = await engine.Journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, compactionId, missing, generation),
            CancellationToken.None);
        var result = await engine.RelocateArticleAsync(
            compactionId, 1, missing, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedSourceMissing, result.Outcome);
    }

    [Fact]
    public async Task Q_ArticleNotPresentRejected()
    {
        using var dir = TempStorageDir.Create();
        var keep = CreateRecord("<rel-q-keep@seg.test>");
        var drop = CreateRecord("<rel-q-drop@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var meta));
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        var compactionId = await BeginAsync(engine, meta.Location.SegmentId, info.Generation);
        var result = await engine.RelocateArticleAsync(
            compactionId, 1, meta.Location.SegmentId, info.Generation, drop.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedNotPresent, result.Outcome);
    }

    [Fact]
    public async Task R_SourcePhysicalCorruptionRejected()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-r@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, sourceLoc) = await AcceptCloseAndLocateAsync(engine, record);
        var compactionId = await BeginAsync(engine, sourceId, generation);

        // Corrupt while store holds the closed file (Share.ReadWrite). Re-open would fail-closed
        // at segment discovery before RelocateArticle can return RejectedSourceCorrupt.
        var path = Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.closed").Single();
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            fs.Position = sourceLoc.Offset + SegmentRecordCodec.FixedHeaderLength;
            var b = new byte[1];
            _ = fs.Read(b, 0, 1);
            b[0] ^= 0xFF;
            fs.Position = sourceLoc.Offset + SegmentRecordCodec.FixedHeaderLength;
            fs.Write(b, 0, 1);
            fs.Flush(true);
        }

        var result = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedSourceCorrupt, result.Outcome);
    }

    [Fact]
    public async Task S_SourceIdentityMismatchRejected_ViaIndexHash()
    {
        // Physical proof uses index ArtHash/ArtSize; corruption that breaks ArtId/hash proof is covered by R.
        // Here: article on wrong segment relative to CompactionBegin source.
        using var dir = TempStorageDir.Create();
        var a = CreateRecord("<rel-s-a@seg.test>");
        var b = CreateRecord("<rel-s-b@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(a, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(b, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        Assert.True(engine.Index.TryGet(a.ArtId, out var metaA));
        Assert.True(engine.Segments.TryGetSegmentInfo(metaA.Location.SegmentId, out var infoA));
        var compactionId = await BeginAsync(engine, metaA.Location.SegmentId, infoA.Generation);
        var result = await engine.RelocateArticleAsync(
            compactionId, 1, metaA.Location.SegmentId, infoA.Generation, b.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedNotPresent, result.Outcome);
    }

    [Fact]
    public async Task TUV_DurableOrdering_Intent_Append_Written_Index()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-tuv@seg.test>");

        // T: Intent durable before destination append (fault after Intent).
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            var (sourceId, generation, _) = await AcceptCloseAndLocateAsync(engine, record);
            var compactionId = await BeginAsync(engine, sourceId, generation);
            engine.TestRelocationFaultPoint =
                FileArticleStorageEngine.RelocationFaultPoint.AfterIntentBeforeAppend;
            var ex = await Assert.ThrowsAsync<IOException>(() =>
                engine.RelocateArticleAsync(compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None));
            Assert.Contains("AfterIntentBeforeAppend", ex.Message, StringComparison.Ordinal);
            Assert.True(engine.Journal.TryGetCompaction(compactionId, out var snap));
            var rel = Assert.Single(snap.Relocations);
            Assert.Null(rel.Written);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            Assert.Equal(sourceId, meta.Location.SegmentId);
        }

        // U: Destination durable before Written (fault after append).
        // Pass Begin.SourceGeneration — catalogue generations are process-local and reallocated on Open.
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            await engine.RecoverAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            var begin = engine.Journal.EnumerateOpenCompactions().Single().Begin;
            var appendsBefore = engine.PhysicalAppendCount;
            engine.TestRelocationFaultPoint =
                FileArticleStorageEngine.RelocationFaultPoint.AfterAppendBeforeWritten;
            _ = await Assert.ThrowsAsync<IOException>(() =>
                engine.RelocateArticleAsync(
                    begin.CompactionId,
                    1,
                    begin.SourceSegmentId,
                    begin.SourceGeneration,
                    record.ArtId,
                    CancellationToken.None));
            Assert.True(engine.PhysicalAppendCount > appendsBefore);
            Assert.True(engine.Journal.TryGetCompaction(begin.CompactionId, out var snap));
            Assert.Null(Assert.Single(snap.Relocations).Written);
            Assert.Equal(begin.SourceSegmentId, meta.Location.SegmentId);
        }

        // V: Written durable before index CAS (fault after Written).
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            await engine.RecoverAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            var begin = engine.Journal.EnumerateOpenCompactions().Single().Begin;
            engine.TestRelocationFaultPoint =
                FileArticleStorageEngine.RelocationFaultPoint.AfterWrittenBeforeIndex;
            _ = await Assert.ThrowsAsync<IOException>(() =>
                engine.RelocateArticleAsync(
                    begin.CompactionId,
                    1,
                    begin.SourceSegmentId,
                    begin.SourceGeneration,
                    record.ArtId,
                    CancellationToken.None));
            Assert.True(engine.Journal.TryGetCompaction(begin.CompactionId, out var snap));
            Assert.NotNull(Assert.Single(snap.Relocations).Written);
            Assert.True(engine.Index.TryGet(record.ArtId, out meta));
            Assert.Equal(begin.SourceSegmentId, meta.Location.SegmentId);
        }
    }

    [Fact]
    public async Task W_DestinationAppendFailure_LeavesSourceAuthoritative()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-w@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, sourceLoc) = await AcceptCloseAndLocateAsync(engine, record);
        var compactionId = await BeginAsync(engine, sourceId, generation);
        engine.TestRelocationFaultPoint =
            FileArticleStorageEngine.RelocationFaultPoint.AfterIntentBeforeAppend;
        _ = await Assert.ThrowsAsync<IOException>(() =>
            engine.RelocateArticleAsync(compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(sourceLoc, meta.Location);
        Assert.Equal(ArticleStorageState.Present, meta.State);
    }

    [Fact]
    public async Task X_ExpectedLocationMismatch_DoesNotOverwrite()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-x@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, sourceLoc) = await AcceptCloseAndLocateAsync(engine, record);
        var compactionId = await BeginAsync(engine, sourceId, generation);

        StoredArticleLocation? newerLoc = null;
        engine.TestHookBeforeIndexRelocate = () =>
        {
            // Simulate a prior relocation that already moved the index.
            var appender = engine.Segments.GetActiveAppenderAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            newerLoc = appender.AppendAsync(record.ArtData, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var moved = engine.Index.TryRelocate(
                record.ArtId, sourceLoc, newerLoc.Value, record.ArtHash, record.ArtSize);
            Assert.Equal(ArticleRelocateOutcome.Relocated, moved);
        };

        var result = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Abandoned, result.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(newerLoc, meta.Location);
        Assert.NotEqual(result.DestinationLocation, meta.Location);
    }

    [Fact]
    public async Task Y_ConcurrentEviction_DoesNotResurrect()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-y@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, _) = await AcceptCloseAndLocateAsync(engine, record);
        var compactionId = await BeginAsync(engine, sourceId, generation);
        engine.TestHookBeforeIndexRelocate = () => Assert.True(engine.TryEvict(record.ArtId));
        var result = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Abandoned, result.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Evicted, meta.State);
        Assert.True(engine.Segments.TryGetSegmentInfo(result.DestinationLocation!.Value.SegmentId, out var dest));
        Assert.True(dest.DeadBytes >= result.DestinationLocation.Value.Length);
    }

    [Fact]
    public async Task Z_ConcurrentInvalidation_DoesNotResurrect()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-z@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, _) = await AcceptCloseAndLocateAsync(engine, record);
        var compactionId = await BeginAsync(engine, sourceId, generation);
        engine.TestHookBeforeIndexRelocate = () => Assert.True(engine.TryInvalidate(record.ArtId));
        var result = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Abandoned, result.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Invalid, meta.State);
    }

    [Fact]
    public async Task AA_ConcurrentPriorRelocation_DoesNotOverwriteNewerLocation()
    {
        // Same seam as X — prior CAS wins; stale Written destination is abandoned.
        await X_ExpectedLocationMismatch_DoesNotOverwrite();
    }

    [Fact]
    public async Task AB_RetryAfterIntentOnly()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-ab@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, _) = await AcceptCloseAndLocateAsync(engine, record);
        var compactionId = await BeginAsync(engine, sourceId, generation);
        engine.TestRelocationFaultPoint =
            FileArticleStorageEngine.RelocationFaultPoint.AfterIntentBeforeAppend;
        _ = await Assert.ThrowsAsync<IOException>(() =>
            engine.RelocateArticleAsync(compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None));

        var result = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, result.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(result.DestinationLocation, meta.Location);
    }

    [Fact]
    public async Task AC_AE_AX_RetryAfterWritten_ReusesDestination_NoSecondAppend()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-ac@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, _) = await AcceptCloseAndLocateAsync(engine, record);
        var compactionId = await BeginAsync(engine, sourceId, generation);
        engine.TestRelocationFaultPoint =
            FileArticleStorageEngine.RelocationFaultPoint.AfterWrittenBeforeIndex;
        _ = await Assert.ThrowsAsync<IOException>(() =>
            engine.RelocateArticleAsync(compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None));

        Assert.True(engine.Journal.TryGetCompaction(compactionId, out var snap));
        var writtenLoc = Assert.Single(snap.Relocations).Written!.Value.DestinationLocation;
        var appends = engine.PhysicalAppendCount;

        var result = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, result.Outcome);
        Assert.Equal(writtenLoc, result.DestinationLocation);
        Assert.Equal(appends, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task AD_RetryAfterSuccessfulRelocation_Idempotent()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-ad@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, _) = await AcceptCloseAndLocateAsync(engine, record);
        var compactionId = await BeginAsync(engine, sourceId, generation);
        var first = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, first.Outcome);
        var appends = engine.PhysicalAppendCount;
        var second = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.IdempotentNoOp, second.Outcome);
        Assert.Equal(first.DestinationLocation, second.DestinationLocation);
        Assert.Equal(appends, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task AF_CorruptWrittenDestination_FailsClosed()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-af@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, sourceLoc) = await AcceptCloseAndLocateAsync(engine, record);
        var compactionId = await BeginAsync(engine, sourceId, generation);
        var bogus = new StoredArticleLocation(sourceId, sourceLoc.Offset + 1, sourceLoc.Length);
        _ = await engine.Journal.AppendRelocationIntentAsync(
            new JournalRelocationIntentRecord(
                1, compactionId, 1, record.ArtId, record.ArtHash, record.ArtSize, sourceLoc),
            CancellationToken.None);
        _ = await engine.Journal.AppendRelocationWrittenAsync(
            new JournalRelocationWrittenRecord(1, compactionId, 1, bogus),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.RelocateArticleAsync(compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None));
    }

    [Fact]
    public async Task AG_AH_AI_EvictedAndInvalidWritten_BecomeDeadDestination()
    {
        using var dir = TempStorageDir.Create();
        var evicted = CreateRecord("<rel-ag@seg.test>");
        var invalid = CreateRecord("<rel-ah@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(evicted, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(invalid, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(evicted.ArtId, out var metaE));
        Assert.True(engine.Segments.TryGetSegmentInfo(metaE.Location.SegmentId, out var info));
        var sourceId = metaE.Location.SegmentId;
        var compactionId = await BeginAsync(engine, sourceId, info.Generation);

        engine.TestHookBeforeIndexRelocate = () => Assert.True(engine.TryEvict(evicted.ArtId));
        var r1 = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, info.Generation, evicted.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Abandoned, r1.Outcome);
        Assert.True(engine.Segments.TryGetSegmentInfo(r1.DestinationLocation!.Value.SegmentId, out var d1));
        Assert.True(d1.DeadBytes >= r1.DestinationLocation.Value.Length);

        engine.TestHookBeforeIndexRelocate = () => Assert.True(engine.TryInvalidate(invalid.ArtId));
        var r2 = await engine.RelocateArticleAsync(
            compactionId, 2, sourceId, info.Generation, invalid.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Abandoned, r2.Outcome);
        Assert.True(engine.Segments.TryGetSegmentInfo(r2.DestinationLocation!.Value.SegmentId, out var d2));
        Assert.True(d2.DeadBytes >= r2.DestinationLocation.Value.Length);
    }

    [Fact]
    public async Task AJ_AK_AL_AM_CacheIndependentOfRelocation()
    {
        using var dir = TempStorageDir.Create();
        var cached = CreateRecord("<rel-aj@seg.test>");
        var uncached = CreateRecord("<rel-ak@seg.test>");
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(cached, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(uncached, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        // Accept may populate cache on IndexCommitted; force the AK miss case explicitly.
        _ = cache.Remove(uncached.ArtId);
        Assert.True(engine.TryRead(cached.ArtId, out _));
        var putsBefore = cache.PutCount;
        var removesBefore = cache.RemoveCount;

        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(cached.ArtId, out var meta));
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        var compactionId = await BeginAsync(engine, meta.Location.SegmentId, info.Generation);

        _ = await engine.RelocateArticleAsync(
            compactionId, 1, meta.Location.SegmentId, info.Generation, cached.ArtId, CancellationToken.None);
        _ = await engine.RelocateArticleAsync(
            compactionId, 2, meta.Location.SegmentId, info.Generation, uncached.ArtId, CancellationToken.None);

        Assert.Equal(putsBefore, cache.PutCount);
        Assert.Equal(removesBefore, cache.RemoveCount);
        Assert.True(cache.Inner.TryGet(cached.ArtId, out var hit));
        Assert.Equal(cached.ArtHash, hit.ArtHash);
        Assert.False(cache.Inner.TryGet(uncached.ArtId, out _));
    }

    [Fact]
    public async Task AN_AP_RestartAfterSuccessfulRelocation()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-an@seg.test>");
        StoredArticleLocation dest;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            var (sourceId, generation, _) = await AcceptCloseAndLocateAsync(engine, record);
            var compactionId = await BeginAsync(engine, sourceId, generation);
            var result = await engine.RelocateArticleAsync(
                compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
            Assert.Equal(ArticleRelocationOutcome.Relocated, result.Outcome);
            dest = result.DestinationLocation!.Value;
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.True(engineB.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(dest, meta.Location);
        Assert.True(engineB.Segments.TryReadProven(dest, record.ArtId, record.ArtHash, record.ArtSize, out _));
    }

    [Fact]
    public async Task AO_RestartAfterWrittenPlusSourceIndex()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-ao@seg.test>");
        StoredArticleLocation dest;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            var (sourceId, generation, _) = await AcceptCloseAndLocateAsync(engine, record);
            var compactionId = await BeginAsync(engine, sourceId, generation);
            engine.TestRelocationFaultPoint =
                FileArticleStorageEngine.RelocationFaultPoint.AfterWrittenBeforeIndex;
            _ = await Assert.ThrowsAsync<IOException>(() =>
                engine.RelocateArticleAsync(compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None));
            dest = engine.Journal.EnumerateOpenCompactions().Single().Relocations.Single().Written!.Value.DestinationLocation;
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.True(engineB.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(dest, meta.Location);
    }

    [Fact]
    public async Task AQ_AR_RestartAfterWrittenPlusEvictedOrInvalid()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rel-aq@seg.test>");
        StoredArticleLocation dest;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            var (sourceId, generation, _) = await AcceptCloseAndLocateAsync(engine, record);
            var compactionId = await BeginAsync(engine, sourceId, generation);
            engine.TestRelocationFaultPoint =
                FileArticleStorageEngine.RelocationFaultPoint.AfterWrittenBeforeIndex;
            _ = await Assert.ThrowsAsync<IOException>(() =>
                engine.RelocateArticleAsync(compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None));
            dest = engine.Journal.EnumerateOpenCompactions().Single().Relocations.Single().Written!.Value.DestinationLocation;
            Assert.True(engine.TryEvict(record.ArtId));
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.True(engineB.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Evicted, meta.State);
        Assert.True(engineB.Segments.TryGetSegmentInfo(dest.SegmentId, out var destInfo));
        Assert.True(destInfo.DeadBytes >= dest.Length);
    }

    [Fact]
    public async Task AS_AT_AU_AV_AW_MultipleRelocations_Rotation_SourceRemainsClosed_NoRetire()
    {
        using var dir = TempStorageDir.Create();
        var records = Enumerable.Range(0, 4)
            .Select(i => CreateRecord($"<rel-as-{i}@seg.test>", new string('x', 80) + "\r\n"))
            .ToArray();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        foreach (var r in records)
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(r, CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(records[0].ArtId, out var meta0));
        var sourceId = meta0.Location.SegmentId;
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var srcInfo));
        var generation = srcInfo.Generation;
        var compactionId = await BeginAsync(engine, sourceId, generation);

        var destSegments = new HashSet<ulong>();
        for (ulong i = 0; i < (ulong)records.Length; i++)
        {
            Assert.True(engine.Index.TryGet(records[i].ArtId, out var meta));
            Assert.Equal(sourceId, meta.Location.SegmentId);
            var result = await engine.RelocateArticleAsync(
                compactionId, i + 1, sourceId, generation, records[i].ArtId, CancellationToken.None);
            Assert.Equal(ArticleRelocationOutcome.Relocated, result.Outcome);
            destSegments.Add(result.DestinationLocation!.Value.SegmentId.Value);
            // Force destination rotation without shrinking SegmentTargetSizeBytes (Accept would rotate too).
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
        }

        Assert.True(destSegments.Count >= 2, "expected destination rotation via CloseActive between relocates");
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var after));
        Assert.Equal(SegmentState.Closed, after.State);
        Assert.True(SegmentLifecycle.IsReclaimable(after));
        Assert.Empty(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.retired"));

        // AW: retired segments are never selected as Active destinations.
        var retiredCandidate = destSegments.First();
        Assert.True(engine.Segments.TryGetSegmentInfo(new SegmentId(retiredCandidate), out var closedDest));
        if (closedDest.State == SegmentState.Closed)
        {
            Assert.True(engine.Catalogue.TryRetire(
                new SegmentId(retiredCandidate), closedDest.Generation, DateTimeOffset.UtcNow));
        }

        var keep = CreateRecord("<rel-aw-keep@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var keepMeta));
        Assert.NotEqual(retiredCandidate, keepMeta.Location.SegmentId.Value);
    }

    private static async Task<(SegmentId SourceId, ulong Generation, StoredArticleLocation Location)>
        AcceptCloseAndLocateAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
        return (meta.Location.SegmentId, info.Generation, meta.Location);
    }

    private static async Task<ulong> BeginAsync(
        FileArticleStorageEngine engine,
        SegmentId sourceId,
        ulong generation)
    {
        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, sourceId, generation),
                CancellationToken.None));
        return compactionId;
    }

    private static async Task<byte[]> ReadSegmentBytesAsync(string segmentDir, StoredArticleLocation location)
    {
        var path = Directory.EnumerateFiles(segmentDir, "seg-*")
            .OrderBy(static p => p)
            .First(p => p.Contains(location.SegmentId.Value.ToString("D20"), StringComparison.Ordinal));
        var bytes = await File.ReadAllBytesAsync(path);
        return bytes.AsSpan((int)location.Offset, location.Length).ToArray();
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
        _ = builder.Append("Subject: relocate\r\n");
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

        public static TempStorageDir Create(long? targetSegmentBytes = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-relocate-" + Guid.NewGuid().ToString("N"));
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
                    SegmentTargetSizeBytes: targetSegmentBytes ?? ArticleStorageOptions.DefaultSegmentTargetSizeBytes));
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

    private sealed class RecordingArticleMemoryCache : IArticleMemoryCache
    {
        private long _tryGet;
        private long _hit;
        private long _put;
        private long _remove;

        public RecordingArticleMemoryCache(IArticleMemoryCache inner) => Inner = inner;

        public IArticleMemoryCache Inner { get; }

        public long MaxBytes => Inner.MaxBytes;

        public long CurrentBytes => Inner.CurrentBytes;

        public int Count => Inner.Count;

        public int PutCount => (int)Interlocked.Read(ref _put);

        public int RemoveCount => (int)Interlocked.Read(ref _remove);

        public int HitCount => (int)Interlocked.Read(ref _hit);

        public bool TryGet(ArticleId artId, out ArticleRecord record)
        {
            _ = Interlocked.Increment(ref _tryGet);
            if (Inner.TryGet(artId, out record))
            {
                _ = Interlocked.Increment(ref _hit);
                return true;
            }

            record = default!;
            return false;
        }

        public ArticleMemoryCachePutOutcome Put(in ArticleRecord record)
        {
            _ = Interlocked.Increment(ref _put);
            return Inner.Put(in record);
        }

        public bool Remove(ArticleId artId)
        {
            _ = Interlocked.Increment(ref _remove);
            return Inner.Remove(artId);
        }

        public void Clear() => Inner.Clear();
    }
}
