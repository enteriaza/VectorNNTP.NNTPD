using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

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

        var all = Task.WhenAll(records.Select(record => engine.AcceptAsync(record, CancellationToken.None)));
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
    public async Task Single_article_waits_for_delay_not_for_another_article()
    {
        await using var engine = OpenSuspended();
        engine.Journal.AcceptGroupLimit = 8;
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flushed = 0;
        engine.Journal.TestAcceptGroupDelay = async (_, cancellationToken) =>
        {
            staged.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        };
        engine.Journal.TestBeforeDurableFlush = () => Interlocked.Increment(ref flushed);
        var record = CreateRecord("<group-delay@seg.test>");

        var accept = engine.AcceptAsync(record, CancellationToken.None);
        var completed = await Task.WhenAny(accept, staged.Task);

        Assert.Same(staged.Task, completed);
        Assert.False(accept.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref flushed));
        release.TrySetResult();
        var result = await accept;
        Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome);
        Assert.Equal(1, engine.Journal.DurableFlushCount);
        Assert.Equal(1, Volatile.Read(ref flushed));
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

        var firstAccept = engine.AcceptAsync(first, CancellationToken.None);
        var secondAccept = engine.AcceptAsync(second, CancellationToken.None);
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
        engine.Journal.AcceptGroupMaxDelay = TimeSpan.FromHours(1);
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Journal.TestAcceptGroupDelay = async (_, cancellationToken) =>
        {
            staged.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        };
        var record = CreateRecord("<group-dup@seg.test>");
        var accept = engine.AcceptAsync(record, CancellationToken.None);
        await staged.Task;

        var duplicate = await engine.AcceptAsync(record, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Duplicate, duplicate.Outcome);
        Assert.False(accept.IsCompleted);
        Assert.Equal(0, engine.Journal.DurableFlushCount);
        Assert.Equal(1, engine.Journal.StagedAcceptCount);
        release.TrySetResult();
        var result = await accept;
        Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome);
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
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Journal.TestAcceptGroupDelay = async (_, cancellationToken) =>
        {
            staged.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        };
        var accept = engine.AcceptAsync(CreateRecord("<group-dispose@seg.test>"), CancellationToken.None);
        await staged.Task;

        await engine.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await accept);
        Assert.Equal(0, engine.Journal.DurableFlushCount);
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
