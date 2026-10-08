using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Durable Accept group commit: shared flush, cancellation, accounting, and post-ACK persistence.
/// </summary>
public sealed class GroupCommitContractTests : IDisposable
{
    private TempStorageDir? _directory;

    public void Dispose() => _directory?.Dispose();

    [Fact]
    public async Task Shipped_defaults_flush_each_accept()
    {
        await using var engine = OpenSuspended();
        Assert.Equal(1, engine.Journal.AcceptGroupLimit);
        Assert.Equal(TimeSpan.Zero, engine.Journal.AcceptGroupMaxDelay);
        Assert.Equal(long.MaxValue, engine.Journal.AcceptGroupMaxBytes);
        var firstRecord = CreateRecord("<default-a@seg.test>");
        var secondRecord = CreateRecord("<default-b@seg.test>");

        var first = await engine.AcceptAsync(firstRecord, CancellationToken.None);
        var second = await engine.AcceptAsync(secondRecord, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Accepted, first.Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, second.Outcome);
        Assert.Equal(2, engine.Journal.DurableFlushCount);
        Assert.Equal(firstRecord.ArtSize + secondRecord.ArtSize, engine.Journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task Group_limit_two_shares_one_flush_and_a_larger_limit_shares_one_flush()
    {
        await using var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 2;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.Zero;
        using (var barrier = new ArrivalBarrier(engine.Journal, 2))
        {
            var pairTask = Task.WhenAll(
                Task.Run(() => engine.AcceptAsync(CreateRecord("<limit2-a@seg.test>"), CancellationToken.None)),
                Task.Run(() => engine.AcceptAsync(CreateRecord("<limit2-b@seg.test>"), CancellationToken.None)));
            barrier.Release();
            var pair = await pairTask;
            Assert.All(pair, static result => Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome));
            Assert.Equal(1, engine.Journal.DurableFlushCount);
        }

        engine.Journal.AcceptGroupLimit = 8;
        var many = Enumerable.Range(0, 8)
            .Select(index => CreateRecord($"<limit8-{index}@seg.test>"))
            .ToArray();
        using (var barrier = new ArrivalBarrier(engine.Journal, many.Length))
        {
            var groupedTask = Task.WhenAll(many.Select(record => Task.Run(() => engine.AcceptAsync(record, CancellationToken.None))));
            barrier.Release();
            var grouped = await groupedTask;
            Assert.All(grouped, static result => Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome));
            Assert.Equal(2, engine.Journal.DurableFlushCount);
            Assert.Equal(10, engine.Journal.EnumerateIncomplete().Count);
            Assert.Equal(10, grouped.Select(result => result.Sequence).Distinct().Count() + 2);
        }
    }

    [Fact]
    public async Task Group_fills_before_the_delay_and_does_not_wait_out_the_timer()
    {
        await using var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 2;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Journal.TestAcceptGroupDelay = async (_, cancellationToken) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                throw;
            }
        };

        using var barrier = new ArrivalBarrier(engine.Journal, 2);
        var acceptsTask = Task.WhenAll(
            Task.Run(() => engine.AcceptAsync(CreateRecord("<fill-a@seg.test>"), CancellationToken.None)),
            Task.Run(() => engine.AcceptAsync(CreateRecord("<fill-b@seg.test>"), CancellationToken.None)));
        barrier.Release();
        var finished = await Task.WhenAny(acceptsTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(acceptsTask, finished);
        Assert.All(await acceptsTask, static result => Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome));
        Assert.Equal(1, engine.Journal.DurableFlushCount);
        if (started.Task.IsCompleted)
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Accept_stays_incomplete_until_the_shared_flush_returns()
    {
        await using var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 2;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.Zero;
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        using var barrier = new ArrivalBarrier(engine.Journal, 2);
        var accepts = Task.WhenAll(
            Task.Run(() => engine.AcceptAsync(CreateRecord("<ack-a@seg.test>"), CancellationToken.None)),
            Task.Run(() => engine.AcceptAsync(CreateRecord("<ack-b@seg.test>"), CancellationToken.None)));
        barrier.Release();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(accepts.IsCompleted);

        release.Set();
        var results = await accepts.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, static result => Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome));
        Assert.Equal(1, engine.Journal.DurableFlushCount);
        Assert.True(engine.Journal.OutstandingRecoverableBytes > 0);
    }

    [Fact]
    public async Task Failed_flush_acks_nobody_and_releases_the_reservation()
    {
        await using var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 2;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);
        engine.Journal.TestBeforeDurableFlush = static () => throw new IOException("injected");
        var first = CreateRecord("<fail-a@seg.test>");
        var second = CreateRecord("<fail-b@seg.test>");
        using (var barrier = new ArrivalBarrier(engine.Journal, 2))
        {
            var firstAccept = Task.Run(() => engine.AcceptAsync(first, CancellationToken.None));
            var secondAccept = Task.Run(() => engine.AcceptAsync(second, CancellationToken.None));
            barrier.Release();
            await Task.WhenAll(
                Assert.ThrowsAsync<UnreconciledDurableTailException>(async () => await firstAccept),
                Assert.ThrowsAsync<UnreconciledDurableTailException>(async () => await secondAccept));
        }

        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.False(engine.Journal.TryGetOutstanding(first.ArtId, out _));
        Assert.False(engine.Journal.TryGetOutstanding(second.ArtId, out _));

        engine.Journal.TestBeforeDurableFlush = null;
        engine.Journal.TestBeforeGroupedLock = null;
        var retried = await engine.AcceptAsync(first, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, retried.Outcome);
        Assert.True(engine.Journal.TryGetOutstanding(first.ArtId, out _));
        Assert.True(engine.Journal.TryGetOutstanding(second.ArtId, out _));
        var duplicate = await engine.AcceptAsync(second, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Duplicate, duplicate.Outcome);
    }

    [Fact]
    public async Task Cancellation_before_stage_writes_nothing()
    {
        await using var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 4;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.AcceptAsync(CreateRecord("<cancel-before@seg.test>"), canceled.Token));

        Assert.Equal(0, engine.Journal.DurableFlushCount);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(0, engine.Journal.StagedAcceptCount);
    }

    [Fact]
    public async Task Cancellation_while_waiting_does_not_ack_and_does_not_drop_the_group()
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
        var canceledRecord = CreateRecord("<cancel-wait-a@seg.test>");
        var kept = CreateRecord("<cancel-wait-b@seg.test>");
        using var cts = new CancellationTokenSource();
        var keptAccept = Task.Run(() => engine.AcceptAsync(kept, CancellationToken.None));
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var canceledAccept = engine.AcceptAsync(canceledRecord, cts.Token);
        Assert.False(canceledAccept.IsCompleted);
        Assert.Equal(0, engine.Journal.DurableFlushCount);
        await cts.CancelAsync();
        releaseParker.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledAccept);
        var keptResult = await keptAccept.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ArticleAcceptOutcome.Accepted, keptResult.Outcome);
        Assert.Equal(1, engine.Journal.DurableFlushCount);
        Assert.True(engine.Journal.TryGetOutstanding(canceledRecord.ArtId, out _));
        Assert.True(engine.Journal.TryGetOutstanding(kept.ArtId, out _));
        var duplicate = await engine.AcceptAsync(canceledRecord, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Duplicate, duplicate.Outcome);
    }

    [Fact]
    public async Task Cancellation_during_flush_does_not_ack_the_caller_or_drop_the_member()
    {
        await using var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 4;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.Zero;
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
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
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var canceledRecord = CreateRecord("<cancel-flush-a@seg.test>");
        var kept = CreateRecord("<cancel-flush-b@seg.test>");
        using var cts = new CancellationTokenSource();
        var keptAccept = Task.Run(() => engine.AcceptAsync(kept, CancellationToken.None));
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var canceledAccept = engine.AcceptAsync(canceledRecord, cts.Token);
        Assert.False(canceledAccept.IsCompleted);
        releaseParker.TrySetResult();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        await cts.CancelAsync();
        release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledAccept);
        var keptResult = await keptAccept.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleAcceptOutcome.Accepted, keptResult.Outcome);
        Assert.Equal(1, engine.Journal.DurableFlushCount);
        Assert.True(engine.Journal.TryGetOutstanding(canceledRecord.ArtId, out _));
        Assert.True(engine.Journal.TryGetOutstanding(kept.ArtId, out _));
    }

    [Fact]
    public async Task Shutdown_during_flush_completes_the_group_before_dispose()
    {
        var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 2;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.Zero;
        Task? disposing = null;
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            disposing = Task.Run(async () => await engine.DisposeAsync().ConfigureAwait(false));
        };
        using var barrier = new ArrivalBarrier(engine.Journal, 2);

        ArticleAcceptResult[]? results = null;
        try
        {
            var pending = Task.WhenAll(
                Task.Run(() => engine.AcceptAsync(CreateRecord("<shutdown-flush-a@seg.test>"), CancellationToken.None)),
                Task.Run(() => engine.AcceptAsync(CreateRecord("<shutdown-flush-b@seg.test>"), CancellationToken.None)));
            barrier.Release();
            results = await pending;
        }
        catch (Exception ex)
        {
            var root = ex is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : ex;
            Assert.IsType<ObjectDisposedException>(root);
        }

        if (results is not null)
        {
            Assert.All(results, static result => Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome));
        }

        Assert.NotNull(disposing);
        await disposing!.WaitAsync(TimeSpan.FromSeconds(5));

        await using var restarted = FileArticleStorageEngine.Open(_directory!.Options);
        Assert.Equal(2, restarted.Journal.EnumerateIncomplete().Count);
        Assert.True(restarted.Journal.OutstandingRecoverableBytes > 0);
    }

    [Fact]
    public async Task Checkpoint_retains_a_grouped_accept_until_persist_releases_it()
    {
        await using var engine = Open();
        engine.SuspendBackgroundPersist = true;
        engine.Journal.AcceptGroupLimit = 2;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);
        var records = new[]
        {
            CreateRecord("<checkpoint-a@seg.test>"),
            CreateRecord("<checkpoint-b@seg.test>"),
        };
        var results = await Task.WhenAll(records.Select(record => engine.AcceptAsync(record, CancellationToken.None)));
        Assert.All(results, static result => Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome));
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        Assert.Equal(records.Sum(record => record.ArtSize), outstanding);

        var retained = engine.CheckpointTruncateCommitted();
        Assert.Equal(outstanding, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(2, engine.Journal.EnumerateIncomplete().Count);
        Assert.True(retained >= 0);

        engine.PersistCoalesceMaxDelay = TimeSpan.Zero;
        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);
        _ = engine.CheckpointTruncateCommitted();

        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.All(records, record => Assert.True(engine.TryRead(record.ArtId, out _)));
    }

    [Fact]
    public async Task Recovery_replays_a_grouped_accept()
    {
        var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 2;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);
        var records = new[]
        {
            CreateRecord("<recover-a@seg.test>"),
            CreateRecord("<recover-b@seg.test>"),
        };
        var results = await Task.WhenAll(records.Select(record => engine.AcceptAsync(record, CancellationToken.None)));
        Assert.All(results, static result => Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome));
        await engine.DisposeAsync();

        await using var restarted = FileArticleStorageEngine.Open(_directory!.Options);
        Assert.Equal(2, restarted.Journal.EnumerateIncomplete().Count);
        Assert.True(restarted.Journal.OutstandingRecoverableBytes > 0);
        await restarted.RecoverAsync(CancellationToken.None);

        Assert.Empty(restarted.Journal.EnumerateIncomplete());
        Assert.Equal(0, restarted.Journal.OutstandingRecoverableBytes);
        foreach (var record in records)
        {
            Assert.True(restarted.TryRead(record.ArtId, out _));
            var duplicate = await restarted.AcceptAsync(record, CancellationToken.None);
            Assert.Equal(ArticleAcceptOutcome.Duplicate, duplicate.Outcome);
        }
    }

    [Fact]
    public async Task Grouped_accept_returns_before_persistence_coalescing()
    {
        await using var engine = Open();
        engine.Journal.AcceptGroupLimit = 2;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PersistCoalesceMaxDelay = TimeSpan.FromHours(1);
        engine.TestPersistCoalesceDelay = async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        };

        var records = new[]
        {
            CreateRecord("<coalesce-group-a@seg.test>"),
            CreateRecord("<coalesce-group-b@seg.test>"),
        };
        using var barrier = new ArrivalBarrier(engine.Journal, records.Length);
        var accepts = Task.WhenAll(records.Select(record => Task.Run(() => engine.AcceptAsync(record, CancellationToken.None))));
        barrier.Release();
        var results = await accepts.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, static result => Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome));
        Assert.Equal(1, engine.Journal.DurableFlushCount);
        Assert.Equal(0, engine.Segments.DurableFlushCount);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, engine.Segments.DurableFlushCount);

        release.TrySetResult();
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Segments.DurableFlushCount >= 1);
        Assert.All(records, record => Assert.True(engine.TryRead(record.ArtId, out _)));
    }

    [Fact]
    public async Task Pressure_rejects_a_group_member_without_bypassing_the_hard_limit()
    {
        await using var engine = OpenSuspended(journalSoftLimitBytes: 1, journalHardLimitBytes: 1);
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
        var first = CreateRecord("<pressure-a@seg.test>");
        var parker = Task.Run(() => engine.AcceptAsync(CreateRecord("<pressure-park@seg.test>"), CancellationToken.None));
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var accepting = engine.AcceptAsync(first, CancellationToken.None);
        Assert.False(accepting.IsCompleted);
        Assert.Equal(1, engine.Journal.StagedAcceptCount);

        var rejected = await engine.AcceptAsync(CreateRecord("<pressure-b@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedPressure, rejected.Outcome);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(1, engine.Journal.StagedAcceptCount);

        releaseParker.TrySetResult();
        var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(5));
        var parkedResult = await parker.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Equal(ArticleAcceptOutcome.RejectedPressure, parkedResult.Outcome);
        Assert.Equal(first.ArtSize, engine.Journal.OutstandingRecoverableBytes);
        Assert.False(engine.Journal.TryGetOutstanding(rejected.ArtId, out _));
    }

    private FileArticleStorageEngine OpenSuspended(long? journalSoftLimitBytes = null, long? journalHardLimitBytes = null)
    {
        var engine = Open(journalSoftLimitBytes, journalHardLimitBytes);
        engine.SuspendBackgroundPersist = true;
        return engine;
    }

    private FileArticleStorageEngine Open(long? journalSoftLimitBytes = null, long? journalHardLimitBytes = null)
    {
        _directory = TempStorageDir.Create(journalSoftLimitBytes, journalHardLimitBytes);
        return FileArticleStorageEngine.Open(_directory.Options);
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

        public string Root { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create(long? journalSoftLimitBytes = null, long? journalHardLimitBytes = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-group-contract-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            return new TempStorageDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: control,
                    SegmentDir: cache,
                    JournalSoftLimitBytes: journalSoftLimitBytes ?? ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    JournalHardLimitBytes: journalHardLimitBytes ?? ArticleStorageOptions.DefaultJournalHardLimitBytes,
                    SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
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
