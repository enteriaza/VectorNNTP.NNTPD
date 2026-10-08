using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Group commit withholds Accept until one shared journal durability flush returns.
/// </summary>
public sealed class JournalGroupCommitTests : IDisposable
{
    private TempStorageDir? _directory;

    public void Dispose() => _directory?.Dispose();

    [Fact]
    public async Task Full_group_is_durable_after_one_flush()
    {
        await using var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 4;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);
        var records = new[]
        {
            CreateRecord("<group-a@seg.test>"),
            CreateRecord("<group-b@seg.test>"),
            CreateRecord("<group-c@seg.test>"),
            CreateRecord("<group-d@seg.test>"),
        };

        using var barrier = new ArrivalBarrier(engine.Journal, records.Length);
        var all = Task.WhenAll(records.Select(record => Task.Run(() => engine.AcceptAsync(record, CancellationToken.None))));
        barrier.Release();
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(all, finished);
        var results = await all;

        Assert.All(results, static result => Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome));
        Assert.Equal(1, engine.Journal.DurableFlushCount);
        foreach (var record in records)
        {
            Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out var outstanding));
            Assert.Equal(record.ArtHash, outstanding.ArtHash);
        }
    }

    [Fact]
    public async Task Single_caller_does_not_arm_the_group_delay()
    {
        await using var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 8;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);
        var delayEntered = 0;
        engine.Journal.TestAcceptGroupDelay = (_, _) =>
        {
            Interlocked.Increment(ref delayEntered);
            return Task.CompletedTask;
        };
        var record = CreateRecord("<group-delay@seg.test>");

        var result = await engine.AcceptAsync(record, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome);
        Assert.Equal(0, Volatile.Read(ref delayEntered));
        Assert.Equal(1, engine.Journal.DurableFlushCount);
        Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out _));
    }

    [Fact]
    public async Task Flush_failure_does_not_acknowledge_and_retry_applies_the_group()
    {
        await using var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 2;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);
        var throws = 0;
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            if (Interlocked.Increment(ref throws) == 1)
            {
                throw new IOException("fsync");
            }
        };
        var first = CreateRecord("<group-fail-a@seg.test>");
        var second = CreateRecord("<group-fail-b@seg.test>");
        using var barrier = new ArrivalBarrier(engine.Journal, 2);

        var firstAccept = Task.Run(() => engine.AcceptAsync(first, CancellationToken.None));
        var secondAccept = Task.Run(() => engine.AcceptAsync(second, CancellationToken.None));
        barrier.Release();
        var bothFailed = Task.WhenAll(
            Assert.ThrowsAsync<UnreconciledDurableTailException>(async () => await firstAccept),
            Assert.ThrowsAsync<UnreconciledDurableTailException>(async () => await secondAccept));
        var failedInTime = await Task.WhenAny(bothFailed, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(bothFailed, failedInTime);
        await bothFailed;
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.False(engine.Journal.TryGetOutstanding(first.ArtId, out _));
        Assert.False(engine.Journal.TryGetOutstanding(second.ArtId, out _));

        engine.Journal.TestBeforeDurableFlush = null;
        engine.Journal.TestBeforeGroupedLock = null;
        var retried = await engine.AcceptAsync(first, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Accepted, retried.Outcome);
        Assert.Equal(1, engine.Journal.DurableFlushCount);
        Assert.True(engine.Journal.TryGetOutstanding(first.ArtId, out _));
        Assert.True(engine.Journal.TryGetOutstanding(second.ArtId, out _));
        var duplicate = await engine.AcceptAsync(second, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Duplicate, duplicate.Outcome);
    }

    [Fact]
    public async Task Staged_duplicate_is_rejected_before_the_group_flushes()
    {
        await using var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 4;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.Zero;
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        engine.Journal.TestBeforeGroupedLock = () =>
        {
            if (Interlocked.Increment(ref arrivals) != 1)
            {
                return;
            }

            parked.TrySetResult();
            releaseParker.Task.GetAwaiter().GetResult();
        };
        var parker = Task.Run(() => engine.AcceptAsync(CreateRecord("<group-park@seg.test>"), CancellationToken.None));
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var record = CreateRecord("<group-dup@seg.test>");
        var accept = engine.AcceptAsync(record, CancellationToken.None);
        Assert.False(accept.IsCompleted);
        Assert.Equal(0, engine.Journal.DurableFlushCount);
        Assert.Equal(1, engine.Journal.StagedAcceptCount);

        var duplicate = await engine.AcceptAsync(record, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Duplicate, duplicate.Outcome);
        Assert.False(accept.IsCompleted);
        Assert.Equal(0, engine.Journal.DurableFlushCount);
        releaseParker.TrySetResult();
        var result = await accept;
        var parkedResult = await parker;
        Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, parkedResult.Outcome);
        Assert.Equal(1, engine.Journal.DurableFlushCount);
    }

    [Fact]
    public async Task Byte_limit_flushes_without_waiting_for_the_article_limit()
    {
        await using var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 10;
        engine.Journal.AcceptGroupMaxBytes = 1;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);

        var first = await engine.AcceptAsync(CreateRecord("<group-byte-a@seg.test>"), CancellationToken.None);
        var second = await engine.AcceptAsync(CreateRecord("<group-byte-b@seg.test>"), CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Accepted, first.Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, second.Outcome);
        Assert.Equal(2, engine.Journal.DurableFlushCount);
    }

    [Fact]
    public async Task Dispose_fails_a_staged_accept_without_acknowledging_it()
    {
        await using var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 4;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        engine.Journal.TestBeforeGroupedLock = () =>
        {
            if (Interlocked.Increment(ref arrivals) != 1)
            {
                return;
            }

            parked.TrySetResult();
            releaseParker.Task.GetAwaiter().GetResult();
        };
        var parker = Task.Run(() => engine.AcceptAsync(CreateRecord("<group-dispose-park@seg.test>"), CancellationToken.None));
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var accept = engine.AcceptAsync(CreateRecord("<group-dispose@seg.test>"), CancellationToken.None);
        Assert.Equal(1, engine.Journal.StagedAcceptCount);
        Assert.False(accept.IsCompleted);

        await engine.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await accept);
        Assert.Equal(0, engine.Journal.DurableFlushCount);
        releaseParker.TrySetResult();
        await Assert.ThrowsAnyAsync<Exception>(async () => await parker);
    }

    private FileArticleStorageEngine OpenSuspended()
    {
        _directory = TempStorageDir.Create();
        var engine = FileArticleStorageEngine.Open(_directory.Options);
        engine.SuspendBackgroundPersist = true;
        return engine;
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
        _ = builder.Append("Subject: group\r\n");
        _ = builder.Append("\r\n").Append("line1\r\nline2\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class ArrivalBarrier : IDisposable
    {
        private readonly CountdownEvent _arrived;
        private readonly ManualResetEventSlim _go = new(false);

        public ArrivalBarrier(FileArticleJournal journal, int callers)
        {
            _arrived = new CountdownEvent(callers);
            journal.TestBeforeGroupedLock = () =>
            {
                _arrived.Signal();
                if (!_arrived.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Grouped arrivals did not meet.");
                }

                if (!_go.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Grouped arrival barrier was not released.");
                }
            };
        }

        public void Release()
        {
            if (!_arrived.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Grouped arrivals did not meet.");
            }

            _go.Set();
        }

        public void Dispose()
        {
            _go.Dispose();
            _arrived.Dispose();
        }
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-group-" + Guid.NewGuid().ToString("N"));
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
