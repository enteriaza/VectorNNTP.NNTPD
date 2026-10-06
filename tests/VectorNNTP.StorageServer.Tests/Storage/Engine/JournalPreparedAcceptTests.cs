using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Parallel preparation reserves journal order, encodes outside the journal lock, and still
/// appends through one writer and one group flush.
/// </summary>
public sealed class JournalPreparedAcceptTests : IDisposable
{
    private TempStorageDir? _directory;

    public void Dispose() => _directory?.Dispose();

    [Fact]
    public async Task Later_preparation_does_not_append_before_the_earlier_sequence()
    {
        await using var engine = OpenPrepared(groupLimit: 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Journal.TestPreparedEncodeGate = async (sequence, _) =>
        {
            if (sequence == 1)
            {
                await release.Task.ConfigureAwait(false);
                return;
            }

            laterReady.TrySetResult();
        };
        var first = CreateRecord("<prep-order-a@seg.test>");
        var second = CreateRecord("<prep-order-b@seg.test>");
        var firstAccept = engine.AcceptAsync(first, CancellationToken.None);
        var secondAccept = engine.AcceptAsync(second, CancellationToken.None);
        var ready = await Task.WhenAny(laterReady.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(laterReady.Task, ready);
        await WaitUntilAsync(() => engine.Journal.PreparedReadyCount == 1, TimeSpan.FromSeconds(5));

        Assert.False(firstAccept.IsCompleted);
        Assert.False(secondAccept.IsCompleted);
        Assert.Equal(0, engine.Journal.DurableFlushCount);
        release.TrySetResult();
        var results = await Task.WhenAll(firstAccept, secondAccept);

        Assert.Equal(ArticleAcceptOutcome.Accepted, results[0].Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, results[1].Outcome);
        Assert.True(results[0].Sequence < results[1].Sequence);
        Assert.Equal(1, engine.Journal.DurableFlushCount);
        Assert.Equal(2, engine.Journal.EnumerateIncomplete().Count);
    }

    [Fact]
    public async Task Prepared_bytes_stop_at_the_journal_hard_limit()
    {
        var sample = CreateRecord("<prep-bound@seg.test>");
        await using var engine = OpenPrepared(groupLimit: 4, hardLimit: sample.ArtSize);
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.Zero;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Journal.TestPreparedEncodeGate = async (_, _) =>
        {
            reserved.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };
        var first = engine.AcceptAsync(sample, CancellationToken.None);
        await reserved.Task;

        var rejected = await engine.AcceptAsync(CreateRecord("<prep-bound-2@seg.test>"), CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.RejectedPressure, rejected.Outcome);
        Assert.Equal(1, engine.Journal.PreparedAcceptCount);
        Assert.True(engine.Journal.PreparedPayloadHighWater <= sample.ArtSize);
        Assert.False(first.IsCompleted);
        release.TrySetResult();
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await first).Outcome);
    }

    [Fact]
    public async Task Preparation_failure_writes_no_journal_record()
    {
        await using var engine = OpenPrepared(groupLimit: 1);
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.Zero;
        engine.Journal.TestPreparedEncodeGate = (sequence, _) =>
            sequence == 1
                ? Task.FromException(new IOException("prepare"))
                : Task.CompletedTask;

        var failed = await Assert.ThrowsAsync<IOException>(
            () => engine.AcceptAsync(CreateRecord("<prep-fail@seg.test>"), CancellationToken.None));
        Assert.Equal("prepare", failed.Message);
        Assert.Equal(0, engine.Journal.DurableFlushCount);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(0, engine.Journal.PreparedAcceptCount);

        var accepted = await engine.AcceptAsync(CreateRecord("<prep-fail-next@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Equal(2UL, accepted.Sequence);
        Assert.False(engine.Journal.TryGetOutstanding(CreateRecord("<prep-fail@seg.test>").ArtId, out _));
    }

    [Fact]
    public async Task Cancellation_during_preparation_writes_no_journal_record()
    {
        await using var engine = OpenPrepared(groupLimit: 4);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        engine.Journal.TestPreparedEncodeGate = async (sequence, token) =>
        {
            if (sequence != 1)
            {
                return;
            }

            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        };
        var accept = engine.AcceptAsync(CreateRecord("<prep-cancel@seg.test>"), cancellation.Token);
        await entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await accept);
        Assert.Equal(0, engine.Journal.DurableFlushCount);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.Zero;

        var next = await engine.AcceptAsync(CreateRecord("<prep-cancel-next@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, next.Outcome);
        Assert.Equal(2UL, next.Sequence);
    }

    [Fact]
    public async Task Append_failure_does_not_acknowledge_prepared_records()
    {
        await using var engine = OpenPrepared(groupLimit: 2);
        engine.Journal.TestBeforeFrameAppend = () => throw new IOException("append");
        var first = CreateRecord("<prep-append-a@seg.test>");
        var second = CreateRecord("<prep-append-b@seg.test>");

        var both = Task.WhenAll(
            Assert.ThrowsAsync<IOException>(() => engine.AcceptAsync(first, CancellationToken.None)),
            Assert.ThrowsAsync<IOException>(() => engine.AcceptAsync(second, CancellationToken.None)));
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(both, finished);
        await both;
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.False(engine.Journal.TryGetOutstanding(first.ArtId, out _));
        Assert.False(engine.Journal.TryGetOutstanding(second.ArtId, out _));
    }

    [Fact]
    public async Task Group_flush_failure_does_not_acknowledge_and_retry_applies_the_group()
    {
        await using var engine = OpenPrepared(groupLimit: 2);
        var throws = 0;
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            if (Interlocked.Increment(ref throws) == 1)
            {
                throw new IOException("fsync");
            }
        };
        var first = CreateRecord("<prep-flush-a@seg.test>");
        var second = CreateRecord("<prep-flush-b@seg.test>");
        var firstAccept = engine.AcceptAsync(first, CancellationToken.None);
        var secondAccept = engine.AcceptAsync(second, CancellationToken.None);
        await Assert.ThrowsAsync<UnreconciledDurableTailException>(async () => await firstAccept);
        await Assert.ThrowsAsync<UnreconciledDurableTailException>(async () => await secondAccept);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);

        engine.Journal.TestBeforeDurableFlush = null;
        var retried = await engine.AcceptAsync(first, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, retried.Outcome);
        Assert.True(engine.Journal.TryGetOutstanding(first.ArtId, out _));
        Assert.True(engine.Journal.TryGetOutstanding(second.ArtId, out _));
        var duplicate = await engine.AcceptAsync(second, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal(1, engine.Journal.DurableFlushCount);
    }

    [Fact]
    public async Task Dispose_before_append_leaves_a_recoverable_journal_without_the_article()
    {
        var engine = OpenPrepared(groupLimit: 4);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Journal.TestPreparedEncodeGate = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };
        var record = CreateRecord("<prep-crash-before@seg.test>");
        var accept = engine.AcceptAsync(record, CancellationToken.None);
        await entered.Task;
        await engine.DisposeAsync();
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(async () => await accept);

        await using var restarted = FileArticleStorageEngine.Open(_directory!.Options);
        Assert.False(restarted.Journal.TryGetOutstanding(record.ArtId, out _));
        Assert.Equal(0, restarted.Journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task Torn_tail_before_flush_is_discarded_on_recovery()
    {
        var engine = OpenPrepared(groupLimit: 1);
        engine.Journal.TestAfterWriteBeforeFlush = (stream, _, _) => stream.SetLength(Math.Max(0, stream.Length - 8));
        engine.Journal.TestBeforeDurableFlush = () => throw new IOException("fsync");
        var record = CreateRecord("<prep-torn@seg.test>");
        await Assert.ThrowsAnyAsync<IOException>(() => engine.AcceptAsync(record, CancellationToken.None));
        await engine.DisposeAsync();

        await using var restarted = FileArticleStorageEngine.Open(_directory!.Options);
        Assert.False(restarted.Journal.TryGetOutstanding(record.ArtId, out _));
    }

    [Fact]
    public async Task Durable_accept_is_still_outstanding_after_restart()
    {
        var record = CreateRecord("<prep-restart@seg.test>");
        var engine = OpenPrepared(groupLimit: 1);
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        await engine.DisposeAsync();

        await using var restarted = FileArticleStorageEngine.Open(_directory!.Options);
        Assert.True(restarted.Journal.TryGetOutstanding(record.ArtId, out var outstanding));
        Assert.True(outstanding.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Accept_does_not_complete_before_the_durability_flush()
    {
        await using var engine = OpenPrepared(groupLimit: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Journal.TestPreparedEncodeGate = async (_, _) => await Task.Yield();
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var accept = engine.AcceptAsync(CreateRecord("<prep-ack@seg.test>"), CancellationToken.None);
        var started = await Task.WhenAny(entered.Task, accept);
        Assert.Same(entered.Task, started);
        Assert.False(accept.IsCompleted);
        release.TrySetResult();
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await accept).Outcome);
        Assert.Equal(1, engine.Journal.DurableFlushCount);
    }

    [Fact]
    public async Task Outstanding_payload_matches_the_accepted_article_and_survives_persist()
    {
        await using var engine = OpenPrepared(groupLimit: 1, suspendPersist: false);
        var record = CreateRecord("<prep-own@seg.test>");
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task Duplicate_while_prepared_does_not_reserve_a_second_sequence()
    {
        await using var engine = OpenPrepared(groupLimit: 4);
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.Zero;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Journal.TestPreparedEncodeGate = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };
        var record = CreateRecord("<prep-dup@seg.test>");
        var accept = engine.AcceptAsync(record, CancellationToken.None);
        await entered.Task;

        var duplicate = await engine.AcceptAsync(record, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal(1, engine.Journal.PreparedAcceptCount);
        Assert.False(accept.IsCompleted);
        release.TrySetResult();
        var result = await accept;
        Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome);
        Assert.Equal(1UL, result.Sequence);
        Assert.Equal(1, engine.Journal.DurableFlushCount);
    }

    private FileArticleStorageEngine OpenPrepared(int groupLimit, long? hardLimit = null, bool suspendPersist = true)
    {
        _directory = TempStorageDir.Create(hardLimit);
        var engine = FileArticleStorageEngine.Open(_directory.Options);
        engine.SuspendBackgroundPersist = suspendPersist;
        engine.Journal.AcceptParallelPreparation = true;
        engine.Journal.AcceptGroupLimit = groupLimit;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);
        return engine;
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
        _ = builder.Append("Subject: prepared\r\n");
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

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create(long? hardLimit)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-prep-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            var hard = hardLimit ?? ArticleStorageOptions.DefaultJournalHardLimitBytes;
            return new TempStorageDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: control,
                    SegmentDir: cache,
                    JournalSoftLimitBytes: Math.Min(ArticleStorageOptions.DefaultJournalSoftLimitBytes, hard),
                    JournalHardLimitBytes: hard,
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
