using System.Runtime.InteropServices;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// After durable IndexCommitted, the cache adopts the journal Accept buffer.
/// Segment cache misses keep a separate copy.
/// </summary>
public sealed class JournalPayloadAdoptionTests
{
    [Fact]
    public async Task IndexCommitted_adopts_the_journal_payload_array()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(maxBytes: 4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<adopt-journal@example.test>");
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);

        var accept = Assert.Single(engine.Journal.EnumerateIncomplete()).Accept;
        var journalPayload = Payload(accept.ArtData);
        engine.TestEnqueueIncompleteWork();
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(0, accept.ArtData.Length);
        Assert.False(Exposes(accept.ArtData, journalPayload));
        Assert.Throws<InvalidOperationException>(accept.DetachPayload);
        Assert.True(cache.TryGet(record.ArtId, out var cached));
        Assert.Same(journalPayload, Payload(cached));
        Assert.Equal(record.ArtId, cached.ArtId);
        Assert.Equal(record.ArtHash, cached.ArtHash);
        Assert.Equal(record.ArtSize, cached.ArtSize);
        Assert.Equal(0, cached.ArtLines);
        Assert.Equal(ArticleFieldTable.Locate(journalPayload, NntpArticleHeaderName.Date), cached.Fields);
        Assert.True(cached.Fields.MessageId.Slice(cached.ArtData.Span).SequenceEqual(
            record.Fields.MessageId.Slice(record.ArtData.Span)));
        Assert.True(cached.Fields.Date.Slice(cached.ArtData.Span).SequenceEqual(
            record.Fields.Date.Slice(record.ArtData.Span)));
        Assert.True(cached.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Segment_cache_miss_copies_into_a_separate_buffer()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(maxBytes: 4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<adopt-segment@example.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        var journalPayload = Payload(Assert.Single(engine.Journal.EnumerateIncomplete()).Accept.ArtData);
        engine.TestEnqueueIncompleteWork();
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.True(cache.TryGet(record.ArtId, out var committed));
        Assert.Same(journalPayload, Payload(committed));
        Assert.True(cache.Remove(record.ArtId));
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(cache.TryGet(record.ArtId, out var repopulated));

        var readPayload = Payload(read.ArtData);
        var cachePayload = Payload(repopulated);
        Assert.NotSame(journalPayload, readPayload);
        Assert.NotSame(journalPayload, cachePayload);
        Assert.NotSame(readPayload, cachePayload);
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(repopulated.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public void Put_still_clones_the_caller_buffer()
    {
        var cache = new ArticleMemoryCache(maxBytes: 1024 * 1024);
        var record = CreateRecord("<adopt-put@example.test>");
        var original = record.ArtData.ToArray();

        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(in record));
        Payload(record)[0] ^= 0xFF;

        Assert.True(cache.TryGet(record.ArtId, out var cached));
        Assert.True(cached.ArtData.Span.SequenceEqual(original));
        Assert.NotSame(Payload(record), Payload(cached));
    }

    [Fact]
    public async Task Rejected_adoption_drops_the_detached_buffer()
    {
        var record = CreateRecord("<adopt-reject@example.test>");
        await AssertRejected(record, new ArticleMemoryCache(maxBytes: 0), expectStored: false);
        await AssertRejected(record, new ArticleMemoryCache(record.ArtSize - 1), expectStored: false);
        await AssertRejected(record, Seeded(record, "other-body\r\n"), expectStored: true);
        await AssertRejected(record, Seeded(record, "line1\r\nline2\r\n"), expectStored: true, identical: true);
    }

    [Fact]
    public async Task IndexCommitted_flush_failure_keeps_the_payload_until_a_later_success()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(maxBytes: 4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engine.SuspendBackgroundPersist = true;
        engine.TestPersistRetryDelay = TimeSpan.FromSeconds(2);
        var record = CreateRecord("<adopt-flush@example.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        var accept = Assert.Single(engine.Journal.EnumerateIncomplete()).Accept;
        var journalPayload = Payload(accept.ArtData);

        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flushes = 0;
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            // SegmentIdFence, then PhysicalWritten, then IndexCommitted.
            flushes++;
            if (flushes < 3)
            {
                return;
            }

            engine.Journal.TestBeforeDurableFlush = null;
            failed.TrySetResult();
            throw new IOException("index committed flush failed");
        };

        engine.TestEnqueueIncompleteWork();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var during = Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Same(accept, during.Accept);
        Assert.NotNull(during.PhysicalWritten);
        Assert.Same(journalPayload, Payload(accept.ArtData));
        var refused = Assert.Throws<InvalidOperationException>(accept.DetachPayload);
        Assert.Contains("only after durable IndexCommitted", refused.Message, StringComparison.Ordinal);
        Assert.Same(journalPayload, Payload(accept.ArtData));
        Assert.False(cache.TryGet(record.ArtId, out _));

        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(0, accept.ArtData.Length);
        Assert.False(Exposes(accept.ArtData, journalPayload));
        Assert.True(cache.TryGet(record.ArtId, out var cached));
        Assert.Same(journalPayload, Payload(cached));
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(journalPayload));
    }

    [Fact]
    public async Task Restart_reads_the_committed_article_from_storage()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<adopt-restart@example.test>");
        var firstCache = new ArticleMemoryCache(maxBytes: 4L * 1024 * 1024);
        await using (var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: firstCache))
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.Empty(engine.Journal.EnumerateIncomplete());
            Assert.True(firstCache.TryGet(record.ArtId, out var cached));
            Assert.True(cached.ArtData.Span.SequenceEqual(record.ArtData.Span));
        }

        var reopenedCache = new ArticleMemoryCache(maxBytes: 4L * 1024 * 1024);
        await using var reopened = FileArticleStorageEngine.Open(dir.Options, articleCache: reopenedCache);
        Assert.Empty(reopened.Journal.EnumerateIncomplete());
        Assert.Equal(0, reopenedCache.Count);
        Assert.True(reopened.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(reopenedCache.TryGet(record.ArtId, out var repopulated));
        Assert.NotSame(Payload(read.ArtData), Payload(repopulated));
    }

    [Fact]
    public async Task Recovery_index_committed_adopts_the_replayed_journal_buffer()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<adopt-recover@example.test>");
        var firstCache = new ArticleMemoryCache(maxBytes: 4L * 1024 * 1024);
        await using (var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: firstCache))
        {
            engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
            engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeIndexCommitted;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await WaitUntilAsync(() =>
            {
                var incomplete = engine.Journal.EnumerateIncomplete();
                return incomplete.Count == 1
                    && incomplete[0].PhysicalWritten is not null
                    && engine.PersistRetryScheduledCount >= 1;
            });

            var stalled = Assert.Single(engine.Journal.EnumerateIncomplete());
            var stalledPayload = Payload(stalled.Accept.ArtData);
            var refused = Assert.Throws<InvalidOperationException>(stalled.Accept.DetachPayload);
            Assert.Contains("only after durable IndexCommitted", refused.Message, StringComparison.Ordinal);
            Assert.Same(stalledPayload, Payload(stalled.Accept.ArtData));
            Assert.False(firstCache.TryGet(record.ArtId, out _));
        }

        var cache = new ArticleMemoryCache(maxBytes: 4L * 1024 * 1024);
        await using var recovered = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var accept = Assert.Single(recovered.Journal.EnumerateIncomplete()).Accept;
        var journalPayload = Payload(accept.ArtData);
        await recovered.RecoverAsync(CancellationToken.None);

        Assert.Empty(recovered.Journal.EnumerateIncomplete());
        Assert.Equal(0, accept.ArtData.Length);
        Assert.False(Exposes(accept.ArtData, journalPayload));
        Assert.True(cache.TryGet(record.ArtId, out var cached));
        Assert.Same(journalPayload, Payload(cached));
        Assert.Equal(ArticleFieldTable.Locate(journalPayload, NntpArticleHeaderName.Date), cached.Fields);
        Assert.True(recovered.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    private static async Task AssertRejected(
        ArticleRecord record,
        ArticleMemoryCache cache,
        bool expectStored,
        bool identical = false)
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engine.SuspendBackgroundPersist = true;
        byte[]? planted = null;
        if (expectStored)
        {
            Assert.True(cache.TryGet(record.ArtId, out var existing));
            planted = Payload(existing);
        }

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        var accept = Assert.Single(engine.Journal.EnumerateIncomplete()).Accept;
        var journalPayload = Payload(accept.ArtData);
        Assert.NotSame(journalPayload, planted);
        engine.TestEnqueueIncompleteWork();
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(0, accept.ArtData.Length);
        Assert.False(Exposes(accept.ArtData, journalPayload));
        Assert.Throws<InvalidOperationException>(accept.DetachPayload);
        if (!expectStored)
        {
            Assert.False(cache.TryGet(record.ArtId, out _));
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.CurrentBytes);
            return;
        }

        Assert.True(cache.TryGet(record.ArtId, out var cached));
        Assert.Same(planted, Payload(cached));
        Assert.NotSame(journalPayload, Payload(cached));
        if (identical)
        {
            Assert.True(cached.ArtData.Span.SequenceEqual(journalPayload));
        }
        else
        {
            Assert.False(cached.ArtData.Span.SequenceEqual(journalPayload));
        }
    }

    private static ArticleMemoryCache Seeded(ArticleRecord record, string body)
    {
        var cache = new ArticleMemoryCache(maxBytes: 4L * 1024 * 1024);
        var seeded = CreateRecord("<adopt-reject@example.test>", body);
        Assert.Equal(record.ArtId, seeded.ArtId);
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(in seeded));
        return cache;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > 10_000)
            {
                throw new TimeoutException("Timed out waiting for the persist fault to leave PhysicalWritten durable.");
            }

            await Task.Delay(10);
        }
    }

    private static bool Exposes(ReadOnlyMemory<byte> artData, byte[] payload) =>
        MemoryMarshal.TryGetArray(artData, out ArraySegment<byte> segment) && ReferenceEquals(segment.Array, payload);

    private static byte[] Payload(ReadOnlyMemory<byte> artData)
    {
        Assert.True(MemoryMarshal.TryGetArray(artData, out ArraySegment<byte> segment));
        Assert.NotNull(segment.Array);
        Assert.Equal(0, segment.Offset);
        Assert.Equal(artData.Length, segment.Count);
        return segment.Array;
    }

    private static byte[] Payload(in ArticleRecord record) => Payload(record.ArtData);

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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-journal-payload-" + Guid.NewGuid().ToString("N"));
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
