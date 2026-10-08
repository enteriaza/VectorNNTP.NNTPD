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

/// <summary>
/// Phase 3B/3C/3D: RAM cache integration on durable FileArticleStorageEngine
/// (read path, write-path populate, eviction/invalidation coherence).
/// </summary>
public sealed class FileArticleStorageEngineCacheTests
{
    [Fact]
    public async Task A_CacheMiss_ReadsDurable()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<a@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        // Drop write-path population so this read is a true miss.
        Assert.True(cache.Remove(record.ArtId));
        cache.ResetCounters();

        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, cache.TryGetCount);
        Assert.Equal(1, cache.PutCount);
        Assert.Equal(0, cache.HitCount);
    }

    [Fact]
    public async Task B_SuccessfulDurableRead_PopulatesCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<b@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(cache.Remove(record.ArtId));
        cache.ResetCounters();

        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Equal(1, cache.PutCount);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.Inner.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task C_SecondRead_IsCacheHit()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<c@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        // Write path already populated; first read is a hit.
        cache.ResetCounters();
        Assert.True(engine.TryRead(record.ArtId, out _));
        var putsAfterFirst = cache.PutCount;

        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Equal(2, cache.TryGetCount);
        Assert.Equal(putsAfterFirst, cache.PutCount);
        Assert.Equal(2, cache.HitCount);
    }

    [Fact]
    public async Task D_CacheHit_WithoutIndexRow_IsNotReadable()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<d@cache.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options, articleCache: cache))
        {
            await AcceptAndDrainAsync(engineA, record);
            Assert.True(engineA.TryRead(record.ArtId, out _));
        }

        // Cache must not publish an article the reopened index does not.
        File.Delete(Path.Combine(dir.Options.ControlDir, FileArticleIndex.IndexFileName));

        await using var engineB = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        Assert.False(engineB.TryRead(record.ArtId, out _));
        Assert.False(cache.Inner.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task E_CacheHit_DoesNotRequireSegmentFile()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<e@cache.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options, articleCache: cache))
        {
            await AcceptAndDrainAsync(engineA, record);
            engineA.TestFailNextIndexedProvenReads = 1;
            Assert.True(engineA.TryRead(record.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
            Assert.Equal(0, engineA.SegmentArticleReadCount);
            Assert.Equal(1, engineA.TestFailNextIndexedProvenReads);
        }

        foreach (var path in Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*"))
        {
            File.Delete(path);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        Assert.False(engineB.TryRead(record.ArtId, out _));
        Assert.False(cache.Inner.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task F_CacheHit_ReturnsCorrectRecord()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<f@cache.test>", "payload-f\r\n");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out var hit));
        Assert.Equal(record.ArtId, hit.Metadata.ArtId);
        Assert.Equal(record.ArtHash, hit.Metadata.ArtHash);
        Assert.Equal(record.ArtSize, hit.Metadata.ArtSize);
        Assert.True(hit.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task G_CacheDisabled_BehavesLikeNoCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(maxBytes: 0));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<g@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.TryRead(record.ArtId, out var first));
        Assert.True(engine.TryRead(record.ArtId, out var second));
        Assert.True(first.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(second.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.HitCount);
        Assert.True(cache.PutCount >= 1);
        Assert.Equal(ArticleMemoryCachePutOutcome.RejectedDisabled, cache.LastPutOutcome);
    }

    [Fact]
    public async Task H_Oversized_FallsThroughWithoutCacheFailure()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<h@cache.test>", "oversized-for-tiny-cache\r\n");
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(maxBytes: record.ArtSize - 1));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        // Accept Put + read-path Put both reject oversized; durable read still succeeds.
        Assert.True(cache.PutCount >= 2);
        Assert.Equal(ArticleMemoryCachePutOutcome.RejectedOversized, cache.LastPutOutcome);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task I_PutFailure_DoesNotFailDurableRead()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new RejectAllPutsCache());
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<i@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        // Accept Put + read-path Put both fail; durable read still succeeds.
        Assert.True(cache.PutCount >= 2);
    }

    [Fact]
    public async Task J_FailedDurableRead_DoesNotPopulateCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        Assert.False(engine.TryRead(CreateRecord("<j-miss@cache.test>").ArtId, out _));
        Assert.Equal(0, cache.PutCount);
    }

    [Fact]
    public async Task K_MissingArticle_DoesNotPopulateCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var missing = CreateRecord("<k@cache.test>");
        Assert.False(engine.TryRead(missing.ArtId, out _));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.PutCount);
    }

    [Fact]
    public async Task L_CorruptPhysical_DoesNotPopulateCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<l@cache.test>");
        StoredArticleLocation location;
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options, articleCache: cache))
        {
            await AcceptAndDrainAsync(engineA, record);
            Assert.True(engineA.Index.TryGet(record.ArtId, out var meta));
            location = meta.Location;
        }

        CorruptSegmentPayload(dir.Options.SegmentDir, location);
        cache.Inner.Clear();
        cache.ResetCounters();
        var segmentPath = Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*").Single();
        var segmentBytes = File.ReadAllBytes(segmentPath);

        var ex = Assert.Throws<SegmentStoreCorruptException>(
            () => FileArticleStorageEngine.Open(dir.Options, articleCache: cache));
        Assert.Contains("CorruptChecksum", ex.Message, StringComparison.Ordinal);
        Assert.Equal(segmentBytes, File.ReadAllBytes(segmentPath));
        Assert.Equal(0, cache.PutCount);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task MNO_P_CacheHit_PreservesIdentityAndArtData()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<mnop@cache.test>", "exact-bytes\r\n");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out var hit));
        Assert.Equal(record.ArtId, hit.Metadata.ArtId);
        Assert.Equal(record.ArtHash, hit.Metadata.ArtHash);
        Assert.Equal(record.ArtSize, hit.Metadata.ArtSize);
        Assert.True(hit.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Q_CacheHit_UpdatesLruRecency()
    {
        using var dir = TempStorageDir.Create();
        var a = CreateRecord("<q-a@cache.test>", "a\r\n");
        var b = CreateRecord("<q-b@cache.test>", "b\r\n");
        var cache = new ArticleMemoryCache(maxBytes: a.ArtSize + b.ArtSize);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        await AcceptAndDrainAsync(engine, a);
        await AcceptAndDrainAsync(engine, b);
        Assert.True(engine.TryRead(a.ArtId, out _));
        Assert.True(engine.TryRead(b.ArtId, out _));
        Assert.True(engine.TryRead(a.ArtId, out _)); // a MRU

        var c = CreateRecord("<q-c@cache.test>", "c\r\n");
        await AcceptAndDrainAsync(engine, c);
        Assert.True(engine.TryRead(c.ArtId, out _)); // may evict LRU (b)
        Assert.True(cache.TryGet(a.ArtId, out _) || cache.Count <= 2);
        Assert.True(engine.TryRead(a.ArtId, out _) || engine.Index.TryGet(a.ArtId, out _));
    }

    [Fact]
    public async Task R_Accept_Alone_DoesNotPopulateCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<r@cache.test>");
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);
        Assert.Equal(0, cache.PutCount); // journal Accept alone — no IndexCommitted yet
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task STUV_Recovery_PopulatesCacheOnlyAfterIndexCommitted()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<stuv@cache.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(0, cache.PutCount);
        Assert.Single(engine.Journal.EnumerateIncomplete());

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(1, cache.PutCount); // after durable IndexCommitted
        Assert.True(cache.TryGet(record.ArtId, out var cached));
        Assert.True(cached.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(engine.Index.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task W_Eviction_InvalidatesCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<w@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Equal(1, cache.Count);

        Assert.True(engine.TryEvict(record.ArtId));
        Assert.Equal(1, cache.RemoveCount);
        Assert.Equal(0, cache.Count);
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task X_Invalidation_InvalidatesCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<x@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.TryInvalidate(record.ArtId));
        Assert.Equal(0, cache.Count);
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Y_AfterCacheRemove_FallsThroughToDurable()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<y@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.True(cache.Remove(record.ArtId));
        cache.ResetCounters();

        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(0, cache.HitCount);
        Assert.Equal(1, cache.PutCount);
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Z_NewEngine_StartsEmptyCache()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<z@cache.test>");
        await using (var engineA = FileArticleStorageEngine.Open(
                         dir.Options,
                         articleCache: new ArticleMemoryCache(4L * 1024 * 1024)))
        {
            await AcceptAndDrainAsync(engineA, record);
            Assert.True(engineA.TryRead(record.ArtId, out _));
            Assert.Equal(1, engineA.ArticleCache.Count);
        }

        await using var engineB = FileArticleStorageEngine.Open(
            dir.Options,
            articleCache: new ArticleMemoryCache(4L * 1024 * 1024));
        Assert.Equal(0, engineB.ArticleCache.Count);
        Assert.True(engineB.TryRead(record.ArtId, out _)); // durable still works
    }

    [Fact]
    public async Task AA_DurableSurvives_CacheLoss()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<aa@cache.test>");
        await using (var engineA = FileArticleStorageEngine.Open(
                         dir.Options,
                         articleCache: new ArticleMemoryCache(4L * 1024 * 1024)))
        {
            await AcceptAndDrainAsync(engineA, record);
            Assert.True(engineA.TryRead(record.ArtId, out _));
        }

        await using var engineB = FileArticleStorageEngine.Open(
            dir.Options,
            articleCache: new ArticleMemoryCache(maxBytes: 0));
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task AB_MultipleArticles_CachedAndRead()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(8L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var records = Enumerable.Range(0, 5)
            .Select(i => CreateRecord($"<ab-{i}@cache.test>", $"body-{i}\r\n"))
            .ToArray();
        foreach (var record in records)
        {
            await AcceptAndDrainAsync(engine, record);
            Assert.True(engine.TryRead(record.ArtId, out _));
        }

        Assert.Equal(5, cache.Count);
        foreach (var record in records)
        {
            Assert.True(engine.TryRead(record.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        }
    }

    [Fact]
    public async Task AC_ConcurrentReads_Correct()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(8L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var records = Enumerable.Range(0, 8)
            .Select(i => CreateRecord($"<ac-{i}@cache.test>", $"c{i}\r\n"))
            .ToArray();
        foreach (var record in records)
        {
            await AcceptAndDrainAsync(engine, record);
        }

        await Task.WhenAll(records.Select(r => Task.Run(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                Assert.True(engine.TryRead(r.ArtId, out var read));
                Assert.Equal(r.ArtId, read.Metadata.ArtId);
            }
        })));
    }

    [Fact]
    public async Task AD_ConcurrentCacheHits_Correct()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<ad@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        cache.ResetCounters();
        Assert.True(engine.TryRead(record.ArtId, out _)); // hit from write-path populate

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            Assert.True(engine.TryRead(record.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        })));
        Assert.True(cache.HitCount >= 16);
    }

    [Fact]
    public async Task AE_RepeatedHitMissCycles_Correct()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<ae@cache.test>");
        await AcceptAndDrainAsync(engine, record);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(engine.TryRead(record.ArtId, out _));
            Assert.True(cache.Remove(record.ArtId));
            Assert.True(engine.TryRead(record.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        }
    }

    // ---- Phase 3C write-path population ----

    [Fact]
    public async Task Write_A_SuccessfulAccept_PopulatesCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<wa@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.Equal(1, cache.PutCount);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.Inner.TryGet(record.ArtId, out var cached));
        Assert.Equal(record.ArtId, cached.ArtId);
    }

    [Fact]
    public async Task Write_BCDE_CachedArticle_MatchesAcceptedFields()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<wbcde@cache.test>", "exact-write\r\n");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(cache.TryGet(record.ArtId, out var cached));
        Assert.Equal(record.ArtId, cached.ArtId);
        Assert.Equal(record.ArtHash, cached.ArtHash);
        Assert.Equal(record.ArtSize, cached.ArtSize);
        Assert.True(cached.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Write_FG_PutOnlyAfterDurableCommit_NotAfterAcceptAlone()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<wfg@cache.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(0, cache.PutCount);
        Assert.Null(Assert.Single(engine.Journal.EnumerateIncomplete()).PhysicalWritten);

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(1, cache.PutCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Write_H_SataFailure_DoesNotPopulateCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<wh@cache.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;
        await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Equal(0, cache.PutCount);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Write_I_PhysicalWrittenFailure_DoesNotPopulateCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<wi@cache.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforePhysicalWritten;
        await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Equal(0, cache.PutCount);
    }

    [Fact]
    public async Task Write_J_IndexPresentFailure_DoesNotPopulateCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<wj@cache.test>", "body-a\r\n");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options, articleCache: cache))
        {
            engineA.SuspendBackgroundPersist = true;
            var accept = await engineA.AcceptAsync(record, CancellationToken.None);
            var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            var location = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            _ = await engineA.Journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                CancellationToken.None);
            Assert.True(
                engineA.Index.TryCommitPresent(
                    new StoredArticleMetadata(
                        record.ArtId,
                        record.ArtHash ^ 1UL,
                        record.ArtSize,
                        new StoredArticleLocation(new SegmentId(99), 0, location.Length),
                        ArticleStorageState.Present,
                        DateTimeOffset.UtcNow,
                        0UL)));
        }

        cache.ResetCounters();
        await using var engineB = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engineB.SuspendBackgroundPersist = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => engineB.RecoverAsync(CancellationToken.None));
        Assert.Equal(0, cache.PutCount);
    }

    [Fact]
    public async Task Write_K_IndexCommittedFailure_DoesNotPopulateCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<wk@cache.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeIndexCommitted;
        await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Equal(0, cache.PutCount);
        Assert.Single(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Write_L_CachePutFailure_StillDurableSuccess()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new RejectAllPutsCache());
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<wl@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(cache.PutCount >= 1);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.True(engine.TryRead(record.ArtId, out _)); // durable read still works
    }

    [Fact]
    public async Task Write_MN_DisabledOrOversized_DoesNotFailAccept()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<wmn@cache.test>", "body\r\n");
        var disabled = new RecordingArticleMemoryCache(new ArticleMemoryCache(maxBytes: 0));
        await using (var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: disabled))
        {
            await AcceptAndDrainAsync(engine, record);
            Assert.Equal(ArticleMemoryCachePutOutcome.RejectedDisabled, disabled.LastPutOutcome);
            Assert.True(engine.TryRead(record.ArtId, out _));
        }

        var oversized = new RecordingArticleMemoryCache(new ArticleMemoryCache(maxBytes: record.ArtSize - 1));
        await using (var engine2 = FileArticleStorageEngine.Open(dir.Options, articleCache: oversized))
        {
            // Already Present → Duplicate; Put may be attempted on duplicate path with oversized reject.
            var dup = await engine2.AcceptAsync(record, CancellationToken.None);
            Assert.Equal(ArticleAcceptOutcome.Duplicate, dup.Outcome);
            Assert.True(engine2.TryRead(record.ArtId, out _));
        }
    }

    [Fact]
    public async Task Write_OPQR_DuplicateAndConflict_CacheSemantics()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var first = CreateRecord("<wopq@cache.test>", "body-a\r\n");
        await AcceptAndDrainAsync(engine, first);
        Assert.True(cache.Inner.TryGet(first.ArtId, out var before));
        var appendsBefore = engine.PhysicalAppendCount;

        var dup = await engine.AcceptAsync(first, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Duplicate, dup.Outcome);
        Assert.Equal(appendsBefore, engine.PhysicalAppendCount);
        Assert.True(cache.Inner.TryGet(first.ArtId, out var afterDup));
        Assert.True(afterDup.ArtData.Span.SequenceEqual(before.ArtData.Span));

        var conflict = CreateRecord("<wopq@cache.test>", "body-b\r\n");
        Assert.Equal(ArticleAcceptOutcome.Conflict, (await engine.AcceptAsync(conflict, CancellationToken.None)).Outcome);
        Assert.True(cache.Inner.TryGet(first.ArtId, out var afterConflict));
        Assert.True(afterConflict.ArtData.Span.SequenceEqual(first.ArtData.Span));
    }

    [Fact]
    public async Task Write_ST_RepopulateAfterEvictOrInvalidate()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<wst@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.Equal(0, cache.Count);

        // Evicted: Accept of same identity creates a new Present via journal path.
        var again = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, again.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(cache.Count >= 1);
        Assert.True(cache.Inner.TryGet(record.ArtId, out _));

        Assert.True(engine.TryInvalidate(record.ArtId));
        Assert.Equal(0, cache.Count);
        var third = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, third.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(cache.Inner.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task Write_UV_CrashBeforePut_AndNewEngineEmptyCache()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<wuv@cache.test>");
        await using (var engineA = FileArticleStorageEngine.Open(
                         dir.Options,
                         articleCache: new ArticleMemoryCache(4L * 1024 * 1024)))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            engineA.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterIndexCommitted;
            // IndexCommitted durable; injected fault after best-effort Put — simulate crash after commit.
            await Assert.ThrowsAsync<IOException>(() => engineA.RecoverAsync(CancellationToken.None));
            Assert.Empty(engineA.Journal.EnumerateIncomplete());
        }

        await using var engineB = FileArticleStorageEngine.Open(
            dir.Options,
            articleCache: new ArticleMemoryCache(4L * 1024 * 1024));
        Assert.Equal(0, engineB.ArticleCache.Count);
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, engineB.ArticleCache.Count);
    }

    [Fact]
    public async Task Write_W_DurableSurvivesCacheLoss()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ww@cache.test>");
        await using (var engineA = FileArticleStorageEngine.Open(
                         dir.Options,
                         articleCache: new ArticleMemoryCache(4L * 1024 * 1024)))
        {
            await AcceptAndDrainAsync(engineA, record);
            Assert.Equal(1, engineA.ArticleCache.Count);
        }

        await using var engineB = FileArticleStorageEngine.Open(
            dir.Options,
            articleCache: new ArticleMemoryCache(4L * 1024 * 1024));
        Assert.Equal(0, engineB.ArticleCache.Count);
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Write_XY_IncompleteRecovery_DoesNotExposeUncommittedViaCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<wxy@cache.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(0, cache.Count);
        Assert.False(cache.Inner.TryGet(record.ArtId, out _));

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(1, cache.Count); // populated only after IndexCommitted
        Assert.True(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Write_Z_ConcurrentAccepts_CorrectCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(8L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var records = Enumerable.Range(0, 8)
            .Select(i => CreateRecord($"<wz-{i}@cache.test>", $"z{i}\r\n"))
            .ToArray();
        await Task.WhenAll(records.Select(r => engine.AcceptAsync(r, CancellationToken.None)));
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(8, cache.Count);
        foreach (var record in records)
        {
            Assert.True(cache.TryGet(record.ArtId, out var cached));
            Assert.True(cached.ArtData.Span.SequenceEqual(record.ArtData.Span));
        }
    }

    [Fact]
    public async Task Write_ABC_CachePopulation_DoesNotMutateDurableArtifacts()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<wabc@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        var journalLen = engine.Journal.JournalPhysicalBytes;
        var segmentLen = engine.Segments.GetActiveSizeBytes();
        var indexWrites = engine.Index.DurableWriteCount;

        Assert.True(cache.TryGet(record.ArtId, out _));
        // Extra Put (idempotent) must not grow durable files / index.
        _ = cache.Put(record);
        Assert.Equal(journalLen, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(segmentLen, engine.Segments.GetActiveSizeBytes());
        Assert.Equal(indexWrites, engine.Index.DurableWriteCount);
    }

    [Fact]
    public async Task Write_AD_RepeatedAcceptReadCache_Cycles()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        for (var i = 0; i < 3; i++)
        {
            var record = CreateRecord($"<wad-{i}@cache.test>", $"c{i}\r\n");
            await AcceptAndDrainAsync(engine, record);
            Assert.True(engine.TryRead(record.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        }
    }

    [Fact]
    public async Task Write_HitAfterWrite_WithoutIndex_IsNotReadable()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<whit@cache.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options, articleCache: cache))
        {
            await AcceptAndDrainAsync(engineA, record);
            Assert.Equal(1, cache.Count);
        }

        foreach (var path in Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*"))
        {
            File.Delete(path);
        }

        File.Delete(Path.Combine(dir.Options.ControlDir, FileArticleIndex.IndexFileName));

        await using var engineB = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        Assert.False(engineB.TryRead(record.ArtId, out _));
        Assert.False(cache.Inner.TryGet(record.ArtId, out _));
    }

    // --- Phase 3D: eviction / invalidation coherence ---

    [Fact]
    public async Task Coherence_A_Evict_RemovesCacheEntry()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<coh-a@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(cache.Inner.TryGet(record.ArtId, out _));
        Assert.True(engine.Index.TryGet(record.ArtId, out var before));
        Assert.Equal(ArticleStorageState.Present, before.State);

        cache.ResetCounters();
        Assert.True(engine.TryEvict(record.ArtId));

        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Evicted, after.State);
        Assert.False(cache.Inner.TryGet(record.ArtId, out _));
        Assert.Equal(0, cache.Count);
        Assert.True(cache.RemoveCount >= 1);
        Assert.Equal(0, cache.PutCount);
    }

    [Fact]
    public async Task Coherence_B_Invalidate_RemovesCacheEntry()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<coh-b@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(cache.Inner.TryGet(record.ArtId, out _));

        cache.ResetCounters();
        Assert.True(engine.TryInvalidate(record.ArtId));

        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Invalid, after.State);
        Assert.False(cache.Inner.TryGet(record.ArtId, out _));
        Assert.Equal(0, cache.PutCount);
    }

    [Fact]
    public async Task Coherence_CD_EngineGet_DoesNotReturnStaleAfterDeath()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var evicted = CreateRecord("<coh-c@cache.test>");
        var invalid = CreateRecord("<coh-d@cache.test>");
        await AcceptAndDrainAsync(engine, evicted);
        await AcceptAndDrainAsync(engine, invalid);

        Assert.True(engine.TryEvict(evicted.ArtId));
        Assert.False(engine.TryRead(evicted.ArtId, out _));
        Assert.False(cache.Inner.TryGet(evicted.ArtId, out _));

        Assert.True(engine.TryInvalidate(invalid.ArtId));
        Assert.False(engine.TryRead(invalid.ArtId, out _));
        Assert.False(cache.Inner.TryGet(invalid.ArtId, out _));
    }

    [Fact]
    public async Task Coherence_E_FailedEvict_LeavesCacheIntact()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<coh-e@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.Equal(1, cache.Count);

        cache.ResetCounters();
        engine.TestFailNextLogicalDeath = true;
        Assert.False(engine.TryEvict(record.ArtId));

        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(1, cache.Count);
        Assert.Equal(0, cache.RemoveCount);
        Assert.True(cache.Inner.TryGet(record.ArtId, out var cached));
        Assert.True(cached.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Coherence_F_FailedInvalidate_LeavesCacheIntact()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<coh-f@cache.test>");
        await AcceptAndDrainAsync(engine, record);

        cache.ResetCounters();
        engine.TestFailNextLogicalDeath = true;
        Assert.False(engine.TryInvalidate(record.ArtId));

        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(1, cache.Count);
        Assert.Equal(0, cache.RemoveCount);
        Assert.True(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Coherence_G_ReAcceptAfterEvict_RepopulatesOnlyAfterDurableSuccess()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<coh-g@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.Equal(0, cache.Count);

        // Inject stale cache content while durable remains Evicted — must not be served.
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(in record));
        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.False(cache.Inner.TryGet(record.ArtId, out _));

        cache.ResetCounters();
        engine.SuspendBackgroundPersist = true;
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);
        Assert.Equal(0, cache.PutCount); // journal Accept alone — not yet IndexCommitted
        Assert.True(engine.Index.TryGet(record.ArtId, out var evicted));
        Assert.Equal(ArticleStorageState.Evicted, evicted.State);
        Assert.True(engine.TryRead(record.ArtId, out var journalRead));
        Assert.True(journalRead.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.False(cache.Inner.TryGet(record.ArtId, out _));

        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(cache.PutCount >= 1);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.True(cache.Inner.TryGet(record.ArtId, out var cached));
        Assert.True(cached.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Coherence_H_Invalid_CannotBypassViaCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<coh-h@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.TryInvalidate(record.ArtId));
        Assert.Equal(0, cache.Count);

        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(in record));
        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.False(cache.Inner.TryGet(record.ArtId, out _));

        // Legitimate re-Accept of Invalid → new Present (existing contract).
        await AcceptAndDrainAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Coherence_I_PresentIndexWinsOverConflictingCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var present = CreateRecord("<coh-i@cache.test>", "present-body\r\n");
        var stale = CreateRecord("<coh-i@cache.test>", "stale-body\r\n");
        Assert.Equal(present.ArtId, stale.ArtId);
        Assert.NotEqual(present.ArtHash, stale.ArtHash);

        await AcceptAndDrainAsync(engine, present);
        Assert.True(engine.TryEvict(present.ArtId));
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(in stale));

        await AcceptAndDrainAsync(engine, present);
        Assert.True(engine.Index.TryGet(present.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(present.ArtHash, meta.ArtHash);

        Assert.True(engine.TryRead(present.ArtId, out var read));
        Assert.Equal(present.ArtHash, read.Metadata.ArtHash);
        Assert.True(read.ArtData.Span.SequenceEqual(present.ArtData.Span));
        Assert.True(cache.TryGet(present.ArtId, out var cached));
        Assert.Equal(present.ArtHash, cached.ArtHash);
        Assert.True(cached.ArtData.Span.SequenceEqual(present.ArtData.Span));
    }

    [Fact]
    public async Task Coherence_RemoveThrow_DoesNotFailDurableEvict()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ThrowingRemoveCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<coh-rm@cache.test>");
        await AcceptAndDrainAsync(engine, record);
        Assert.True(cache.Inner.TryGet(record.ArtId, out _));

        Assert.True(engine.TryEvict(record.ArtId));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Evicted, meta.State);
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Coherence_EvictInvalidate_DoNotPut()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var a = CreateRecord("<coh-noput-a@cache.test>");
        var b = CreateRecord("<coh-noput-b@cache.test>");
        await AcceptAndDrainAsync(engine, a);
        await AcceptAndDrainAsync(engine, b);
        cache.ResetCounters();

        Assert.True(engine.TryEvict(a.ArtId));
        Assert.True(engine.TryInvalidate(b.ArtId));
        Assert.Equal(0, cache.PutCount);
        Assert.True(cache.RemoveCount >= 2);
    }

    [Fact]
    public async Task Coherence_ConcurrentGetAndEvict_NoStaleAfterEvictReturns()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<coh-race@cache.test>");
        await AcceptAndDrainAsync(engine, record);

        using var start = new Barrier(9);
        var readers = new Task[8];
        for (var i = 0; i < readers.Length; i++)
        {
            readers[i] = Task.Run(() =>
            {
                start.SignalAndWait();
                for (var n = 0; n < 200; n++)
                {
                    _ = engine.TryRead(record.ArtId, out _);
                }
            });
        }

        var evict = Task.Run(() =>
        {
            start.SignalAndWait();
            Assert.True(engine.TryEvict(record.ArtId));
        });

        await Task.WhenAll(readers.Append(evict));

        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Evicted, meta.State);
        Assert.False(cache.Inner.TryGet(record.ArtId, out _));
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Coherence_ConcurrentGetAndInvalidate_NoStaleAfterInvalidateReturns()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<coh-race-inv@cache.test>");
        await AcceptAndDrainAsync(engine, record);

        using var start = new Barrier(9);
        var readers = new Task[8];
        for (var i = 0; i < readers.Length; i++)
        {
            readers[i] = Task.Run(() =>
            {
                start.SignalAndWait();
                for (var n = 0; n < 200; n++)
                {
                    _ = engine.TryRead(record.ArtId, out _);
                }
            });
        }

        var invalidate = Task.Run(() =>
        {
            start.SignalAndWait();
            Assert.True(engine.TryInvalidate(record.ArtId));
        });

        await Task.WhenAll(readers.Append(invalidate));

        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Invalid, meta.State);
        Assert.False(cache.Inner.TryGet(record.ArtId, out _));
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    private static async Task AcceptAndDrainAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
    }

    private static void CorruptSegmentPayload(string segmentDir, StoredArticleLocation location)
    {
        var path = Directory.EnumerateFiles(segmentDir, "seg-*").Single();
        var bytes = File.ReadAllBytes(path);
        bytes[(int)location.Offset + SegmentRecordCodec.FixedHeaderLength] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
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
        _ = builder.Append("Subject: cache-integration\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-engine-cache-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Records TryGet/Put/Remove traffic around an inner <see cref="IArticleMemoryCache"/>.</summary>
    private sealed class RecordingArticleMemoryCache : IArticleMemoryCache
    {
        private long _tryGet;
        private long _hit;
        private long _put;
        private long _remove;

        public RecordingArticleMemoryCache(IArticleMemoryCache inner)
        {
            Inner = inner;
        }

        public IArticleMemoryCache Inner { get; }

        public long MaxBytes => Inner.MaxBytes;

        public long CurrentBytes => Inner.CurrentBytes;

        public int Count => Inner.Count;

        public int TryGetCount => (int)Interlocked.Read(ref _tryGet);

        public int HitCount => (int)Interlocked.Read(ref _hit);

        public int PutCount => (int)Interlocked.Read(ref _put);

        public int RemoveCount => (int)Interlocked.Read(ref _remove);

        public ArticleMemoryCachePutOutcome LastPutOutcome { get; private set; }

        public bool TryGet(ArticleId artId, out ArticleRecord record)
        {
            _ = Interlocked.Increment(ref _tryGet);
            if (Inner.TryGet(artId, out record))
            {
                _ = Interlocked.Increment(ref _hit);
                return true;
            }

            return false;
        }

        public ArticleMemoryCachePutOutcome Put(in ArticleRecord record)
        {
            _ = Interlocked.Increment(ref _put);
            LastPutOutcome = Inner.Put(in record);
            return LastPutOutcome;
        }

        public bool Remove(ArticleId artId)
        {
            _ = Interlocked.Increment(ref _remove);
            return Inner.Remove(artId);
        }

        public void Clear() => Inner.Clear();

        public void ResetCounters()
        {
            Interlocked.Exchange(ref _tryGet, 0);
            Interlocked.Exchange(ref _hit, 0);
            Interlocked.Exchange(ref _put, 0);
            Interlocked.Exchange(ref _remove, 0);
        }
    }

    private sealed class RejectAllPutsCache : IArticleMemoryCache
    {
        public long MaxBytes => 1024 * 1024;

        public long CurrentBytes => 0;

        public int Count => 0;

        public bool TryGet(ArticleId artId, out ArticleRecord record)
        {
            record = default;
            return false;
        }

        public ArticleMemoryCachePutOutcome Put(in ArticleRecord record) =>
            ArticleMemoryCachePutOutcome.RejectedInvalid;

        public bool Remove(ArticleId artId) => false;

        public void Clear()
        {
        }
    }

    /// <summary>Remove throws; Clear succeeds — exercises best-effort salvage after durable death.</summary>
    private sealed class ThrowingRemoveCache : IArticleMemoryCache
    {
        public ThrowingRemoveCache(IArticleMemoryCache inner)
        {
            Inner = inner;
        }

        public IArticleMemoryCache Inner { get; }

        public long MaxBytes => Inner.MaxBytes;

        public long CurrentBytes => Inner.CurrentBytes;

        public int Count => Inner.Count;

        public bool TryGet(ArticleId artId, out ArticleRecord record) => Inner.TryGet(artId, out record);

        public ArticleMemoryCachePutOutcome Put(in ArticleRecord record) => Inner.Put(in record);

        public bool Remove(ArticleId artId) =>
            throw new InvalidOperationException("test: Remove must not fail durable death.");

        public void Clear() => Inner.Clear();
    }
}
