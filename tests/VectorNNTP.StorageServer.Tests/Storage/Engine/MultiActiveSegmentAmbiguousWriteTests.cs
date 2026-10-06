using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Multi-writer append failures use the same pending-record and torn-tail rules as one writer.
/// A failed segment must accept a later append without a process restart.
/// </summary>
public sealed class MultiActiveSegmentAmbiguousWriteTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Partial_write_is_truncated_and_the_same_batch_appends_once(int writers)
    {
        using var dir = TempDir.Create(writers);
        using var store = FileSegmentStore.Open(dir.Options);
        var articles = Articles(writers, "partial");
        store.TestAfterWriteBeforeFlush = (stream, offset, _) =>
        {
            stream.SetLength(offset + 5);
            throw new IOException("partial-write");
        };

        var error = Assert.ThrowsAny<Exception>(() => store.AppendActiveBatch(articles));
        AssertIoFailures(error);
        Assert.Equal(0, store.FlushedHeaderConfirmCount);
        Assert.All(articles, article => Assert.Equal(0, Count(dir.SegmentDir, MessageId(article))));

        store.TestAfterWriteBeforeFlush = null;
        var receipts = store.AppendActiveBatch(articles);
        Assert.Equal(writers, receipts.Select(static receipt => receipt.Location.SegmentId.Value).Distinct().Count());
        Assert.Equal(writers, store.OpenActiveSegmentCount);
        foreach (var article in articles)
        {
            Assert.Equal(1, Count(dir.SegmentDir, MessageId(article)));
        }

        foreach (var receipt in receipts)
        {
            Assert.True(store.TryRead(receipt.Location, out var read));
            Assert.True(read.Length > 0);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Flush_failure_keeps_one_pending_record_and_retry_does_not_duplicate(int writers)
    {
        using var dir = TempDir.Create(writers);
        using var store = FileSegmentStore.Open(dir.Options);
        var articles = Articles(writers, "flush");
        store.TestBeforeDurableFlush = () => throw new IOException("segment-flush");

        var error = Assert.ThrowsAny<Exception>(() => store.AppendActiveBatch(articles));
        AssertIoFailures(error, static failure => failure is UnreconciledDurableTailException);
        Assert.Equal(0, store.DurableFlushCount);
        Assert.Equal(0, store.FlushedHeaderConfirmCount);
        foreach (var article in articles)
        {
            Assert.Equal(1, Count(dir.SegmentDir, MessageId(article)));
        }

        store.TestBeforeDurableFlush = null;
        var receipts = store.AppendActiveBatch(articles);
        Assert.Equal(writers, store.DurableFlushCount);
        Assert.Equal(writers, receipts.Select(static receipt => receipt.Location.SegmentId.Value).Distinct().Count());
        foreach (var article in articles)
        {
            Assert.Equal(1, Count(dir.SegmentDir, MessageId(article)));
        }

        foreach (var receipt in receipts)
        {
            Assert.Equal(0, receipt.Location.Offset);
            Assert.True(store.TryRead(receipt.Location, out var read));
            Assert.True(read.Length > 0);
        }

        var again = store.AppendActiveBatch(Articles(writers, "after-flush"));
        Assert.Equal(writers, again.Length);
        Assert.All(again, receipt => Assert.True(receipt.Location.Offset > 0));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Corrupt_tail_is_rejected_and_the_next_batch_appends(int writers)
    {
        using var dir = TempDir.Create(writers);
        using var store = FileSegmentStore.Open(dir.Options);
        var articles = Articles(writers, "corrupt");
        store.TestAfterWriteBeforeFlush = (stream, offset, length) =>
        {
            stream.Position = offset + length - 1;
            stream.WriteByte(0x5A);
            stream.Position = offset + length;
            throw new IOException("corrupt-tail");
        };

        var error = Assert.ThrowsAny<Exception>(() => store.AppendActiveBatch(articles));
        AssertIoFailures(error);
        Assert.Equal(0, store.FlushedHeaderConfirmCount);
        Assert.All(articles, article => Assert.Equal(0, Count(dir.SegmentDir, MessageId(article))));

        store.TestAfterWriteBeforeFlush = null;
        var receipts = store.AppendActiveBatch(articles);
        foreach (var article in articles)
        {
            Assert.Equal(1, Count(dir.SegmentDir, MessageId(article)));
        }

        Assert.Equal(writers, store.OpenActiveSegmentCount);
        Assert.All(receipts, receipt => Assert.True(store.TryRead(receipt.Location, out _)));
    }

    [Fact]
    public void Partial_failure_on_one_segment_leaves_the_other_usable()
    {
        using var dir = TempDir.Create(2);
        using var store = FileSegmentStore.Open(dir.Options);
        var articles = Articles(2, "one-side");
        string? victim = null;
        var gate = new object();
        store.TestAfterWriteBeforeFlush = (stream, offset, _) =>
        {
            lock (gate)
            {
                victim ??= stream.Name;
                if (!string.Equals(victim, stream.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            stream.SetLength(offset + 4);
            throw new IOException("partial-one");
        };

        _ = Assert.ThrowsAny<Exception>(() => store.AppendActiveBatch(articles));
        store.TestAfterWriteBeforeFlush = null;

        var survivor = articles.Single(article => Count(dir.SegmentDir, MessageId(article)) == 1);
        var dropped = articles.Single(article => Count(dir.SegmentDir, MessageId(article)) == 0);
        Assert.NotEqual(MessageId(survivor), MessageId(dropped));

        var next = Articles(2, "one-side-next");
        var receipts = store.AppendActiveBatch(next);
        Assert.Equal(2, receipts.Select(static receipt => receipt.Location.SegmentId.Value).Distinct().Count());
        Assert.Equal(2, store.OpenActiveSegmentCount);
        Assert.Equal(1, Count(dir.SegmentDir, MessageId(survivor)));
        Assert.Equal(0, Count(dir.SegmentDir, MessageId(dropped)));
        Assert.All(next, article => Assert.Equal(1, Count(dir.SegmentDir, MessageId(article))));
        Assert.All(receipts, receipt => Assert.True(store.TryRead(receipt.Location, out _)));
    }

    [Fact]
    public void Four_writers_continue_after_one_segment_write_fails()
    {
        using var dir = TempDir.Create(4);
        using var store = FileSegmentStore.Open(dir.Options);
        var articles = Articles(4, "quad-fail");
        string? victim = null;
        var gate = new object();
        store.TestAfterWriteBeforeFlush = (stream, offset, _) =>
        {
            lock (gate)
            {
                victim ??= stream.Name;
                if (!string.Equals(victim, stream.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            stream.SetLength(offset + 4);
            throw new IOException("partial-quad");
        };

        _ = Assert.ThrowsAny<Exception>(() => store.AppendActiveBatch(articles));
        store.TestAfterWriteBeforeFlush = null;
        var next = Articles(4, "quad-next");
        var receipts = store.AppendActiveBatch(next);
        Assert.Equal(4, receipts.Select(static receipt => receipt.Location.SegmentId.Value).Distinct().Count());
        Assert.Equal(4, store.OpenActiveSegmentCount);
        Assert.All(next, article => Assert.Equal(1, Count(dir.SegmentDir, MessageId(article))));
    }

    [Fact]
    public void Restart_keeps_a_complete_record_left_by_an_ambiguous_flush()
    {
        using var dir = TempDir.Create(2);
        var articles = Articles(2, "restart");
        using (var store = FileSegmentStore.Open(dir.Options))
        {
            store.TestBeforeDurableFlush = () => throw new IOException("segment-flush");
            var error = Assert.ThrowsAny<Exception>(() => store.AppendActiveBatch(articles));
            AssertIoFailures(error, static failure => failure is UnreconciledDurableTailException);
        }

        using var restarted = FileSegmentStore.Open(dir.Options);
        foreach (var article in articles)
        {
            Assert.Equal(1, Count(dir.SegmentDir, MessageId(article)));
        }

        var next = restarted.AppendActiveBatch(Articles(2, "restart-next"));
        Assert.Equal(2, restarted.OpenActiveSegmentCount);
        Assert.Equal(2, next.Select(static receipt => receipt.Location.SegmentId.Value).Distinct().Count());
        foreach (var article in articles)
        {
            Assert.Equal(1, Count(dir.SegmentDir, MessageId(article)));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Engine_flush_failure_does_not_ack_early_and_retry_publishes_once(int writers)
    {
        using var dir = TempDir.Create(writers, hardLimit: 32L << 20);
        var records = new ArticleRecord[writers];
        for (var i = 0; i < writers; i++)
        {
            records[i] = Record($"<engine-{writers}-{i}@seg.test>", "persist\r\n");
        }

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        foreach (var record in records)
        {
            var accepted = await engine.AcceptAsync(record, CancellationToken.None);
            Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
            Assert.False(engine.Index.TryGet(record.ArtId, out var row) && row.State == ArticleStorageState.Present);
        }

        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        Assert.True(outstanding > 0);
        engine.Segments.TestBeforeDurableFlush = () => throw new IOException("segment-flush");
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        engine.TestEnqueueIncompleteWork();
        await WaitUntilAsync(() => engine.PersistRetryScheduledCount >= records.Length, TimeSpan.FromSeconds(10));

        Assert.Equal(outstanding, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(0, engine.Index.DurableFlushCount);
        foreach (var incomplete in engine.Journal.EnumerateIncomplete())
        {
            Assert.Null(incomplete.PhysicalWritten);
            Assert.True(engine.Journal.TryGetSequenceRetention(incomplete.Accept.Sequence, out var retention));
            Assert.False(retention.IndexCommitted);
            Assert.False(engine.Index.TryGet(incomplete.Accept.ArtId, out var row) && row.State == ArticleStorageState.Present);
        }

        engine.Segments.TestBeforeDurableFlush = null;
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await engine.DrainPendingAsync(drain.Token);

        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        foreach (var record in records)
        {
            Assert.True(engine.Index.TryGet(record.ArtId, out var row));
            Assert.Equal(ArticleStorageState.Present, row.State);
            Assert.True(engine.TryRead(record.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
            Assert.Equal(1, Count(dir.SegmentDir, $"<engine-{writers}-{Array.IndexOf(records, record)}@seg.test>"));
        }
    }

    private static void AssertIoFailures(Exception error, Func<Exception, bool>? match = null)
    {
        var leaves = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [error];
        Assert.NotEmpty(leaves);
        Assert.All(leaves, failure =>
        {
            Assert.IsAssignableFrom<IOException>(failure);
            if (match is not null)
            {
                Assert.True(match(failure));
            }
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private static ReadOnlyMemory<byte>[] Articles(int count, string prefix)
    {
        var articles = new ReadOnlyMemory<byte>[count];
        for (var i = 0; i < count; i++)
        {
            articles[i] = Record($"<{prefix}-{i}@seg.test>", "body\r\n").ArtData.ToArray();
        }

        return articles;
    }

    private static string MessageId(ReadOnlyMemory<byte> article)
    {
        var text = Encoding.ASCII.GetString(article.Span);
        var start = text.IndexOf("Message-ID: ", StringComparison.Ordinal);
        var from = start + "Message-ID: ".Length;
        var end = text.IndexOf("\r\n", from, StringComparison.Ordinal);
        return text[from..end];
    }

    private static int Count(string directory, string needleText)
    {
        var needle = Encoding.ASCII.GetBytes(needleText);
        var found = 0;
        foreach (var file in Directory.GetFiles(directory))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            for (var i = 0; i <= bytes.Length - needle.Length; i++)
            {
                if (bytes.AsSpan(i, needle.Length).SequenceEqual(needle))
                {
                    found++;
                }
            }
        }

        return found;
    }

    private static ArticleRecord Record(string messageId, string body)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase14\r\n\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class TempDir : IDisposable
    {
        private TempDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
            SegmentDir = options.SegmentDir;
        }

        public string SegmentDir { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        private string Root { get; }

        public static TempDir Create(int activeSegments, long hardLimit = 32L << 20)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase14-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            return new TempDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: control,
                    SegmentDir: cache,
                    JournalSoftLimitBytes: Math.Min(8L << 20, hardLimit),
                    JournalHardLimitBytes: hardLimit,
                    SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes,
                    MaxSegmentSealDelay: TimeSpan.FromHours(1),
                    ActiveSegmentCount: activeSegments));
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
