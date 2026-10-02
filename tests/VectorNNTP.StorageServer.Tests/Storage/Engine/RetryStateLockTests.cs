using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Retry-attempt state is excluded from the engine gate. These tests cover that split.
/// </summary>
public sealed class RetryStateLockTests : IDisposable
{
    private readonly List<TempStorageDir> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            directory.Dispose();
        }
    }

    [Fact]
    public async Task Clear_proceeds_while_accept_holds_gate_during_durable_flush()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        const ulong other = 99;
        Assert.Equal(1, engine.TestIncrementPersistRetryAttempts(other));
        Assert.Equal(1, engine.TestIncrementPersistBlockedRetryAttempts(other));
        engine.TestAddAcceptWithoutPhysicalBytes(other);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            engine.Journal.TestBeforeDurableFlush = null;
            entered.TrySetResult();
            if (!release.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    "ClearPersistRetryAttempts did not finish while Accept held the engine gate.");
            }
        };

        var acceptTask = Task.Run(() => engine.AcceptAsync(CreateRecord("<retry-gate@seg.test>"), CancellationToken.None));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var clearTask = Task.Run(() => engine.ClearPersistRetryAttempts(other));
            var winner = await Task.WhenAny(clearTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(clearTask, winner);
            await clearTask;
            Assert.Equal(0, engine.TestReadPersistRetryAttempts(other));
            Assert.Equal(0, engine.TestReadPersistBlockedRetryAttempts(other));
            Assert.False(engine.TestContainsAcceptWithoutPhysicalBytes(other));
        }
        finally
        {
            release.TrySetResult();
        }

        var accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.True(engine.TestContainsAcceptWithoutPhysicalBytes(accepted.Sequence));
        Assert.Equal(0, engine.TestReadPersistRetryAttempts(other));
    }

    [Fact]
    public async Task Clear_does_not_wait_on_engine_gate()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Journal.TestBeforeDurableFlush = () =>
        {
            engine.Journal.TestBeforeDurableFlush = null;
            entered.TrySetResult();
            if (!release.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    "ClearPersistRetryAttempts waited on the engine gate.");
            }
        };

        var acceptTask = Task.Run(() => engine.AcceptAsync(CreateRecord("<retry-nowait@seg.test>"), CancellationToken.None));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var started = Environment.TickCount64;
            engine.ClearPersistRetryAttempts(1);
            var elapsed = Environment.TickCount64 - started;
            Assert.True(elapsed < 1000, $"ClearPersistRetryAttempts took {elapsed} ms while Accept held the engine gate.");
        }
        finally
        {
            release.TrySetResult();
        }

        var accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
    }

    [Fact]
    public async Task Concurrent_add_and_clear_are_race_free()
    {
        await using var engine = OpenSuspended();
        const int keys = 32;
        const int rounds = 200;
        var tasks = new Task[keys * 2];
        for (var i = 0; i < keys; i++)
        {
            var sequence = (ulong)(i + 1);
            tasks[i] = Task.Run(() =>
            {
                for (var n = 0; n < rounds; n++)
                {
                    _ = engine.TestIncrementPersistRetryAttempts(sequence);
                    _ = engine.TestIncrementPersistBlockedRetryAttempts(sequence);
                    engine.TestAddAcceptWithoutPhysicalBytes(sequence);
                }
            });
            tasks[keys + i] = Task.Run(() =>
            {
                for (var n = 0; n < rounds; n++)
                {
                    engine.ClearPersistRetryAttempts(sequence);
                }
            });
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));
        for (var i = 0; i < keys; i++)
        {
            var sequence = (ulong)(i + 1);
            var retry = engine.TestReadPersistRetryAttempts(sequence);
            var blocked = engine.TestReadPersistBlockedRetryAttempts(sequence);
            Assert.InRange(retry, 0, rounds);
            Assert.InRange(blocked, 0, rounds);
            Assert.Equal(retry + 1, engine.TestIncrementPersistRetryAttempts(sequence));
            Assert.Equal(blocked + 1, engine.TestIncrementPersistBlockedRetryAttempts(sequence));
        }
    }

    [Fact]
    public async Task Concurrent_remove_and_clear_are_race_free()
    {
        await using var engine = OpenSuspended();
        const int keys = 32;
        const int rounds = 200;
        var tasks = new Task[keys * 2];
        for (var i = 0; i < keys; i++)
        {
            var sequence = (ulong)(i + 1);
            tasks[i] = Task.Run(() =>
            {
                for (var n = 0; n < rounds; n++)
                {
                    engine.TestAddAcceptWithoutPhysicalBytes(sequence);
                    engine.RemoveAcceptWithoutPhysicalBytes(sequence);
                }
            });
            tasks[keys + i] = Task.Run(() =>
            {
                for (var n = 0; n < rounds; n++)
                {
                    engine.ClearPersistRetryAttempts(sequence);
                }
            });
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));
        for (var i = 0; i < keys; i++)
        {
            var sequence = (ulong)(i + 1);
            engine.TestAddAcceptWithoutPhysicalBytes(sequence);
            Assert.True(engine.TestContainsAcceptWithoutPhysicalBytes(sequence));
            engine.RemoveAcceptWithoutPhysicalBytes(sequence);
            Assert.False(engine.TestContainsAcceptWithoutPhysicalBytes(sequence));
        }
    }

    [Fact]
    public async Task Concurrent_membership_add_and_remove_are_race_free()
    {
        await using var engine = OpenSuspended();
        const ulong sequence = 11;
        var stop = 0;
        var reader = Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                _ = engine.TestContainsAcceptWithoutPhysicalBytes(sequence);
            }

            _ = engine.TestContainsAcceptWithoutPhysicalBytes(sequence);
        });
        var writer = Task.Run(() =>
        {
            for (var n = 0; n < 400; n++)
            {
                engine.TestAddAcceptWithoutPhysicalBytes(sequence);
                engine.RemoveAcceptWithoutPhysicalBytes(sequence);
                engine.ClearPersistRetryAttempts(sequence);
            }
        });

        await writer.WaitAsync(TimeSpan.FromSeconds(15));
        Volatile.Write(ref stop, 1);
        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        engine.TestAddAcceptWithoutPhysicalBytes(sequence);
        Assert.True(engine.TestContainsAcceptWithoutPhysicalBytes(sequence));
        engine.RemoveAcceptWithoutPhysicalBytes(sequence);
        Assert.False(engine.TestContainsAcceptWithoutPhysicalBytes(sequence));
    }

    [Fact]
    public async Task Concurrent_clears_of_one_sequence_are_idempotent()
    {
        await using var engine = OpenSuspended();
        const ulong sequence = 7;
        _ = engine.TestIncrementPersistRetryAttempts(sequence);
        Assert.Equal(2, engine.TestIncrementPersistRetryAttempts(sequence));
        Assert.Equal(1, engine.TestIncrementPersistBlockedRetryAttempts(sequence));
        engine.TestAddAcceptWithoutPhysicalBytes(sequence);

        var first = Task.Run(() =>
        {
            for (var n = 0; n < 1000; n++)
            {
                engine.ClearPersistRetryAttempts(sequence);
            }
        });
        var second = Task.Run(() =>
        {
            for (var n = 0; n < 1000; n++)
            {
                engine.ClearPersistRetryAttempts(sequence);
            }
        });
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, engine.TestReadPersistRetryAttempts(sequence));
        Assert.Equal(0, engine.TestReadPersistBlockedRetryAttempts(sequence));
        Assert.False(engine.TestContainsAcceptWithoutPhysicalBytes(sequence));
        engine.ClearPersistRetryAttempts(sequence);
        Assert.Equal(1, engine.TestIncrementPersistRetryAttempts(sequence));
    }

    [Fact]
    public async Task Clear_of_sequence_does_not_remove_the_next_sequence()
    {
        await using var engine = OpenSuspended();
        const ulong current = 4;
        const ulong next = 5;
        for (var n = 0; n < 3; n++)
        {
            _ = engine.TestIncrementPersistRetryAttempts(current);
        }

        for (var n = 0; n < 4; n++)
        {
            _ = engine.TestIncrementPersistRetryAttempts(next);
            _ = engine.TestIncrementPersistBlockedRetryAttempts(next);
        }

        engine.TestAddAcceptWithoutPhysicalBytes(current);
        engine.TestAddAcceptWithoutPhysicalBytes(next);

        engine.ClearPersistRetryAttempts(current);

        Assert.Equal(0, engine.TestReadPersistRetryAttempts(current));
        Assert.Equal(0, engine.TestReadPersistBlockedRetryAttempts(current));
        Assert.False(engine.TestContainsAcceptWithoutPhysicalBytes(current));
        Assert.Equal(4, engine.TestReadPersistRetryAttempts(next));
        Assert.Equal(4, engine.TestReadPersistBlockedRetryAttempts(next));
        Assert.True(engine.TestContainsAcceptWithoutPhysicalBytes(next));
    }

    [Fact]
    public async Task Retry_scheduling_still_requeues_and_then_clears_attempt_state()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;
        const ulong neighbor = 50;
        Assert.Equal(1, engine.TestIncrementPersistRetryAttempts(neighbor));

        var record = CreateRecord("<retry-schedule@seg.test>");
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.NotEqual(neighbor, accepted.Sequence);

        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.True(engine.PersistRetryScheduledCount >= 1);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(0, engine.TestReadPersistRetryAttempts(accepted.Sequence));
        Assert.Equal(0, engine.TestReadPersistBlockedRetryAttempts(accepted.Sequence));
        Assert.False(engine.TestContainsAcceptWithoutPhysicalBytes(accepted.Sequence));
        Assert.Equal(1, engine.TestReadPersistRetryAttempts(neighbor));
    }

    [Fact]
    public async Task Accept_records_never_written_until_persist_completes()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        const ulong neighbor = 60;
        engine.TestAddAcceptWithoutPhysicalBytes(neighbor);

        var accepted = await engine.AcceptAsync(CreateRecord("<retry-mark@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.True(engine.TestContainsAcceptWithoutPhysicalBytes(accepted.Sequence));
        Assert.True(engine.TestContainsAcceptWithoutPhysicalBytes(neighbor));

        engine.TestEnqueueIncompleteWork();
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.False(engine.TestContainsAcceptWithoutPhysicalBytes(accepted.Sequence));
        Assert.Equal(0, engine.TestReadPersistRetryAttempts(accepted.Sequence));
        Assert.True(engine.TestContainsAcceptWithoutPhysicalBytes(neighbor));
    }

    private FileArticleStorageEngine OpenSuspended()
    {
        var directory = TempStorageDir.Create();
        _directories.Add(directory);
        var engine = FileArticleStorageEngine.Open(directory.Options);
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
        _ = builder.Append("Subject: retry-state\r\n");
        _ = builder.Append("\r\nline1\r\nline2\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-retry-lock-" + Guid.NewGuid().ToString("N"));
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
