using System.Runtime.InteropServices;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Tests.Storage.Cache;

/// <summary>
/// Ownership split between <see cref="ArticleMemoryCache.Put"/> and <see cref="ArticleMemoryCache.AdoptOwned"/>.
/// </summary>
public sealed class ArticleMemoryCacheAdoptTests
{
    [Fact]
    public void Adopt_stores_the_supplied_buffer()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var record = CreateRecord("<adopt-same@example.test>");
        var payload = Payload(record);

        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.AdoptOwned(in record));
        Assert.True(cache.TryGet(record.ArtId, out var cached));
        Assert.Same(payload, Payload(cached));
        Assert.True(cached.ArtData.Span.SequenceEqual(payload));
    }

    [Fact]
    public void Put_clones_so_caller_mutation_does_not_change_the_cache()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var record = CreateRecord("<put-clone@example.test>");
        var original = record.ArtData.ToArray();

        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(in record));
        Payload(record)[0] ^= 0xFF;

        Assert.True(cache.TryGet(record.ArtId, out var cached));
        Assert.True(cached.ArtData.Span.SequenceEqual(original));
        Assert.NotSame(Payload(record), Payload(cached));
    }

    [Fact]
    public void Put_and_adopt_have_different_ownership()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var putRecord = CreateRecord("<own-put@example.test>", "put-body\r\n");
        var adoptRecord = CreateRecord("<own-adopt@example.test>", "adopt-body\r\n");
        var putOriginal = putRecord.ArtData.ToArray();
        var adoptPayload = Payload(adoptRecord);

        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(in putRecord));
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.AdoptOwned(in adoptRecord));
        Payload(putRecord)[0] ^= 0xFF;
        adoptPayload[0] ^= 0xFF;

        Assert.True(cache.TryGet(putRecord.ArtId, out var putCached));
        Assert.True(cache.TryGet(adoptRecord.ArtId, out var adoptCached));
        Assert.True(putCached.ArtData.Span.SequenceEqual(putOriginal));
        Assert.Same(adoptPayload, Payload(adoptCached));
        Assert.Equal(adoptPayload[0], adoptCached.ArtData.Span[0]);
    }

    [Fact]
    public void Adopted_entry_remains_readable_after_the_creating_method_returns()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var payload = AdoptAndReturnPayload(cache, out var artId);

        Assert.True(cache.TryGet(artId, out var cached));
        Assert.Same(payload, Payload(cached));
        Assert.True(cached.ArtData.Span.SequenceEqual(payload));
    }

    [Fact]
    public void Adopt_does_not_allocate_a_second_payload()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var body = new StringBuilder();
        for (var i = 0; i < 400; i++)
        {
            _ = body.Append("yyyy\r\n");
        }

        var record = CreateRecord("<adopt-alloc@example.test>", body.ToString());
        var payload = Payload(record);
        var before = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.AdoptOwned(in record));

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(cache.TryGet(record.ArtId, out var cached));
        Assert.Same(payload, Payload(cached));
        Assert.True(allocated < record.ArtSize);
    }

    [Fact]
    public void Rejected_adopt_does_not_retain_the_candidate()
    {
        var record = CreateRecord("<adopt-reject@example.test>");
        var disabled = new ArticleMemoryCache(maxBytes: 0);
        Assert.Equal(ArticleMemoryCachePutOutcome.RejectedDisabled, disabled.AdoptOwned(in record));
        Assert.Equal(0, disabled.Count);
        Assert.False(disabled.TryGet(record.ArtId, out _));

        var oversized = new ArticleMemoryCache(record.ArtSize - 1);
        Assert.Equal(ArticleMemoryCachePutOutcome.RejectedOversized, oversized.AdoptOwned(in record));
        Assert.Equal(0, oversized.Count);

        var invalid = new ArticleRecord(
            record.ArtId,
            record.ArtHash ^ 1,
            record.ArtType,
            record.ArtLines,
            record.CanonicalUtc,
            ArticleParseStatus.CanonicalV1,
            Payload(record),
            record.Fields);
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        Assert.Equal(ArticleMemoryCachePutOutcome.RejectedInvalid, cache.AdoptOwned(in invalid));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
    }

    [Fact]
    public void Identical_and_conflicting_adopt_keep_the_existing_buffer()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var first = CreateRecord("<adopt-keep@example.test>", "same-body\r\n");
        var identical = CreateRecord("<adopt-keep@example.test>", "same-body\r\n");
        var conflict = CreateRecord("<adopt-keep@example.test>", "other-body\r\n");
        var firstPayload = Payload(first);
        Assert.NotSame(firstPayload, Payload(identical));

        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.AdoptOwned(in first));
        Assert.Equal(ArticleMemoryCachePutOutcome.IdempotentNoOp, cache.AdoptOwned(in identical));
        Assert.Equal(ArticleMemoryCachePutOutcome.RejectedConflict, cache.AdoptOwned(in conflict));

        Payload(identical)[0] ^= 0xFF;
        Payload(conflict)[0] ^= 0xFF;
        Assert.True(cache.TryGet(first.ArtId, out var cached));
        Assert.Same(firstPayload, Payload(cached));
        Assert.Equal(1, cache.Count);
        Assert.Equal(first.ArtSize, cache.CurrentBytes);
    }

    [Fact]
    public void Adopted_entries_evict_least_recently_used()
    {
        var a = CreateRecord("<adopt-lru-a@example.test>", "aa\r\n");
        var b = CreateRecord("<adopt-lru-b@example.test>", "bb\r\n");
        var c = CreateRecord("<adopt-lru-c@example.test>", "cc\r\n");
        var cache = new ArticleMemoryCache(maxBytes: a.ArtSize + b.ArtSize);

        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.AdoptOwned(in a));
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.AdoptOwned(in b));
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.AdoptOwned(in c));

        Assert.False(cache.TryGet(a.ArtId, out _));
        Assert.True(cache.TryGet(b.ArtId, out var cachedB));
        Assert.True(cache.TryGet(c.ArtId, out var cachedC));
        Assert.True(cachedB.ArtData.Span.SequenceEqual(b.ArtData.Span));
        Assert.True(cachedC.ArtData.Span.SequenceEqual(c.ArtData.Span));
        Assert.Same(Payload(b), Payload(cachedB));
        Assert.Same(Payload(c), Payload(cachedC));
    }

    [Fact]
    public async Task Created_cache_records_are_adopted_on_commit_and_on_read_miss()
    {
        using var dir = TempStorageDir.Create();
        var cache = new AdoptionSpy(maxBytes: 4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<adopt-callers@example.test>");

        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        var adoptDeadline = Environment.TickCount64 + 5_000;
        while (cache.Adopts < 1 && Environment.TickCount64 < adoptDeadline)
        {
            await Task.Delay(1);
        }

        Assert.Equal(1, cache.Adopts);
        Assert.Equal(0, cache.Puts);
        Assert.True(cache.TryGet(record.ArtId, out var committed));
        Assert.True(committed.ArtData.Span.SequenceEqual(record.ArtData.Span));

        Assert.True(cache.Remove(record.ArtId));
        cache.Reset();
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, cache.Adopts);
        Assert.Equal(0, cache.Puts);
        Assert.True(cache.TryGet(record.ArtId, out var repopulated));
        Assert.True(repopulated.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    private static byte[] AdoptAndReturnPayload(ArticleMemoryCache cache, out ArticleId artId)
    {
        var record = CreateRecord("<adopt-return@example.test>");
        artId = record.ArtId;
        var payload = Payload(record);
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.AdoptOwned(in record));
        return payload;
    }

    private static byte[] Payload(in ArticleRecord record)
    {
        Assert.True(MemoryMarshal.TryGetArray(record.ArtData, out ArraySegment<byte> segment));
        Assert.NotNull(segment.Array);
        Assert.Equal(0, segment.Offset);
        Assert.Equal(record.ArtSize, segment.Count);
        return segment.Array;
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
        _ = builder.Append("Subject: adopt\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class AdoptionSpy : IArticleMemoryCache, IArticleMemoryCacheAdoption
    {
        private readonly ArticleMemoryCache _inner;
        private int _puts;
        private int _adopts;

        public AdoptionSpy(long maxBytes)
        {
            _inner = new ArticleMemoryCache(maxBytes);
        }

        public int Puts => _puts;

        public int Adopts => _adopts;

        public long MaxBytes => _inner.MaxBytes;

        public long CurrentBytes => _inner.CurrentBytes;

        public int Count => _inner.Count;

        public void Reset()
        {
            _puts = 0;
            _adopts = 0;
        }

        public bool TryGet(ArticleId artId, out ArticleRecord record) => _inner.TryGet(artId, out record);

        public ArticleMemoryCachePutOutcome Put(in ArticleRecord record)
        {
            _puts++;
            return _inner.Put(in record);
        }

        public ArticleMemoryCachePutOutcome AdoptOwned(in ArticleRecord record)
        {
            _adopts++;
            return _inner.AdoptOwned(in record);
        }

        public bool Remove(ArticleId artId) => _inner.Remove(artId);

        public void Clear() => _inner.Clear();
    }

    private sealed class TempStorageDir : IDisposable
    {
        private TempStorageDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-adopt-" + Guid.NewGuid().ToString("N"));
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

        private string Root { get; }
    }
}
