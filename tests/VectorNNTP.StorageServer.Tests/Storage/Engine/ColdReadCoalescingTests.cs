using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Concurrent published-segment reads of one article share a single physical read.
/// </summary>
public sealed class ColdReadCoalescingTests
{
    [Fact]
    public async Task OneColdRead_UsesOnePhysicalRead()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<one-cold@seg.test>", "one-body\r\n");
        await PublishAsync(engine, record);

        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(record.ArtHash, read.Metadata.ArtHash);
        Assert.Equal(record.ArtSize, read.Metadata.ArtSize);
        Assert.Equal(1, engine.SegmentArticleReadCount);
        Assert.Equal(0, engine.JournalArticleReadCount);
        Assert.Equal(0, engine.CacheArticleReadCount);
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(0, engine.ArticleReadCoalescedCount);
        AssertIdle(engine);
    }

    [Fact]
    public async Task TwoSimultaneousReads_ShareOnePhysicalRead()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<two-cold@seg.test>", "two-body\r\n");
        await PublishAsync(engine, record);

        var reads = await ReadTogetherAsync(engine, record.ArtId, readers: 2);
        AssertSameArticle(record, reads);
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(1, engine.ArticleReadCoalescedCount);
        Assert.Equal(1, engine.SegmentArticleReadCount);
        AssertIdle(engine);
    }

    [Fact]
    public async Task ManySimultaneousReads_ShareOnePhysicalRead()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<many-cold@seg.test>", "many-body\r\n");
        await PublishAsync(engine, record);

        var reads = await ReadTogetherAsync(engine, record.ArtId, readers: 100);
        AssertSameArticle(record, reads);
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(99, engine.ArticleReadCoalescedCount);
        Assert.Equal(1, engine.SegmentArticleReadCount);
        AssertIdle(engine);
    }

    [Fact]
    public async Task DifferentArticleIds_DoNotCoalesce()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var left = CreateRecord("<left-cold@seg.test>", "left-body\r\n");
        var right = CreateRecord("<right-cold@seg.test>", "right-body\r\n");
        await PublishAsync(engine, left);
        await PublishAsync(engine, right);

        var gate = new ReadGate();
        gate.Arm(engine, enteredTarget: 2);
        var slots = new[]
        {
            ReadSlot.Start(engine, left.ArtId),
            ReadSlot.Start(engine, right.ArtId),
        };
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, engine.ArticleReadPhysicalReadCount);
            Assert.Equal(0, engine.ArticleReadCoalescedCount);
            Assert.Equal(2, engine.ArticleReadInFlightCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            ReadSlot.JoinAll(slots);
        }

        Assert.True(slots[0].Found);
        Assert.True(slots[1].Found);
        Assert.True(slots[0].Result.ArtData.Span.SequenceEqual(left.ArtData.Span));
        Assert.True(slots[1].Result.ArtData.Span.SequenceEqual(right.ArtData.Span));
        Assert.Equal(0, engine.ArticleReadCoalescedCount);
        AssertIdle(engine);
    }

    [Fact]
    public async Task DifferentArticles_CanReadConcurrentlyWithinTheInFlightLimit()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.PhysicalReadCoalesceMaxInFlight = 2;
        var left = CreateRecord("<concurrent-left@seg.test>", "c-left\r\n");
        var right = CreateRecord("<concurrent-right@seg.test>", "c-right\r\n");
        await PublishAsync(engine, left);
        await PublishAsync(engine, right);

        var gate = new ReadGate();
        gate.Arm(engine, enteredTarget: 2);
        var slots = new[]
        {
            ReadSlot.Start(engine, left.ArtId),
            ReadSlot.Start(engine, right.ArtId),
        };
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, gate.EnteredCount);
            Assert.Equal(2, engine.ArticleReadInFlightCount);
            Assert.Equal(2, engine.ArticleReadPhysicalReadCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            ReadSlot.JoinAll(slots);
        }

        Assert.Null(slots[0].Error);
        Assert.Null(slots[1].Error);
        Assert.True(slots[0].Found);
        Assert.True(slots[1].Found);
        AssertIdle(engine);
    }

    [Fact]
    public async Task SharedFailure_PropagatesToEveryWaiter()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<fail-cold@seg.test>", "fail-body\r\n");
        await PublishAsync(engine, record);
        var slots = await FailTogetherAsync(engine, record.ArtId, readers: 8);

        Assert.All(slots, slot => Assert.IsType<IOException>(slot.Error));
        Assert.All(slots, slot => Assert.Equal("injected", slot.Error!.Message));
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(7, engine.ArticleReadCoalescedCount);
        Assert.Equal(0, engine.SegmentArticleReadCount);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        AssertIdle(engine);
    }

    [Fact]
    public async Task FailedRead_RemovesInFlightEntry()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<fail-clear@seg.test>", "clear-body\r\n");
        await PublishAsync(engine, record);
        _ = await FailTogetherAsync(engine, record.ArtId, readers: 3);

        Assert.Equal(0, engine.ArticleReadInFlightCount);
        Assert.Equal(0, engine.ArticleReadWaiterCount);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
    }

    [Fact]
    public async Task RetryAfterFailure_StartsNewPhysicalRead()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<retry-cold@seg.test>", "retry-body\r\n");
        await PublishAsync(engine, record);
        _ = await FailTogetherAsync(engine, record.ArtId, readers: 4);
        AssertIdle(engine);

        engine.TestHookBeforeProvenSegmentRead = null;
        var retry = await ReadTogetherAsync(engine, record.ArtId, readers: 3);
        AssertSameArticle(record, retry);
        Assert.Equal(2, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(1, engine.SegmentArticleReadCount);
        AssertIdle(engine);
    }

    [Fact]
    public async Task CancellingOneWaiter_DoesNotCancelTheOthers()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<cancel-one@seg.test>", "cancel-one\r\n");
        await PublishAsync(engine, record);

        using var cancelled = new CancellationTokenSource();
        var gate = new ReadGate();
        gate.Arm(engine);
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.TestWhenPhysicalReadWaitersChanged = count =>
        {
            if (count >= 2)
            {
                enough.TrySetResult();
            }
        };

        var owner = ReadSlot.Start(engine, record.ArtId);
        ReadSlot? staying = null;
        ReadSlot? leaving = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            staying = ReadSlot.Start(engine, record.ArtId);
            leaving = ReadSlot.Start(engine, record.ArtId, cancelled.Token);
            await enough.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            Assert.True(leaving.Thread.Join(TimeSpan.FromSeconds(5)));
            Assert.IsAssignableFrom<OperationCanceledException>(leaving.Error);
            Assert.Equal(1, engine.ArticleReadInFlightCount);
            Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            ReadSlot.JoinAll(owner);
            if (staying is not null)
            {
                ReadSlot.JoinAll(staying);
            }
        }

        Assert.Null(owner.Error);
        Assert.Null(staying!.Error);
        Assert.True(owner.Found);
        Assert.True(staying.Found);
        Assert.True(owner.Result.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(staying.Result.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        AssertIdle(engine);
    }

    [Fact]
    public async Task OwnerCancellation_DoesNotCancelRemainingWaiters()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<cancel-owner@seg.test>", "cancel-owner\r\n");
        await PublishAsync(engine, record);

        using var ownerToken = new CancellationTokenSource();
        var gate = new ReadGate();
        gate.Arm(engine);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.TestWhenPhysicalReadWaitersChanged = count =>
        {
            if (count >= 1)
            {
                joined.TrySetResult();
            }
        };

        var owner = ReadSlot.Start(engine, record.ArtId, ownerToken.Token);
        ReadSlot? waiter = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            waiter = ReadSlot.Start(engine, record.ArtId);
            await joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ownerToken.Cancel();
            Assert.Equal(1, engine.ArticleReadInFlightCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            ReadSlot.JoinAll(owner);
            if (waiter is not null)
            {
                ReadSlot.JoinAll(waiter);
            }
        }

        Assert.IsAssignableFrom<OperationCanceledException>(owner.Error);
        Assert.Null(waiter!.Error);
        Assert.True(waiter.Found);
        Assert.True(waiter.Result.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(1, engine.SegmentArticleReadCount);
        AssertIdle(engine);
    }

    [Fact]
    public async Task AllWaitersCancelling_RemovesTheInFlightEntry()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<cancel-all@seg.test>", "cancel-all\r\n");
        await PublishAsync(engine, record);

        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var gate = new ReadGate();
        gate.Arm(engine);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.TestWhenPhysicalReadWaitersChanged = count =>
        {
            if (count >= 2)
            {
                joined.TrySetResult();
            }
        };

        var owner = ReadSlot.Start(engine, record.ArtId);
        ReadSlot? waiterA = null;
        ReadSlot? waiterB = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            waiterA = ReadSlot.Start(engine, record.ArtId, first.Token);
            waiterB = ReadSlot.Start(engine, record.ArtId, second.Token);
            await joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first.Cancel();
            second.Cancel();
            Assert.True(waiterA.Thread.Join(TimeSpan.FromSeconds(5)));
            Assert.True(waiterB.Thread.Join(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, engine.ArticleReadInFlightCount);
            Assert.Equal(0, engine.ArticleReadWaiterCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            ReadSlot.JoinAll(owner);
        }

        Assert.Null(owner.Error);
        Assert.True(owner.Found);
        Assert.IsAssignableFrom<OperationCanceledException>(waiterA!.Error);
        Assert.IsAssignableFrom<OperationCanceledException>(waiterB!.Error);
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        AssertIdle(engine);

        using var abandoned = new CancellationTokenSource();
        engine.TestHookAfterIndexSnapshotBeforeSegmentRead = (_, _) => abandoned.Cancel();
        Assert.Throws<OperationCanceledException>(() => engine.TryRead(record.ArtId, abandoned.Token, out _));
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        AssertIdle(engine);
    }

    [Fact]
    public async Task RamCache_StillServesACoherentArticle()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<cache-coherent@seg.test>", "cache-body\r\n");
        await PublishAsync(engine, record);
        engine.ArticleCache.Clear();

        var first = await ReadTogetherAsync(engine, record.ArtId, readers: 2);
        AssertSameArticle(record, first);
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(0, engine.CacheArticleReadCount);

        Assert.True(engine.TryRead(record.ArtId, out var cached));
        Assert.True(cached.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(record.ArtHash, cached.Metadata.ArtHash);
        Assert.Equal(1, engine.CacheArticleReadCount);
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(1, engine.ArticleReadCoalescedCount);
        AssertIdle(engine);
    }

    [Fact]
    public async Task CachedArticle_SkipsCoalescing()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = CreateRecord("<cache-skip@seg.test>", "cached\r\n");
        await PublishAsync(engine, record);

        Assert.True(engine.TryRead(record.ArtId, out var warmed));
        Assert.True(warmed.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, engine.CacheArticleReadCount);
        Assert.Equal(0, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(0, engine.ArticleReadCoalescedCount);

        Assert.True(engine.TryRead(record.ArtId, out var cached));
        Assert.True(cached.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(record.ArtHash, cached.Metadata.ArtHash);
        Assert.Equal(2, engine.CacheArticleReadCount);
        Assert.Equal(0, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(0, engine.ArticleReadCoalescedCount);
        Assert.Equal(0, engine.ArticleReadInFlightCount);
    }

    [Fact]
    public async Task ConcurrentJournalReads_StayOffThePhysicalPath()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<journal-many@seg.test>", "journal-many\r\n");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        var slots = Enumerable.Range(0, 16).Select(_ => ReadSlot.Start(engine, record.ArtId)).ToArray();
        ReadSlot.JoinAll(slots);

        Assert.All(slots, slot =>
        {
            Assert.Null(slot.Error);
            Assert.True(slot.Found);
            Assert.True(slot.Result.ArtData.Span.SequenceEqual(record.ArtData.Span));
        });
        Assert.Equal(16, engine.JournalArticleReadCount);
        Assert.Equal(0, engine.SegmentArticleReadCount);
        Assert.Equal(0, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(0, engine.ArticleReadCoalescedCount);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        AssertIdle(engine);
    }

    [Fact]
    public async Task JournalOnlyArticle_IsReadableBeforeSata()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<journal-before@seg.test>", "before-sata\r\n");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, engine.JournalArticleReadCount);
        Assert.Equal(0, engine.ArticleReadPhysicalReadCount);
        AssertIdle(engine);
    }

    [Fact]
    public async Task JournalToPresent_ServesTheSameBytes()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<handoff@seg.test>", "handoff-body\r\n");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.TestHookAfterSataBeforePhysicalWritten = (_, _) =>
        {
            gate.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(engine.TryRead(record.ArtId, out var during));
        Assert.True(during.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(0, engine.ArticleReadPhysicalReadCount);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));

        release.TrySetResult();
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.True(engine.Index.TryGet(record.ArtId, out var published));
        Assert.Equal(ArticleStorageState.Present, published.State);
        Assert.True(engine.TryRead(record.ArtId, out var after));
        Assert.True(after.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(published.Location, after.Metadata.Location);
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(1, engine.SegmentArticleReadCount);
        AssertIdle(engine);
    }

    [Fact]
    public async Task ReadsDuringSataPersist_StayOnTheJournal()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<during-sata@seg.test>", "during-sata\r\n");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.TestHookAfterSataBeforePhysicalWritten = (_, _) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var slots = Enumerable.Range(0, 8).Select(_ => ReadSlot.Start(engine, record.ArtId)).ToArray();
        ReadSlot.JoinAll(slots);
        try
        {
            Assert.All(slots, slot =>
            {
                Assert.Null(slot.Error);
                Assert.True(slot.Found);
                Assert.True(slot.Result.ArtData.Span.SequenceEqual(record.ArtData.Span));
            });
            Assert.Equal(0, engine.ArticleReadPhysicalReadCount);
            Assert.Equal(0, engine.SegmentArticleReadCount);
            Assert.Equal(8, engine.JournalArticleReadCount);
            Assert.False(engine.Index.TryGet(record.ArtId, out _));
        }
        finally
        {
            release.TrySetResult();
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        AssertIdle(engine);
    }

    [Fact]
    public async Task ReadStartedBeforePresent_CompletesFromTheJournal()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<before-present@seg.test>", "before-present\r\n");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        Assert.True(engine.TryRead(record.ArtId, out var before));
        Assert.True(before.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(0, engine.ArticleReadPhysicalReadCount);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));

        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.True(engine.TryRead(record.ArtId, out var after));
        Assert.True(after.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(meta.Location, after.Metadata.Location);
    }

    [Fact]
    public async Task ReadAfterPresent_UsesThePublishedSegment()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<after-present@seg.test>", "after-present\r\n");
        await PublishAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);

        var reads = await ReadTogetherAsync(engine, record.ArtId, readers: 4);
        AssertSameArticle(record, reads);
        Assert.All(reads, read => Assert.Equal(meta.Location, read.Metadata.Location));
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(1, engine.SegmentArticleReadCount);
        Assert.Equal(0, engine.JournalArticleReadCount);
        AssertIdle(engine);
    }

    [Fact]
    public async Task RestartBeforeSata_StillReadsTheJournal()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<restart-coalesce@seg.test>", "restart-body\r\n");
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        restarted.SuspendBackgroundPersist = true;
        Assert.Equal(0, restarted.PhysicalAppendCount);
        Assert.True(restarted.TryRead(record.ArtId, out var journalRead));
        Assert.True(journalRead.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(0, restarted.ArticleReadPhysicalReadCount);
        Assert.Equal(0, restarted.ArticleReadInFlightCount);
        Assert.False(restarted.Index.TryGet(record.ArtId, out _));

        await restarted.RecoverAsync(CancellationToken.None);
        Assert.Empty(restarted.Journal.EnumerateIncomplete());
        Assert.True(restarted.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);

        var reads = await ReadTogetherAsync(restarted, record.ArtId, readers: 5);
        AssertSameArticle(record, reads);
        Assert.Equal(1, restarted.ArticleReadPhysicalReadCount);
        Assert.Equal(meta.Location, reads[0].Metadata.Location);
        AssertIdle(restarted);
    }

    [Fact]
    public async Task CheckpointWhileOutstanding_DoesNotChangeJournalReads()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<checkpoint-coalesce@seg.test>", "checkpoint-body\r\n");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        var checkpoint = Task.Run(() => engine.CheckpointTruncateCommitted());
        var slots = Enumerable.Range(0, 8).Select(_ => ReadSlot.Start(engine, record.ArtId)).ToArray();
        ReadSlot.JoinAll(slots);
        await checkpoint.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(slots, slot =>
        {
            Assert.Null(slot.Error);
            Assert.True(slot.Found);
            Assert.True(slot.Result.ArtData.Span.SequenceEqual(record.ArtData.Span));
        });
        Assert.Equal(0, engine.ArticleReadPhysicalReadCount);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out _));
        AssertIdle(engine);
    }

    [Fact]
    public async Task InFlightLimit_FallsBackToADirectRead()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.PhysicalReadCoalesceMaxInFlight = 1;
        var left = CreateRecord("<bound-left@seg.test>", "bound-left\r\n");
        var right = CreateRecord("<bound-right@seg.test>", "bound-right\r\n");
        await PublishAsync(engine, left);
        await PublishAsync(engine, right);

        var gate = new ReadGate();
        gate.Arm(engine, enteredTarget: 2);
        var first = ReadSlot.Start(engine, left.ArtId);
        ReadSlot? second = null;
        try
        {
            await WaitUntilAsync(() => gate.EnteredCount >= 1);
            second = ReadSlot.Start(engine, right.ArtId);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, engine.ArticleReadInFlightCount);
            Assert.Equal(2, engine.ArticleReadPhysicalReadCount);
            Assert.Equal(0, engine.ArticleReadCoalescedCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            ReadSlot.JoinAll(first);
            if (second is not null)
            {
                ReadSlot.JoinAll(second);
            }
        }

        Assert.Null(first.Error);
        Assert.Null(second!.Error);
        Assert.True(first.Result.ArtData.Span.SequenceEqual(left.ArtData.Span));
        Assert.True(second.Result.ArtData.Span.SequenceEqual(right.ArtData.Span));
        AssertIdle(engine);
    }

    [Fact]
    public async Task WaiterLimit_FallsBackToADirectRead()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.PhysicalReadCoalesceMaxWaiters = 1;
        var record = CreateRecord("<waiter-bound@seg.test>", "waiter-bound\r\n");
        await PublishAsync(engine, record);

        var gate = new ReadGate();
        gate.Arm(engine, enteredTarget: 2);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.TestWhenPhysicalReadWaitersChanged = count =>
        {
            if (count >= 1)
            {
                joined.TrySetResult();
            }
        };

        var owner = ReadSlot.Start(engine, record.ArtId);
        ReadSlot? waiter = null;
        ReadSlot? overflow = null;
        try
        {
            await WaitUntilAsync(() => gate.EnteredCount >= 1);
            waiter = ReadSlot.Start(engine, record.ArtId);
            await joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
            overflow = ReadSlot.Start(engine, record.ArtId);
            await WaitUntilAsync(() => gate.EnteredCount >= 2);
            Assert.Equal(1, engine.ArticleReadInFlightCount);
            Assert.Equal(1, engine.ArticleReadCoalescedCount);
            Assert.Equal(2, engine.ArticleReadPhysicalReadCount);
            Assert.Equal(1, engine.ArticleReadWaiterCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            ReadSlot.JoinAll(owner);
            if (waiter is not null)
            {
                ReadSlot.JoinAll(waiter);
            }

            if (overflow is not null)
            {
                ReadSlot.JoinAll(overflow);
            }
        }

        Assert.Null(owner.Error);
        Assert.Null(waiter!.Error);
        Assert.Null(overflow!.Error);
        Assert.True(owner.Result.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(waiter.Result.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(overflow.Result.ArtData.Span.SequenceEqual(record.ArtData.Span));
        AssertIdle(engine);
    }

    [Fact]
    public async Task Registry_IsEmptyAfterCompletionFailureAndCancellation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<registry-zero@seg.test>", "registry\r\n");
        await PublishAsync(engine, record);

        _ = await ReadTogetherAsync(engine, record.ArtId, readers: 4);
        AssertIdle(engine);

        _ = await FailTogetherAsync(engine, record.ArtId, readers: 3);
        AssertIdle(engine);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => engine.TryRead(record.ArtId, cancelled.Token, out _));
        AssertIdle(engine);
    }

    [Fact]
    public async Task FiveHundredColdReads_PerformOnePhysicalRead()
    {
        const int readers = 500;
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<five-hundred@seg.test>", "five-hundred\r\n");
        await PublishAsync(engine, record);

        var reads = await ReadTogetherAsync(engine, record.ArtId, readers);
        AssertSameArticle(record, reads);
        Assert.Equal(1, engine.ArticleReadPhysicalReadCount);
        Assert.Equal(readers - 1, engine.ArticleReadCoalescedCount);
        Assert.Equal(1, engine.SegmentArticleReadCount);
        Assert.Equal(readers - 1, engine.ArticleReadMaxObservedWaiters);
        AssertIdle(engine);
    }

    /// <summary>Publishes <paramref name="record"/> through the existing persist worker.</summary>
    private static async Task PublishAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
    }

    /// <summary>
    /// Blocks the owner inside the proven-segment read until every other reader has joined.
    /// </summary>
    private static async Task<ArticleReadResult[]> ReadTogetherAsync(
        FileArticleStorageEngine engine,
        ArticleId artId,
        int readers)
    {
        var gate = new ReadGate();
        gate.Arm(engine);
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.TestWhenPhysicalReadWaitersChanged = count =>
        {
            if (count >= readers - 1)
            {
                enough.TrySetResult();
            }
        };

        var slots = Enumerable.Range(0, readers).Select(_ => ReadSlot.Start(engine, artId)).ToArray();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (readers > 1)
            {
                await enough.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        finally
        {
            gate.Release.TrySetResult();
            engine.TestHookBeforeProvenSegmentRead = null;
            engine.TestWhenPhysicalReadWaitersChanged = null;
            ReadSlot.JoinAll(slots);
        }

        Assert.All(slots, slot => Assert.Null(slot.Error));
        Assert.All(slots, slot => Assert.True(slot.Found));
        return slots.Select(slot => slot.Result).ToArray();
    }

    /// <summary>Shares one injected <see cref="IOException"/> across <paramref name="readers"/>.</summary>
    private static async Task<ReadSlot[]> FailTogetherAsync(
        FileArticleStorageEngine engine,
        ArticleId artId,
        int readers)
    {
        var gate = new ReadGate();
        var release = gate.Release;
        engine.TestHookBeforeProvenSegmentRead = () =>
        {
            gate.MarkEntered();
            release.Task.GetAwaiter().GetResult();
            throw new IOException("injected");
        };

        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.TestWhenPhysicalReadWaitersChanged = count =>
        {
            if (count >= readers - 1)
            {
                enough.TrySetResult();
            }
        };

        var slots = Enumerable.Range(0, readers).Select(_ => ReadSlot.Start(engine, artId)).ToArray();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (readers > 1)
            {
                await enough.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        finally
        {
            release.TrySetResult();
            engine.TestHookBeforeProvenSegmentRead = null;
            engine.TestWhenPhysicalReadWaitersChanged = null;
            ReadSlot.JoinAll(slots);
        }

        return slots;
    }

    /// <summary>Asserts every read returned <paramref name="record"/> unchanged.</summary>
    private static void AssertSameArticle(ArticleRecord record, IReadOnlyList<ArticleReadResult> reads)
    {
        Assert.NotEmpty(reads);
        foreach (var read in reads)
        {
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
            Assert.Equal(record.ArtId, read.Metadata.ArtId);
            Assert.Equal(record.ArtHash, read.Metadata.ArtHash);
            Assert.Equal(record.ArtSize, read.Metadata.ArtSize);
        }
    }

    /// <summary>Asserts the in-flight registry and waiter gauge are empty.</summary>
    private static void AssertIdle(FileArticleStorageEngine engine)
    {
        Assert.Equal(0, engine.ArticleReadInFlightCount);
        Assert.Equal(0, engine.ArticleReadWaiterCount);
    }

    /// <summary>Polls <paramref name="condition"/> until it holds. The delay is only a safety bound.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > 5000)
            {
                throw new TimeoutException("Condition was not observed.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Builds a canonical article whose identity is <paramref name="messageId"/>.</summary>
    private static ArticleRecord CreateRecord(string messageId, string body)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: cold-read\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    /// <summary>Blocks inside the owner's proven-segment read.</summary>
    private sealed class ReadGate
    {
        /// <summary>Set once <see cref="EnteredCount"/> reaches the arm target.</summary>
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Releases every caller blocked in the hook.</summary>
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Times the hook has run.</summary>
        public int EnteredCount => Volatile.Read(ref _enteredCount);

        private int _enteredCount;
        private int _target = 1;

        /// <summary>Installs the blocking hook on <paramref name="engine"/>.</summary>
        public void Arm(FileArticleStorageEngine engine, int enteredTarget = 1)
        {
            _target = enteredTarget;
            engine.TestHookBeforeProvenSegmentRead = () =>
            {
                MarkEntered();
                Release.Task.GetAwaiter().GetResult();
            };
        }

        /// <summary>Records one entry into the proven-segment hook.</summary>
        public void MarkEntered()
        {
            var count = Interlocked.Increment(ref _enteredCount);
            if (count >= _target)
            {
                Entered.TrySetResult();
            }
        }
    }

    /// <summary>One reader thread and the outcome it observed.</summary>
    private sealed class ReadSlot
    {
        /// <summary>Reader thread.</summary>
        public Thread Thread { get; private set; } = null!;

        /// <summary>True when <see cref="FileArticleStorageEngine.TryRead(ArticleId, CancellationToken, out ArticleReadResult)"/> returned true.</summary>
        public bool Found { get; private set; }

        /// <summary>Article returned to this caller.</summary>
        public ArticleReadResult Result { get; private set; }

        /// <summary>Exception observed by this caller, including cancellation.</summary>
        public Exception? Error { get; private set; }

        /// <summary>Starts a dedicated reader so a blocked wait cannot exhaust the thread pool.</summary>
        public static ReadSlot Start(
            FileArticleStorageEngine engine,
            ArticleId artId,
            CancellationToken cancellationToken = default)
        {
            var slot = new ReadSlot();
            var thread = new Thread(
                () =>
                {
                    try
                    {
                        slot.Found = engine.TryRead(artId, cancellationToken, out var result);
                        slot.Result = result;
                    }
                    catch (Exception ex)
                    {
                        slot.Error = ex;
                    }
                },
                maxStackSize: 256 * 1024)
            {
                IsBackground = true,
                Name = "cold-read",
            };
            slot.Thread = thread;
            thread.Start();
            return slot;
        }

        /// <summary>Waits until every reader has left <see cref="FileArticleStorageEngine.TryRead(ArticleId, CancellationToken, out ArticleReadResult)"/>.</summary>
        public static void JoinAll(params ReadSlot[] slots)
        {
            foreach (var slot in slots)
            {
                Assert.True(slot.Thread.Join(TimeSpan.FromSeconds(15)), "Reader thread did not finish.");
            }
        }
    }

    /// <summary>Isolated control and segment directories.</summary>
    private sealed class TempStorageDir : IDisposable
    {
        private TempStorageDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        /// <summary>Directory removed on dispose.</summary>
        public string Root { get; }

        /// <summary>Engine open options for <see cref="Root"/>.</summary>
        public ArticleStorageRuntimeOptions Options { get; }

        /// <summary>Creates a unique temporary storage root.</summary>
        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-cold-read-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            var options = new ArticleStorageRuntimeOptions(
                ControlDir: control,
                SegmentDir: cache,
                JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
            return new TempStorageDir(root, options);
        }

        /// <inheritdoc />
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
