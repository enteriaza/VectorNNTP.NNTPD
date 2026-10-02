using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Stage batching on the single persistence worker. Accept stays durable on the caller.
/// </summary>
public sealed class PersistenceBatchTests : IDisposable
{
    private TempStorageDir? _directory;

    public void Dispose() => _directory?.Dispose();

    [Fact]
    public async Task Worker_drain_flushes_each_later_stage_once()
    {
        await using var engine = OpenSuspended();
        var journalBefore = engine.Journal.DurableFlushCount;
        var segmentBefore = engine.Segments.DurableFlushCount;
        var indexBefore = engine.Index.DurableFlushCount;
        var records = new[]
        {
            CreateRecord("<batch-a@seg.test>"),
            CreateRecord("<batch-b@seg.test>"),
            CreateRecord("<batch-c@seg.test>"),
        };

        foreach (var record in records)
        {
            var accepted = await engine.AcceptAsync(record, CancellationToken.None);
            Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        }

        var acceptFlushes = engine.Journal.DurableFlushCount - journalBefore;
        Assert.Equal(records.Length, acceptFlushes);

        engine.TestEnqueueIncompleteWork();
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(1, engine.PersistBatchCount);
        Assert.Equal(records.Length, engine.LastPersistBatchArticleCount);
        Assert.Equal(records.Sum(static record => (long)record.ArtSize), engine.LastPersistBatchByteCount);
        Assert.Equal(1, engine.Segments.DurableFlushCount - segmentBefore);
        Assert.Equal(1, engine.Index.DurableFlushCount - indexBefore);
        var firstWorkerJournalFlushes = engine.Journal.DurableFlushCount - journalBefore - acceptFlushes;
        Assert.Equal(3, firstWorkerJournalFlushes);

        engine.SuspendBackgroundPersist = true;
        var more = new[]
        {
            CreateRecord("<batch-d@seg.test>"),
            CreateRecord("<batch-e@seg.test>"),
        };
        var journalBeforeSecond = engine.Journal.DurableFlushCount;
        var segmentBeforeSecond = engine.Segments.DurableFlushCount;
        var indexBeforeSecond = engine.Index.DurableFlushCount;
        foreach (var record in more)
        {
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        }

        var secondAcceptFlushes = engine.Journal.DurableFlushCount - journalBeforeSecond;
        Assert.Equal(more.Length, secondAcceptFlushes);
        engine.TestEnqueueIncompleteWork();
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(2, engine.PersistBatchCount);
        Assert.Equal(more.Length, engine.LastPersistBatchArticleCount);
        Assert.Equal(1, engine.Segments.DurableFlushCount - segmentBeforeSecond);
        Assert.Equal(1, engine.Index.DurableFlushCount - indexBeforeSecond);
        Assert.Equal(2, engine.Journal.DurableFlushCount - journalBeforeSecond - secondAcceptFlushes);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        foreach (var record in records)
        {
            Assert.True(engine.TryRead(record.ArtId, out _));
        }

        foreach (var record in more)
        {
            Assert.True(engine.TryRead(record.ArtId, out _));
        }
    }

    [Fact]
    public async Task Duplicate_art_id_is_rejected_while_the_accept_is_waiting_for_the_worker()
    {
        await using var engine = OpenSuspended();
        var record = CreateRecord("<batch-dup@seg.test>");
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);

        var duplicate = await engine.AcceptAsync(record, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Duplicate, duplicate.Outcome);
        Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out var outstanding));
        Assert.Equal(accepted.Sequence, outstanding.Sequence);
        Assert.Equal(record.ArtSize, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(0, engine.PersistBatchCount);
    }

    [Fact]
    public async Task Segment_flush_failure_keeps_accepts_and_does_not_publish_physical_written()
    {
        await using var engine = OpenSuspended();
        var records = AcceptMany(engine, 3);
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        engine.Segments.TestBeforeDurableFlush = () => throw new IOException("segment-flush");
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        var journalAfterAccept = engine.Journal.DurableFlushCount;

        engine.TestEnqueueIncompleteWork();
        await WaitUntilAsync(() => engine.PersistRetryScheduledCount >= records.Length, TimeSpan.FromSeconds(5));

        Assert.Equal(outstanding, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(records.Length, engine.Journal.EnumerateIncomplete().Count);
        Assert.All(engine.Journal.EnumerateIncomplete(), static item => Assert.Null(item.PhysicalWritten));
        Assert.Equal(0, engine.Segments.DurableFlushCount);
        Assert.Equal(0, engine.Index.DurableFlushCount);
        Assert.Equal(journalAfterAccept + 1, engine.Journal.DurableFlushCount);
    }

    [Fact]
    public async Task PhysicalWritten_flush_failure_does_not_mark_the_journal_state()
    {
        await using var engine = OpenSuspended();
        var records = AcceptMany(engine, 3);
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        var journalAfterAccept = engine.Journal.DurableFlushCount;
        var journalFlushes = 0;
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            journalFlushes++;
            if (journalFlushes >= 2)
            {
                throw new IOException("pw-flush");
            }
        };
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);

        engine.TestEnqueueIncompleteWork();
        await WaitUntilAsync(() => engine.PersistRetryScheduledCount >= records.Length, TimeSpan.FromSeconds(5));

        Assert.Equal(outstanding, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(journalAfterAccept + 1, engine.Journal.DurableFlushCount);
        Assert.All(
            engine.Journal.EnumerateIncomplete(),
            static item => Assert.Null(item.PhysicalWritten));
        Assert.Equal(1, engine.Segments.DurableFlushCount);
        Assert.Equal(0, engine.Index.DurableFlushCount);
    }

    [Fact]
    public async Task Index_flush_failure_keeps_physical_written_and_outstanding_bytes()
    {
        await using var engine = OpenSuspended();
        var records = AcceptMany(engine, 2);
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        engine.Index.TestBeforeDurableFlush = () => throw new IOException("index-flush");
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);

        engine.TestEnqueueIncompleteWork();
        await WaitUntilAsync(() => engine.PersistRetryScheduledCount >= records.Length, TimeSpan.FromSeconds(5));

        Assert.Equal(outstanding, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(1, engine.Segments.DurableFlushCount);
        Assert.Equal(0, engine.Index.DurableFlushCount);
        foreach (var incomplete in engine.Journal.EnumerateIncomplete())
        {
            Assert.NotNull(incomplete.PhysicalWritten);
            Assert.False(engine.Index.TryGet(incomplete.Accept.ArtId, out _));
            Assert.True(engine.Journal.TryGetSequenceRetention(incomplete.Accept.Sequence, out var retention));
            Assert.False(retention.IndexCommitted);
        }
    }

    [Fact]
    public async Task IndexCommitted_flush_failure_does_not_release_outstanding_or_allow_checkpoint_omission()
    {
        await using var engine = OpenSuspended();
        var records = AcceptMany(engine, 2);
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        var journalFlushes = 0;
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            journalFlushes++;
            if (journalFlushes >= 3)
            {
                throw new IOException("ic-flush");
            }
        };
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);

        engine.TestEnqueueIncompleteWork();
        await WaitUntilAsync(() => engine.PersistRetryScheduledCount >= records.Length, TimeSpan.FromSeconds(5));

        Assert.Equal(outstanding, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(1, engine.Index.DurableFlushCount);
        foreach (var record in records)
        {
            Assert.True(engine.Index.TryGet(record.ArtId, out var indexed));
            Assert.Equal(ArticleStorageState.Present, indexed.State);
        }

        foreach (var incomplete in engine.Journal.EnumerateIncomplete())
        {
            Assert.True(engine.Journal.TryGetSequenceRetention(incomplete.Accept.Sequence, out var retention));
            Assert.False(retention.IndexCommitted);
            Assert.NotNull(retention.PhysicalWritten);
        }

        var ex = Assert.Throws<UnreconciledDurableTailException>(() => engine.CheckpointTruncateCommitted());
        Assert.Contains("pending", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(records.Length, engine.Journal.EnumerateIncomplete().Count);
        Assert.Equal(outstanding, engine.Journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task Retry_of_one_sequence_does_not_rewrite_a_neighbor_that_already_committed()
    {
        await using var engine = OpenSuspended();
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        var records = AcceptMany(engine, 3);
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;

        engine.TestEnqueueIncompleteWork();
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(records.Length, engine.PhysicalAppendCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        foreach (var record in records)
        {
            Assert.True(engine.TryRead(record.ArtId, out _));
        }
    }

    [Fact]
    public async Task Failed_stage_retries_to_the_same_committed_state_as_a_full_batch()
    {
        await using var engine = OpenSuspended();
        var records = AcceptMany(engine, 2);
        var journalFlushes = 0;
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            journalFlushes++;
            if (journalFlushes >= 2)
            {
                throw new IOException("pw-flush");
            }
        };
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        engine.TestEnqueueIncompleteWork();
        await WaitUntilAsync(() => engine.PersistRetryScheduledCount >= records.Length, TimeSpan.FromSeconds(5));
        Assert.All(engine.Journal.EnumerateIncomplete(), static item => Assert.Null(item.PhysicalWritten));

        engine.Journal.TestBeforeDurableFlush = null;
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(1, engine.Segments.DurableFlushCount);
        foreach (var record in records)
        {
            Assert.True(engine.TryRead(record.ArtId, out _));
        }
    }

    private FileArticleStorageEngine OpenSuspended()
    {
        _directory = TempStorageDir.Create();
        var engine = FileArticleStorageEngine.Open(_directory.Options);
        engine.SuspendBackgroundPersist = true;
        return engine;
    }

    private static ArticleRecord[] AcceptMany(FileArticleStorageEngine engine, int count)
    {
        var records = new ArticleRecord[count];
        for (var i = 0; i < count; i++)
        {
            records[i] = CreateRecord($"<batch-{i}-{Guid.NewGuid():N}@seg.test>");
            var accepted = engine.AcceptAsync(records[i], CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        }

        return records;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
            {
                throw new TimeoutException("Condition not met within timeout.");
            }

            await Task.Delay(10);
        }
    }

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: batch\r\n");
        _ = builder.Append("\r\n").Append("line1\r\nline2\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-batch-" + Guid.NewGuid().ToString("N"));
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
