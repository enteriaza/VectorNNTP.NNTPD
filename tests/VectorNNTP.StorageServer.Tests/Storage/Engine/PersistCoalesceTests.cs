using System.Diagnostics;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Post-ACK coalescing. Accept stays behind the journal flush and ahead of the segment flush.
/// </summary>
public sealed class PersistCoalesceTests : IDisposable
{
    private TempStorageDir? _directory;

    public void Dispose() => _directory?.Dispose();

    [Fact]
    public async Task Single_pending_article_persists_after_the_bounded_wait()
    {
        await using var engine = Open();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PersistCoalesceMaxDelay = TimeSpan.FromSeconds(30);
        engine.TestPersistCoalesceDelay = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
        };

        var record = CreateRecord("<coalesce-one@seg.test>");
        var accepting = engine.AcceptAsync(record, CancellationToken.None);
        await WaitUntilAsync(() => engine.PendingSequenceCount == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(0, engine.Segments.DurableFlushCount);

        var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Equal(0, engine.Segments.DurableFlushCount);

        release.TrySetResult();
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(1, engine.PersistenceBatchCount);
        Assert.Equal(1, engine.PersistenceBatchArticles);
        Assert.True(engine.PersistenceBatchBytes > 0);
        Assert.True(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Multiple_pending_articles_coalesce_into_one_batch()
    {
        await using var engine = Open();
        engine.SuspendBackgroundPersist = true;
        var records = AcceptMany(engine, 3);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PersistCoalesceMaxDelay = TimeSpan.FromSeconds(30);
        engine.TestPersistCoalesceDelay = async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
        };

        engine.TestEnqueueIncompleteWork();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, engine.Segments.DurableFlushCount);
        Assert.Equal(0, engine.PersistBatchCount);

        release.TrySetResult();
        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(1, engine.PersistenceBatchCount);
        Assert.Equal(records.Length, engine.PersistenceBatchArticles);
        Assert.Equal(records.Length, engine.PersistenceBatchMaxObservedArticles);
        foreach (var record in records)
        {
            Assert.True(engine.TryRead(record.ArtId, out _));
        }
    }

    [Fact]
    public async Task Article_cap_splits_a_queued_handoff_into_immediate_batches()
    {
        await using var engine = Open();
        engine.SuspendBackgroundPersist = true;
        engine.PersistCoalesceMaxDelay = TimeSpan.Zero;
        engine.PersistCoalesceMaxArticles = 3;
        var records = AcceptMany(engine, 7);

        engine.TestEnqueueIncompleteWork();
        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(3, engine.PersistenceBatchCount);
        Assert.Equal(1, engine.PersistenceBatchArticles);
        Assert.Equal(3, engine.PersistenceBatchMaxObservedArticles);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(records.Length, records.Count(record => engine.TryRead(record.ArtId, out _)));
    }

    [Fact]
    public async Task Byte_cap_stops_before_the_next_article()
    {
        await using var engine = Open();
        engine.SuspendBackgroundPersist = true;
        engine.PersistCoalesceMaxDelay = TimeSpan.Zero;
        engine.PersistCoalesceMaxBytes = 1;
        var records = AcceptMany(engine, 3);

        engine.TestEnqueueIncompleteWork();
        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(records.Length, engine.PersistenceBatchCount);
        Assert.Equal(1, engine.PersistenceBatchMaxObservedArticles);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task Coalesce_delay_is_the_configured_bound_and_persistence_waits_for_it()
    {
        await using var engine = Open();
        engine.SuspendBackgroundPersist = true;
        var record = AcceptMany(engine, 1)[0];
        TimeSpan observed = TimeSpan.Zero;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delay = TimeSpan.FromMilliseconds(25);
        engine.PersistCoalesceMaxDelay = delay;
        engine.TestPersistCoalesceDelay = (remaining, ct) =>
        {
            observed = remaining;
            entered.TrySetResult();
            return release.Task.WaitAsync(ct);
        };

        engine.TestEnqueueIncompleteWork();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(observed.TotalMilliseconds, 20, 25);
        Assert.Equal(0, engine.Segments.DurableFlushCount);

        release.TrySetResult();
        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.True(engine.PersistenceBatchWaitTime > TimeSpan.Zero);
        Assert.Equal(1, engine.PersistenceBatchArticles);
        Assert.True(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Low_rate_traffic_persists_one_article_without_waiting_for_a_full_batch()
    {
        await using var engine = Open();
        engine.PersistCoalesceMaxArticles = 64;
        engine.PersistCoalesceMaxDelay = TimeSpan.FromMilliseconds(30);

        var first = CreateRecord("<coalesce-low-1@seg.test>");
        var firstStarted = Stopwatch.GetTimestamp();
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        var ackUs = (Stopwatch.GetTimestamp() - firstStarted) * 1_000_000d / Stopwatch.Frequency;
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(1, engine.PersistenceBatchArticles);
        Assert.InRange(engine.PersistenceBatchWaitTime.TotalMilliseconds, 15, 500);
        Assert.True(ackUs < engine.PersistenceBatchWaitTime.TotalMicroseconds);

        await Task.Delay(50);

        var second = CreateRecord("<coalesce-low-2@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(second, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(2, engine.PersistenceBatchCount);
        Assert.Equal(1, engine.PersistenceBatchArticles);
        Assert.Equal(1, engine.PersistenceBatchMaxObservedArticles);
        Assert.True(engine.TryRead(first.ArtId, out _));
        Assert.True(engine.TryRead(second.ArtId, out _));
    }

    [Fact]
    public async Task High_rate_handoff_does_not_wait_once_the_article_cap_is_full()
    {
        await using var engine = Open();
        engine.SuspendBackgroundPersist = true;
        engine.PersistCoalesceMaxArticles = 4;
        engine.PersistCoalesceMaxDelay = TimeSpan.FromHours(1);
        var waits = 0;
        engine.TestPersistCoalesceDelay = (_, _) =>
        {
            Interlocked.Increment(ref waits);
            return Task.CompletedTask;
        };
        var records = AcceptMany(engine, 8);

        engine.TestEnqueueIncompleteWork();
        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(0, waits);
        Assert.Equal(2, engine.PersistenceBatchCount);
        Assert.Equal(4, engine.PersistenceBatchArticles);
        Assert.Equal(4, engine.PersistenceBatchMaxObservedArticles);
        Assert.Equal(records.Length, records.Count(record => engine.TryRead(record.ArtId, out _)));
    }

    [Fact]
    public async Task Cancellation_during_the_coalesce_wait_leaves_the_journal_accept_intact()
    {
        var engine = Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PersistCoalesceMaxDelay = TimeSpan.FromHours(1);
        engine.TestPersistCoalesceDelay = (_, ct) =>
        {
            entered.TrySetResult();
            return Task.Delay(Timeout.Infinite, ct);
        };

        var record = CreateRecord("<coalesce-cancel@seg.test>");
        var accepting = engine.AcceptAsync(record, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, engine.Segments.DurableFlushCount);
        var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);

        await engine.DisposeAsync();

        await using var restarted = FileArticleStorageEngine.Open(_directory!.Options);
        Assert.Single(restarted.Journal.EnumerateIncomplete());
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.Empty(restarted.Journal.EnumerateIncomplete());
        Assert.Equal(0, restarted.Journal.OutstandingRecoverableBytes);
        Assert.True(restarted.TryRead(record.ArtId, out _));
        var duplicate = await restarted.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Duplicate, duplicate.Outcome);
    }

    [Fact]
    public async Task Persistence_failure_retries_a_coalesced_batch_without_losing_the_accept()
    {
        await using var engine = Open();
        engine.SuspendBackgroundPersist = true;
        engine.PersistCoalesceMaxDelay = TimeSpan.Zero;
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        var records = AcceptMany(engine, 2);
        var journalFlushesAfterAccept = engine.Journal.DurableFlushCount;
        Assert.True(journalFlushesAfterAccept >= records.Length);
        Assert.Equal(0, engine.Segments.DurableFlushCount);
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;

        engine.TestEnqueueIncompleteWork();
        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        foreach (var record in records)
        {
            Assert.True(engine.TryRead(record.ArtId, out _));
        }
    }

    [Fact]
    public async Task Restart_recovers_an_acked_article_that_was_still_waiting_to_persist()
    {
        var engine = Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PersistCoalesceMaxDelay = TimeSpan.FromHours(1);
        engine.TestPersistCoalesceDelay = (_, ct) =>
        {
            entered.TrySetResult();
            return Task.Delay(Timeout.Infinite, ct);
        };

        var record = CreateRecord("<coalesce-restart@seg.test>");
        var accepted = await AcceptAfterQueuedAsync(engine, record, entered.Task);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Equal(0, engine.Segments.DurableFlushCount);
        Assert.True(engine.Journal.OutstandingRecoverableBytes > 0);

        await engine.DisposeAsync();

        await using var restarted = FileArticleStorageEngine.Open(_directory!.Options);
        Assert.Single(restarted.Journal.EnumerateIncomplete());
        await restarted.RecoverAsync(CancellationToken.None);

        Assert.Empty(restarted.Journal.EnumerateIncomplete());
        Assert.Equal(0, restarted.Journal.OutstandingRecoverableBytes);
        Assert.True(restarted.TryRead(record.ArtId, out _));
        var duplicate = await restarted.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Duplicate, duplicate.Outcome);
    }

    [Fact]
    public async Task Accept_returns_after_the_journal_flush_and_before_the_segment_flush()
    {
        await using var engine = Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PersistCoalesceMaxDelay = TimeSpan.FromSeconds(30);
        engine.TestPersistCoalesceDelay = (_, ct) =>
        {
            entered.TrySetResult();
            return release.Task.WaitAsync(ct);
        };

        var record = CreateRecord("<coalesce-ack@seg.test>");
        var journalBefore = engine.Journal.DurableFlushCount;
        var accepting = engine.AcceptAsync(record, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.True(engine.Journal.DurableFlushCount > journalBefore);
        Assert.Equal(0, engine.Segments.DurableFlushCount);
        Assert.Equal(0, engine.Index.DurableFlushCount);
        Assert.True(engine.Journal.OutstandingRecoverableBytes > 0);

        release.TrySetResult();
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.True(engine.Segments.DurableFlushCount > 0);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
    }

    private async Task<ArticleAcceptResult> AcceptAfterQueuedAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record,
        Task entered)
    {
        var accepting = engine.AcceptAsync(record, CancellationToken.None);
        await entered.WaitAsync(TimeSpan.FromSeconds(5));
        return await accepting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private FileArticleStorageEngine Open()
    {
        _directory = TempStorageDir.Create();
        return FileArticleStorageEngine.Open(_directory.Options);
    }

    private static ArticleRecord[] AcceptMany(FileArticleStorageEngine engine, int count)
    {
        var records = new ArticleRecord[count];
        for (var i = 0; i < count; i++)
        {
            records[i] = CreateRecord($"<coalesce-{i}-{Guid.NewGuid():N}@seg.test>");
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
        _ = builder.Append("Subject: coalesce\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-coalesce-" + Guid.NewGuid().ToString("N"));
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
