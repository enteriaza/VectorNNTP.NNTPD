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

/// <summary>Phase 3B: RAM cache integration on the durable FileArticleStorageEngine read path.</summary>
public sealed class FileArticleStorageEngineCacheTests
{
    [Fact]
    public async Task A_CacheMiss_ReadsDurable()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<a@cache.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, cache.TryGetCount);
        Assert.Equal(1, cache.PutCount);
    }

    [Fact]
    public async Task B_SuccessfulDurableRead_PopulatesCache()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<b@cache.test>");
        await AcceptAndDrainAsync(engine, record);

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
        Assert.True(engine.TryRead(record.ArtId, out _));
        var putsAfterFirst = cache.PutCount;

        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Equal(2, cache.TryGetCount);
        Assert.Equal(putsAfterFirst, cache.PutCount);
        Assert.Equal(1, cache.HitCount);
    }

    [Fact]
    public async Task D_CacheHit_DoesNotRequireIndexFile()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<d@cache.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options, articleCache: cache))
        {
            await AcceptAndDrainAsync(engineA, record);
            Assert.True(engineA.TryRead(record.ArtId, out _));
        }

        // Destroy durable index; shared cache still holds the article.
        File.Delete(Path.Combine(dir.Options.ControlDir, FileArticleIndex.IndexFileName));

        await using var engineB = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(cache.HitCount >= 1);
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
            Assert.True(engineA.TryRead(record.ArtId, out _));
        }

        foreach (var path in Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*"))
        {
            File.Delete(path);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
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
        Assert.Equal(1, cache.PutCount);
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
        Assert.Equal(1, cache.PutCount);
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

        await using var engineB = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        Assert.False(engineB.TryRead(record.ArtId, out _));
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
    public async Task R_Accept_Unchanged()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<r@cache.test>");
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);
        Assert.Equal(0, cache.PutCount); // no write-through
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(0, cache.PutCount);
    }

    [Fact]
    public async Task STUV_Cache_DoesNotParticipateInJournalStages()
    {
        using var dir = TempStorageDir.Create();
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<stuv@cache.test>");
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(0, cache.PutCount);
        Assert.Single(engine.Journal.EnumerateIncomplete());

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(0, cache.PutCount); // recovery does not populate cache
        Assert.True(engine.Index.TryGet(record.ArtId, out _));
        Assert.Equal(accept.Sequence, accept.Sequence);
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
        Assert.True(engine.TryRead(record.ArtId, out _)); // populate

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
}
