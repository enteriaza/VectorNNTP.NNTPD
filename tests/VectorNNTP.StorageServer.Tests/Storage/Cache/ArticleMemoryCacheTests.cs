using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;

namespace VectorNNTP.StorageServer.Tests.Storage.Cache;

/// <summary>Phase 3A in-memory LRU article-cache tests.</summary>
public sealed class ArticleMemoryCacheTests
{
    [Fact]
    public void A_Empty_Misses()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        Assert.False(cache.TryGet(CreateRecord("<a@example.test>").ArtId, out _));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
    }

    [Fact]
    public void B_PutGet_ReturnsArticle()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var record = CreateRecord("<b@example.test>");
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(record));
        Assert.True(cache.TryGet(record.ArtId, out var got));
        Assert.Equal(record.ArtId, got.ArtId);
        Assert.Equal(record.ArtHash, got.ArtHash);
        Assert.Equal(record.ArtSize, got.ArtSize);
        Assert.True(got.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public void C_Get_UpdatesLruRecency()
    {
        var a = CreateRecord("<c-a@example.test>", "aa\r\n");
        var b = CreateRecord("<c-b@example.test>", "bb\r\n");
        var cache = new ArticleMemoryCache(maxBytes: a.ArtSize + b.ArtSize);
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(a));
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(b));
        Assert.True(cache.TryGet(a.ArtId, out _)); // a becomes MRU; b is LRU
        var c = CreateRecord("<c-c@example.test>", "cc\r\n");
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(c));
        Assert.False(cache.TryGet(b.ArtId, out _));
        Assert.True(cache.TryGet(a.ArtId, out _));
        Assert.True(cache.TryGet(c.ArtId, out _));
    }

    [Fact]
    public void D_Put_UpdatesLruRecency()
    {
        var a = CreateRecord("<d-a@example.test>", "aa\r\n");
        var b = CreateRecord("<d-b@example.test>", "bb\r\n");
        var cache = new ArticleMemoryCache(maxBytes: a.ArtSize + b.ArtSize);
        _ = cache.Put(a);
        _ = cache.Put(b);
        Assert.Equal(ArticleMemoryCachePutOutcome.IdempotentNoOp, cache.Put(a)); // a MRU
        var c = CreateRecord("<d-c@example.test>", "cc\r\n");
        _ = cache.Put(c);
        Assert.False(cache.TryGet(b.ArtId, out _));
        Assert.True(cache.TryGet(a.ArtId, out _));
    }

    [Fact]
    public void E_Remove_RemovesEntry()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var record = CreateRecord("<e@example.test>");
        _ = cache.Put(record);
        Assert.True(cache.Remove(record.ArtId));
        Assert.False(cache.TryGet(record.ArtId, out _));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
    }

    [Fact]
    public void F_Clear_Empties()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        _ = cache.Put(CreateRecord("<f1@example.test>", "1\r\n"));
        _ = cache.Put(CreateRecord("<f2@example.test>", "2\r\n"));
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
    }

    [Fact]
    public void G_CurrentBytes_Correct()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var a = CreateRecord("<g-a@example.test>", "one\r\n");
        var b = CreateRecord("<g-b@example.test>", "twoo\r\n");
        _ = cache.Put(a);
        Assert.Equal(a.ArtSize, cache.CurrentBytes);
        _ = cache.Put(b);
        Assert.Equal(a.ArtSize + b.ArtSize, cache.CurrentBytes);
    }

    [Fact]
    public void H_MaxBytes_Enforced()
    {
        var a = CreateRecord("<h-a@example.test>", "aaaa\r\n");
        var cache = new ArticleMemoryCache(maxBytes: a.ArtSize);
        _ = cache.Put(a);
        var b = CreateRecord("<h-b@example.test>", "bbbb\r\n");
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(b));
        Assert.False(cache.TryGet(a.ArtId, out _));
        Assert.True(cache.TryGet(b.ArtId, out _));
        Assert.True(cache.CurrentBytes <= cache.MaxBytes);
    }

    [Fact]
    public void I_LeastRecentlyUsed_EvictedFirst()
    {
        var a = CreateRecord("<i-a@example.test>", "a\r\n");
        var b = CreateRecord("<i-b@example.test>", "b\r\n");
        var c = CreateRecord("<i-c@example.test>", "c\r\n");
        var cache = new ArticleMemoryCache(maxBytes: a.ArtSize + b.ArtSize);
        _ = cache.Put(a);
        _ = cache.Put(b);
        _ = cache.Put(c);
        Assert.False(cache.TryGet(a.ArtId, out _));
        Assert.True(cache.TryGet(b.ArtId, out _));
        Assert.True(cache.TryGet(c.ArtId, out _));
    }

    [Fact]
    public void J_AccessOlder_ChangesEvictionOrder()
    {
        var a = CreateRecord("<j-a@example.test>", "a\r\n");
        var b = CreateRecord("<j-b@example.test>", "b\r\n");
        var c = CreateRecord("<j-c@example.test>", "c\r\n");
        var cache = new ArticleMemoryCache(maxBytes: a.ArtSize + b.ArtSize);
        _ = cache.Put(a);
        _ = cache.Put(b);
        Assert.True(cache.TryGet(a.ArtId, out _));
        _ = cache.Put(c);
        Assert.True(cache.TryGet(a.ArtId, out _));
        Assert.False(cache.TryGet(b.ArtId, out _));
        Assert.True(cache.TryGet(c.ArtId, out _));
    }

    [Fact]
    public void K_MultipleEvictions_UntilUnderMax()
    {
        var a = CreateRecord("<k-a@example.test>", "a\r\n");
        var b = CreateRecord("<k-b@example.test>", "b\r\n");
        var c = CreateRecord("<k-c@example.test>", "c\r\n");
        var big = CreateRecord("<k-big@example.test>", "BIGBODY\r\n");
        // Capacity holds a+b+c but not a+b+c+big → inserting big must evict enough.
        var cache = new ArticleMemoryCache(maxBytes: a.ArtSize + b.ArtSize + c.ArtSize);
        _ = cache.Put(a);
        _ = cache.Put(b);
        _ = cache.Put(c);
        Assert.Equal(3, cache.Count);
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(big));
        Assert.True(cache.TryGet(big.ArtId, out _));
        Assert.True(cache.CurrentBytes <= cache.MaxBytes);
        Assert.True(cache.Count is >= 1 and < 4);
    }

    [Fact]
    public void L_Oversized_NotCached()
    {
        var record = CreateRecord("<l@example.test>", "oversized-body\r\n");
        var cache = new ArticleMemoryCache(maxBytes: record.ArtSize - 1);
        Assert.Equal(ArticleMemoryCachePutOutcome.RejectedOversized, cache.Put(record));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
    }

    [Fact]
    public void M_MaxBytesZero_Disables()
    {
        var cache = new ArticleMemoryCache(maxBytes: 0);
        var record = CreateRecord("<m@example.test>");
        Assert.Equal(ArticleMemoryCachePutOutcome.RejectedDisabled, cache.Put(record));
        Assert.False(cache.TryGet(record.ArtId, out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void N_DuplicateIdentical_Idempotent()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var record = CreateRecord("<n@example.test>");
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(record));
        Assert.Equal(ArticleMemoryCachePutOutcome.IdempotentNoOp, cache.Put(record));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void O_DuplicateIdentical_DoesNotDoubleBytes()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var record = CreateRecord("<o@example.test>");
        _ = cache.Put(record);
        var bytes = cache.CurrentBytes;
        _ = cache.Put(record);
        Assert.Equal(bytes, cache.CurrentBytes);
    }

    [Fact]
    public void P_ConflictingSameId_Rejected()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var first = CreateRecord("<p@example.test>", "body-a\r\n");
        var conflict = CreateRecord("<p@example.test>", "body-b\r\n");
        _ = cache.Put(first);
        Assert.Equal(ArticleMemoryCachePutOutcome.RejectedConflict, cache.Put(conflict));
        Assert.True(cache.TryGet(first.ArtId, out var got));
        Assert.True(got.ArtData.Span.SequenceEqual(first.ArtData.Span));
    }

    [Fact]
    public void Q_IntegrityValidation_RejectsInvalid()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        // Non-canonical: default struct
        Assert.Equal(ArticleMemoryCachePutOutcome.RejectedInvalid, cache.Put(default));
    }

    [Fact]
    public async Task R_Concurrent_GetPutRemove_Consistent()
    {
        var cache = new ArticleMemoryCache(maxBytes: 4L * 1024 * 1024);
        var records = Enumerable.Range(0, 32)
            .Select(i => CreateRecord($"<r-{i}@example.test>", $"body-{i}\r\n"))
            .ToArray();

        await Task.WhenAll(records.Select(async r =>
        {
            for (var i = 0; i < 20; i++)
            {
                _ = cache.Put(r);
                _ = cache.TryGet(r.ArtId, out _);
                if (i % 7 == 0)
                {
                    _ = cache.Remove(r.ArtId);
                }

                await Task.Yield();
            }
        }));

        Assert.True(cache.CurrentBytes >= 0);
        Assert.True(cache.CurrentBytes <= cache.MaxBytes);
        Assert.True(cache.Count >= 0);
        Assert.True(cache.Count <= records.Length);
    }

    [Fact]
    public async Task S_Count_ConsistentUnderConcurrency()
    {
        var cache = new ArticleMemoryCache(maxBytes: 8L * 1024 * 1024);
        var records = Enumerable.Range(0, 16)
            .Select(i => CreateRecord($"<s-{i}@example.test>", $"x{i}\r\n"))
            .ToArray();
        await Task.WhenAll(records.Select(r => Task.Run(() => cache.Put(r))));
        Assert.Equal(records.Length, cache.Count);
        Assert.Equal(records.Sum(static r => (long)r.ArtSize), cache.CurrentBytes);
    }

    [Fact]
    public void T_CurrentBytes_NeverNegative()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var record = CreateRecord("<t@example.test>");
        _ = cache.Put(record);
        _ = cache.Remove(record.ArtId);
        _ = cache.Remove(record.ArtId);
        Assert.Equal(0, cache.CurrentBytes);
    }

    [Fact]
    public void U_CurrentBytes_NeverExceedsMaxAfterPut()
    {
        var a = CreateRecord("<u-a@example.test>", "aaa\r\n");
        var b = CreateRecord("<u-b@example.test>", "bbb\r\n");
        var c = CreateRecord("<u-c@example.test>", "ccc\r\n");
        var cache = new ArticleMemoryCache(maxBytes: a.ArtSize + b.ArtSize);
        _ = cache.Put(a);
        _ = cache.Put(b);
        _ = cache.Put(c);
        Assert.True(cache.CurrentBytes <= cache.MaxBytes);
    }

    [Fact]
    public void V_Clear_ResetsCountAndBytes()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        _ = cache.Put(CreateRecord("<v@example.test>"));
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
    }

    [Fact]
    public void W_RemoveThenPut_Works()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var record = CreateRecord("<w@example.test>");
        _ = cache.Put(record);
        Assert.True(cache.Remove(record.ArtId));
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(record));
        Assert.True(cache.TryGet(record.ArtId, out _));
    }

    [Fact]
    public void X_EvictionThenReinsert_Works()
    {
        var a = CreateRecord("<x-a@example.test>", "a\r\n");
        var b = CreateRecord("<x-b@example.test>", "b\r\n");
        var cache = new ArticleMemoryCache(maxBytes: a.ArtSize);
        _ = cache.Put(a);
        _ = cache.Put(b);
        Assert.False(cache.TryGet(a.ArtId, out _));
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(a));
        Assert.True(cache.TryGet(a.ArtId, out _));
        Assert.False(cache.TryGet(b.ArtId, out _));
    }

    [Fact]
    public void Y_NewInstance_Empty()
    {
        var record = CreateRecord("<y@example.test>");
        var cacheA = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        _ = cacheA.Put(record);
        var cacheB = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        Assert.False(cacheB.TryGet(record.ArtId, out _));
        Assert.Equal(0, cacheB.Count);
    }

    [Fact]
    public void Z_MaxSizeArticle_CachedWhenCapacityAllows()
    {
        // Large multi-line body under parser line limits; capacity exactly ArtSize.
        var bodyBuilder = new StringBuilder();
        for (var i = 0; i < 256; i++)
        {
            _ = bodyBuilder.Append('x', 200).Append("\r\n");
        }

        var record = CreateRecord("<z@example.test>", bodyBuilder.ToString());
        Assert.True(record.ArtSize > 40_000);
        var cache = new ArticleMemoryCache(maxBytes: record.ArtSize);
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(record));
        Assert.True(cache.TryGet(record.ArtId, out var got));
        Assert.Equal(record.ArtSize, got.ArtSize);
    }

    [Fact]
    public void AA_EvictsEnoughToMakeRoom()
    {
        var a = CreateRecord("<aa-a@example.test>", "1\r\n");
        var b = CreateRecord("<aa-b@example.test>", "2\r\n");
        var c = CreateRecord("<aa-c@example.test>", "3333\r\n");
        var cache = new ArticleMemoryCache(maxBytes: a.ArtSize + b.ArtSize);
        // If c does not fit in a+b capacity, use capacity = c.ArtSize
        if (c.ArtSize > a.ArtSize + b.ArtSize)
        {
            cache = new ArticleMemoryCache(maxBytes: c.ArtSize);
        }

        _ = cache.Put(a);
        _ = cache.Put(b);
        var before = cache.Count;
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(c));
        Assert.True(cache.TryGet(c.ArtId, out _));
        Assert.True(cache.Count <= before);
        Assert.True(cache.CurrentBytes <= cache.MaxBytes);
    }

    [Fact]
    public void AB_MissingGet_DoesNotMutateAccounting()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var record = CreateRecord("<ab@example.test>");
        _ = cache.Put(record);
        var count = cache.Count;
        var bytes = cache.CurrentBytes;
        Assert.False(cache.TryGet(CreateRecord("<ab-miss@example.test>").ArtId, out _));
        Assert.Equal(count, cache.Count);
        Assert.Equal(bytes, cache.CurrentBytes);
    }

    [Fact]
    public void AC_MissingRemove_DoesNotMutateAccounting()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var record = CreateRecord("<ac@example.test>");
        _ = cache.Put(record);
        var count = cache.Count;
        var bytes = cache.CurrentBytes;
        Assert.False(cache.Remove(CreateRecord("<ac-miss@example.test>").ArtId));
        Assert.Equal(count, cache.Count);
        Assert.Equal(bytes, cache.CurrentBytes);
    }

    [Fact]
    public void AD_IdenticalReplace_DoesNotChangeBytes()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var record = CreateRecord("<ad@example.test>");
        _ = cache.Put(record);
        var bytes = cache.CurrentBytes;
        _ = cache.Put(record);
        Assert.Equal(bytes, cache.CurrentBytes);
    }

    [Fact]
    public void AE_Lru_AfterRepeatedGets()
    {
        var a = CreateRecord("<ae-a@example.test>", "a\r\n");
        var b = CreateRecord("<ae-b@example.test>", "b\r\n");
        var c = CreateRecord("<ae-c@example.test>", "c\r\n");
        var cache = new ArticleMemoryCache(maxBytes: a.ArtSize + b.ArtSize);
        _ = cache.Put(a);
        _ = cache.Put(b);
        for (var i = 0; i < 5; i++)
        {
            Assert.True(cache.TryGet(a.ArtId, out _));
        }

        _ = cache.Put(c);
        Assert.True(cache.TryGet(a.ArtId, out _));
        Assert.False(cache.TryGet(b.ArtId, out _));
        Assert.True(cache.TryGet(c.ArtId, out _));
    }

    [Fact]
    public void Options_MaxBytesNegative_ThrowsOnConstruct()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ArticleMemoryCache(maxBytes: -1));
    }

    [Fact]
    public void Options_BindableDefault_IsDisabled()
    {
        var options = new ArticleMemoryCacheOptions();
        Assert.Equal(0, options.MaxBytes);
        var cache = new ArticleMemoryCache(options);
        Assert.Equal(ArticleMemoryCachePutOutcome.RejectedDisabled, cache.Put(CreateRecord("<opt@example.test>")));
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
        _ = builder.Append("Subject: cache\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }
}
