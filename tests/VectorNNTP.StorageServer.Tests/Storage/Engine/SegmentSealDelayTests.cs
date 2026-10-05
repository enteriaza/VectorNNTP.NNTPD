using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Tests.Fixtures;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Active retention segments seal on the size target or on the maximum seal delay.
/// </summary>
public sealed class SegmentSealDelayTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset Start = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Default_maximum_seal_delay_is_one_minute()
    {
        var options = StorageServerTestOptions.CreateValid();
        Assert.Equal(ArticleStorageOptions.DefaultMaxSegmentSealDelay, options.Storage.MaxSegmentSealDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Storage.MaxSegmentSealDelay);

        var runtime = StorageServerRuntimeOptionsFactory.Create(options);
        Assert.Equal(TimeSpan.FromSeconds(60), runtime.Storage.MaxSegmentSealDelay);
    }

    [Fact]
    public async Task Configuration_override_is_honored()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.MaxSegmentSealDelay = TimeSpan.FromMinutes(5);
        var runtime = StorageServerRuntimeOptionsFactory.Create(options);
        Assert.Equal(TimeSpan.FromMinutes(5), runtime.Storage.MaxSegmentSealDelay);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageServer:Storage:MaxSegmentSealDelay"] = "00:05:00",
            })
            .Build();
        var bound = new StorageServerOptions();
        configuration.GetSection(StorageServerOptions.SectionName).Bind(bound);
        Assert.Equal(TimeSpan.FromMinutes(5), bound.Storage.MaxSegmentSealDelay);

        var delay = TimeSpan.FromMinutes(5);
        var time = new FakeTimeProvider(Start);
        using var dir = TempStorageDir.Create(delay, ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        await using var engine = Open(dir, time);
        await AcceptAndPersistAsync(engine, CreateRecord("<override@seg.test>", "override-body\r\n"));

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(1), engine.Segments.ActiveSegmentAge);
        Assert.Equal(SegmentState.Active, Single(engine, SegmentState.Active).State);
        Assert.Equal(0, engine.Segments.SegmentSealByAgeCount);

        time.Advance(TimeSpan.FromMinutes(4));
        await engine.Segments.AgeSealTask.WaitAsync(Safety);
        Assert.Equal(1, engine.Segments.SegmentSealByAgeCount);
        Assert.Equal(SegmentState.Closed, Single(engine, SegmentState.Closed).State);
    }

    [Fact]
    public void Negative_seal_delay_is_rejected()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.MaxSegmentSealDelay = TimeSpan.FromTicks(-1);
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static failure => failure.Contains("MaxSegmentSealDelay", StringComparison.Ordinal));

        options.Storage.MaxSegmentSealDelay = TimeSpan.Zero;
        Assert.False(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
    }

    [Fact]
    public async Task Zero_disables_age_sealing()
    {
        var time = new FakeTimeProvider(Start);
        using var dir = TempStorageDir.Create(TimeSpan.Zero, ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        await using var engine = Open(dir, time);
        var record = CreateRecord("<disabled@seg.test>", "disabled-body\r\n");
        await AcceptAndPersistAsync(engine, record);

        time.Advance(TimeSpan.FromDays(30));
        Assert.False(engine.Segments.TrySealActiveForAge(ActiveId(engine)));
        Assert.Equal(0, engine.Segments.SegmentSealByAgeCount);
        Assert.Equal(0, engine.Segments.SegmentSealBySizeCount);
        Assert.Null(engine.Segments.ActiveSegmentAge);
        Assert.Equal(SegmentState.Active, Single(engine, SegmentState.Active).State);
        Assert.False(File.Exists(ActivationPath(dir)));
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Size_rollover_still_seals_before_the_age_delay()
    {
        var delay = TimeSpan.FromHours(1);
        var time = new FakeTimeProvider(Start);
        using var dir = TempStorageDir.Create(delay, segmentTarget: 1);
        await using var engine = Open(dir, time);
        var first = CreateRecord("<size-1@seg.test>", "size-one\r\n");
        var second = CreateRecord("<size-2@seg.test>", "size-two\r\n");
        await AcceptAndPersistAsync(engine, first);
        var original = ActiveId(engine);
        await AcceptAndPersistAsync(engine, second);

        Assert.Equal(1, engine.Segments.SegmentSealBySizeCount);
        Assert.Equal(0, engine.Segments.SegmentSealByAgeCount);
        Assert.False(engine.Segments.TrySealActiveForAge(original));
        Assert.Equal(0, engine.Segments.SegmentSealByAgeCount);
        Assert.Equal(SegmentState.Closed, Info(engine, original).State);
        Assert.NotEqual(original, ActiveId(engine));
        Assert.Equal(1, Count(dir, "seg-*.closed"));
        AssertReadable(engine, first);
        AssertReadable(engine, second);
    }

    [Fact]
    public async Task Old_age_timer_does_not_seal_the_segment_opened_by_size_rollover()
    {
        var delay = TimeSpan.FromHours(1);
        var time = new FakeTimeProvider(Start);
        using var dir = TempStorageDir.Create(delay, segmentTarget: 1);
        await using var engine = Open(dir, time);
        await AcceptAndPersistAsync(engine, CreateRecord("<timer-a@seg.test>", "timer-a\r\n"));
        var original = ActiveId(engine);
        var originalTimer = engine.Segments.AgeSealTask;

        await AcceptAndPersistAsync(engine, CreateRecord("<timer-b@seg.test>", "timer-b\r\n"));
        await originalTimer.WaitAsync(Safety);

        var successor = ActiveId(engine);
        Assert.NotEqual(original, successor);
        Assert.Equal(1, engine.Segments.SegmentSealBySizeCount);
        Assert.Equal(0, engine.Segments.SegmentSealByAgeCount);
        Assert.False(engine.Segments.TrySealActiveForAge(original));
        Assert.Equal(SegmentState.Active, Info(engine, successor).State);
        Assert.Equal(1, Count(dir, "seg-*.closed"));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SegmentState.Active, Info(engine, successor).State);
        Assert.Equal(0, engine.Segments.SegmentSealByAgeCount);
    }

    [Fact]
    public async Task Small_segment_seals_when_the_age_delay_elapses()
    {
        await AssertAgeSealBelowTargetAsync(TimeSpan.FromSeconds(60), "age-seal");
    }

    [Fact]
    public async Task Segment_below_the_size_target_seals_because_of_age()
    {
        await AssertAgeSealBelowTargetAsync(TimeSpan.FromSeconds(15), "below-target");
    }

    [Fact]
    public async Task Empty_active_segment_is_not_sealed_when_the_delay_elapses()
    {
        var time = new FakeTimeProvider(Start);
        using var dir = TempStorageDir.Create(TimeSpan.FromSeconds(30), ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        await using var engine = Open(dir, time);
        var appender = await engine.Segments.GetActiveAppenderAsync(CancellationToken.None);
        var id = appender.SegmentId.Value;

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(engine.Segments.TrySealActiveForAge(id));
        Assert.Equal(0, engine.Segments.SegmentSealByAgeCount);
        Assert.Equal(0, engine.Segments.SegmentSealBySizeCount);
        Assert.Null(engine.Segments.ActiveSegmentAge);
        Assert.Equal(SegmentState.Active, Info(engine, id).State);
        Assert.Equal(0, Info(engine, id).SizeBytes);
        Assert.Equal(1, Count(dir, "seg-*.active"));
        Assert.Equal(0, Count(dir, "seg-*.closed"));
        Assert.False(File.Exists(ActivationPath(dir)));

        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.Equal(0, engine.Segments.SegmentSealByAgeCount);
        Assert.Equal(0, engine.Segments.SegmentSealBySizeCount);
    }

    [Fact]
    public async Task Age_seal_waits_until_the_in_progress_append_commits()
    {
        var delay = TimeSpan.FromHours(1);
        var time = new ManualTimeProvider(Start);
        using var dir = TempStorageDir.Create(delay, ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        await using var engine = Open(dir, time);
        var first = CreateRecord("<append-race-1@seg.test>", "append-one\r\n");
        var second = CreateRecord("<append-race-2@seg.test>", "append-two\r\n");
        await AcceptAndPersistAsync(engine, first);
        var segmentId = ActiveId(engine);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 0;
        engine.Segments.TestAfterWriteBeforeFlush = (_, _, _) =>
        {
            if (Interlocked.Exchange(ref armed, 1) != 0)
            {
                return;
            }

            time.Set(Start + delay);
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        var append = Task.Run(async () =>
        {
            await AcceptAndPersistAsync(engine, second);
        });

        try
        {
            await entered.Task.WaitAsync(Safety);
            var sealing = Task.Run(() => engine.Segments.TrySealActiveForAge(segmentId));
            release.TrySetResult();
            Assert.True(await sealing.WaitAsync(Safety));
            await append.WaitAsync(Safety);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(1, engine.Segments.SegmentSealByAgeCount);
        Assert.Equal(0, engine.Segments.SegmentSealBySizeCount);
        Assert.Equal(1, Count(dir, "seg-*.closed"));
        Assert.True(engine.Index.TryGet(first.ArtId, out var firstMeta));
        Assert.True(engine.Index.TryGet(second.ArtId, out var secondMeta));
        Assert.Equal(segmentId, firstMeta.Location.SegmentId.Value);
        Assert.Equal(segmentId, secondMeta.Location.SegmentId.Value);
        Assert.Equal(ArticleStorageState.Present, firstMeta.State);
        Assert.Equal(ArticleStorageState.Present, secondMeta.State);
        AssertReadable(engine, first);
        AssertReadable(engine, second);
    }

    [Fact]
    public async Task Size_rollover_and_age_seal_close_the_segment_once()
    {
        var delay = TimeSpan.FromHours(1);
        var time = new ManualTimeProvider(Start);
        using var dir = TempStorageDir.Create(delay, segmentTarget: 1);
        await using var engine = Open(dir, time);
        var first = CreateRecord("<both-1@seg.test>", "both-one\r\n");
        var second = CreateRecord("<both-2@seg.test>", "both-two\r\n");
        await AcceptAndPersistAsync(engine, first);
        var original = ActiveId(engine);
        time.Set(Start + delay);

        using var barrier = new Barrier(2);
        var seal = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return engine.Segments.TrySealActiveForAge(original);
        });
        var append = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            await AcceptAndPersistAsync(engine, second);
        });
        await Task.WhenAll(seal, append).WaitAsync(Safety);

        Assert.Equal(1, engine.Segments.SegmentSealByAgeCount + engine.Segments.SegmentSealBySizeCount);
        Assert.Equal(1, Count(dir, "seg-*.closed"));
        Assert.Equal(SegmentState.Closed, Info(engine, original).State);
        Assert.False(engine.Segments.TrySealActiveForAge(original));
        Assert.Equal(1, engine.Segments.SegmentSealByAgeCount + engine.Segments.SegmentSealBySizeCount);
        AssertReadable(engine, first);
        AssertReadable(engine, second);
    }

    [Fact]
    public async Task Stale_segment_id_does_not_seal_the_new_active_segment()
    {
        var delay = TimeSpan.FromMinutes(30);
        var time = new FakeTimeProvider(Start);
        using var dir = TempStorageDir.Create(delay, segmentTarget: 1);
        await using var engine = Open(dir, time);
        await AcceptAndPersistAsync(engine, CreateRecord("<stale-1@seg.test>", "stale-one\r\n"));
        var original = ActiveId(engine);
        time.Advance(delay);
        await engine.Segments.AgeSealTask.WaitAsync(Safety);
        Assert.Equal(1, engine.Segments.SegmentSealByAgeCount);

        await AcceptAndPersistAsync(engine, CreateRecord("<stale-2@seg.test>", "stale-two\r\n"));
        var successor = ActiveId(engine);
        Assert.NotEqual(original, successor);
        Assert.False(engine.Segments.TrySealActiveForAge(original));
        Assert.Equal(SegmentState.Closed, Info(engine, original).State);
        Assert.Equal(SegmentState.Active, Info(engine, successor).State);
        Assert.Equal(1, engine.Segments.SegmentSealByAgeCount);
        Assert.Equal(1, Count(dir, "seg-*.closed"));
    }

    [Fact]
    public async Task Repeated_age_seal_attempts_succeed_once()
    {
        var delay = TimeSpan.FromSeconds(45);
        var time = new FakeTimeProvider(Start);
        using var dir = TempStorageDir.Create(delay, ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        await using var engine = Open(dir, time);
        var record = CreateRecord("<once@seg.test>", "once-body\r\n");
        await AcceptAndPersistAsync(engine, record);
        var id = ActiveId(engine);

        time.SetUtcNow(Start + delay);
        await engine.Segments.AgeSealTask.WaitAsync(Safety);
        Assert.False(engine.Segments.TrySealActiveForAge(id));
        Assert.False(engine.Segments.TrySealActiveForAge(id));
        Assert.Equal(1, engine.Segments.SegmentSealByAgeCount);
        Assert.Equal(0, engine.Segments.SegmentSealBySizeCount);
        Assert.Equal(1, Count(dir, "seg-*.closed"));
        Assert.Equal(0, Count(dir, "seg-*.active"));
        AssertReadable(engine, record);
    }

    [Fact]
    public async Task Active_segment_age_continues_after_restart()
    {
        var delay = TimeSpan.FromMinutes(10);
        var elapsed = TimeSpan.FromMinutes(4);
        var time = new FakeTimeProvider(Start);
        using var dir = TempStorageDir.Create(delay, ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        ulong segmentId;
        long activationTicks;
        var record = CreateRecord("<young@seg.test>", "young-body\r\n");
        await using (var engine = Open(dir, time))
        {
            await AcceptAndPersistAsync(engine, record);
            segmentId = ActiveId(engine);
            time.Advance(elapsed);
            Assert.Equal(SegmentState.Active, Info(engine, segmentId).State);
            activationTicks = ReadActivation(dir).Ticks;
            Assert.Equal(Start.UtcTicks, activationTicks);
        }

        Assert.Equal(1, Count(dir, "seg-*.active"));
        Assert.Equal(0, Count(dir, "seg-*.closed"));

        var restartedTime = new FakeTimeProvider(Start + elapsed);
        await using var restarted = Open(dir, restartedTime);
        Assert.Equal(segmentId, ActiveId(restarted));
        Assert.Equal(activationTicks, ReadActivation(dir).Ticks);
        Assert.Equal(elapsed, restarted.Segments.ActiveSegmentAge);
        Assert.Equal(0, restarted.Segments.SegmentSealByAgeCount);
        Assert.Equal(SegmentState.Active, Info(restarted, segmentId).State);

        restartedTime.Advance(delay - elapsed);
        await restarted.Segments.AgeSealTask.WaitAsync(Safety);
        Assert.Equal(1, restarted.Segments.SegmentSealByAgeCount);
        Assert.Equal(SegmentState.Closed, Info(restarted, segmentId).State);
        Assert.Equal(0, restarted.PhysicalAppendCount);
        AssertReadable(restarted, record);
        Assert.Equal(1, Count(dir, "seg-*.closed"));
    }

    [Fact]
    public async Task Over_age_active_segment_seals_on_recovery()
    {
        var delay = TimeSpan.FromMinutes(10);
        var record = CreateRecord("<over-age@seg.test>", "over-age-body\r\n");
        using var dir = TempStorageDir.Create(delay, ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        StoredArticleLocation location;
        await using (var engine = Open(dir, new FakeTimeProvider(Start)))
        {
            await AcceptAndPersistAsync(engine, record);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            location = meta.Location;
        }

        await using var restarted = Open(dir, new FakeTimeProvider(Start + delay + TimeSpan.FromSeconds(1)));
        Assert.Equal(1, restarted.Segments.SegmentSealByAgeCount);
        Assert.Equal(0, restarted.Segments.SegmentSealBySizeCount);
        Assert.Equal(SegmentState.Closed, Info(restarted, location.SegmentId.Value).State);
        Assert.Equal(0, Count(dir, "seg-*.active"));
        Assert.Equal(1, Count(dir, "seg-*.closed"));
        Assert.False(File.Exists(ActivationPath(dir)));
        Assert.True(restarted.Index.TryGet(record.ArtId, out var recovered));
        Assert.Equal(ArticleStorageState.Present, recovered.State);
        Assert.Equal(location, recovered.Location);
        Assert.Equal(0, restarted.PhysicalAppendCount);
        AssertReadable(restarted, record);

        await restarted.RecoverAsync(CancellationToken.None);
        Assert.Equal(0, restarted.PhysicalAppendCount);
        Assert.Equal(1, Count(dir, "seg-*.closed"));
        AssertReadable(restarted, record);
    }

    [Fact]
    public async Task Restart_at_the_seal_deadline_keeps_one_copy()
    {
        var delay = TimeSpan.FromMinutes(10);
        var record = CreateRecord("<deadline@seg.test>", "deadline-body\r\n");
        using var dir = TempStorageDir.Create(delay, ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        var time = new FakeTimeProvider(Start);
        await using (var engine = Open(dir, time))
        {
            await AcceptAndPersistAsync(engine, record);
            time.Advance(delay - TimeSpan.FromMilliseconds(1));
            Assert.Equal(SegmentState.Active, Single(engine, SegmentState.Active).State);
            Assert.Equal(0, engine.Segments.SegmentSealByAgeCount);
        }

        Assert.Equal(1, Count(dir, "seg-*.active"));
        Assert.Equal(0, Count(dir, "seg-*.closed"));

        await using var restarted = Open(dir, new FakeTimeProvider(Start + delay));
        Assert.Equal(1, restarted.Segments.SegmentSealByAgeCount);
        Assert.Equal(1, Count(dir, "seg-*.closed"));
        Assert.Equal(0, Count(dir, "seg-*.active"));
        Assert.Equal(0, restarted.PhysicalAppendCount);
        Assert.Empty(restarted.Journal.EnumerateIncomplete());
        AssertReadable(restarted, record);

        await restarted.RecoverAsync(CancellationToken.None);
        Assert.Equal(0, restarted.PhysicalAppendCount);
        Assert.Equal(1, Count(dir, "seg-*"));
    }

    [Fact]
    public async Task Restart_with_the_clock_behind_activation_does_not_seal()
    {
        var delay = TimeSpan.FromMinutes(10);
        var record = CreateRecord("<behind@seg.test>", "behind-body\r\n");
        using var dir = TempStorageDir.Create(delay, ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        await using (var engine = Open(dir, new FakeTimeProvider(Start)))
        {
            await AcceptAndPersistAsync(engine, record);
        }

        var ticks = ReadActivation(dir).Ticks;
        var behind = new FakeTimeProvider(Start);
        behind.AdjustTime(Start - TimeSpan.FromDays(1));
        await using var restarted = Open(dir, behind);
        Assert.Equal(0, restarted.Segments.SegmentSealByAgeCount);
        Assert.Equal(SegmentState.Active, Single(restarted, SegmentState.Active).State);
        Assert.Equal(TimeSpan.Zero, restarted.Segments.ActiveSegmentAge);
        Assert.Equal(ticks, ReadActivation(dir).Ticks);
        Assert.False(restarted.Segments.TrySealActiveForAge(ActiveId(restarted)));
        Assert.Equal(1, Count(dir, "seg-*.active"));
        AssertReadable(restarted, record);
    }

    [Fact]
    public async Task Clock_behind_activation_seals_after_one_delay()
    {
        var delay = TimeSpan.FromMinutes(10);
        var record = CreateRecord("<behind-bound@seg.test>", "behind-bound\r\n");
        using var dir = TempStorageDir.Create(delay, ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        ulong segmentId;
        await using (var engine = Open(dir, new FakeTimeProvider(Start)))
        {
            await AcceptAndPersistAsync(engine, record);
            segmentId = ActiveId(engine);
        }

        var ticks = ReadActivation(dir).Ticks;
        var behind = new FakeTimeProvider(Start - TimeSpan.FromDays(1));
        await using var restarted = Open(dir, behind);
        Assert.Equal(segmentId, ActiveId(restarted));
        Assert.Equal(0, restarted.Segments.SegmentSealByAgeCount);
        Assert.Equal(ticks, ReadActivation(dir).Ticks);
        Assert.Equal(TimeSpan.Zero, restarted.Segments.ActiveSegmentAge);

        behind.Advance(delay - TimeSpan.FromMilliseconds(1));
        Assert.Equal(SegmentState.Active, Info(restarted, segmentId).State);
        Assert.Equal(0, restarted.Segments.SegmentSealByAgeCount);
        Assert.Equal(0, restarted.Segments.SegmentSealBySizeCount);

        behind.Advance(TimeSpan.FromMilliseconds(1));
        await restarted.Segments.AgeSealTask.WaitAsync(Safety);
        Assert.Equal(1, restarted.Segments.SegmentSealByAgeCount);
        Assert.Equal(0, restarted.Segments.SegmentSealBySizeCount);
        Assert.Equal(SegmentState.Closed, Info(restarted, segmentId).State);
        Assert.Equal(0, restarted.PhysicalAppendCount);
        Assert.False(File.Exists(ActivationPath(dir)));
        Assert.Equal(1, Count(dir, "seg-*.closed"));
        AssertReadable(restarted, record);
    }

    [Fact]
    public async Task Missing_activation_stamp_seals_without_duplicating_the_article()
    {
        var delay = TimeSpan.FromMinutes(10);
        var record = CreateRecord("<missing-stamp@seg.test>", "missing-stamp\r\n");
        using var dir = TempStorageDir.Create(delay, ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        StoredArticleLocation location;
        await using (var engine = Open(dir, new FakeTimeProvider(Start)))
        {
            await AcceptAndPersistAsync(engine, record);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            location = meta.Location;
        }

        File.Delete(ActivationPath(dir));
        await using var restarted = Open(dir, new FakeTimeProvider(Start));
        Assert.Equal(1, restarted.Segments.SegmentSealByAgeCount);
        Assert.Equal(SegmentState.Closed, Info(restarted, location.SegmentId.Value).State);
        Assert.Equal(1, Count(dir, "seg-*.closed"));
        Assert.Equal(0, restarted.PhysicalAppendCount);
        Assert.True(restarted.Index.TryGet(record.ArtId, out var recovered));
        Assert.Equal(location, recovered.Location);
        Assert.Equal(ArticleStorageState.Present, recovered.State);
        AssertReadable(restarted, record);
    }

    [Fact]
    public async Task Timed_seal_keeps_physical_written_before_present()
    {
        var delay = TimeSpan.FromSeconds(20);
        var time = new FakeTimeProvider(Start);
        using var dir = TempStorageDir.Create(delay, ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        await using var engine = Open(dir, time);
        var record = CreateRecord("<durability@seg.test>", "durable-body\r\n");
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);
        var appender = await engine.Segments.GetActiveAppenderAsync(CancellationToken.None);
        var location = await appender.AppendAsync(record.ArtData, CancellationToken.None);

        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        var beforeSeal = engine.Journal.EnumerateIncomplete();
        Assert.Single(beforeSeal);
        Assert.Null(beforeSeal[0].PhysicalWritten);

        time.SetUtcNow(Start + delay);
        await engine.Segments.AgeSealTask.WaitAsync(Safety);
        Assert.Equal(1, engine.Segments.SegmentSealByAgeCount);
        Assert.Equal(SegmentState.Closed, Info(engine, location.SegmentId.Value).State);
        Assert.Equal(location.Offset + location.Length, new FileInfo(ClosedPath(dir)).Length);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        var afterSeal = engine.Journal.EnumerateIncomplete();
        Assert.Single(afterSeal);
        Assert.Null(afterSeal[0].PhysicalWritten);
        Assert.Equal(accept.Sequence, afterSeal[0].Accept.Sequence);
        Assert.True(engine.TryRead(record.ArtId, out var journalRead));
        Assert.True(journalRead.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(engine.JournalArticleReadCount >= 1);

        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                CancellationToken.None));
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        var written = engine.Journal.EnumerateIncomplete();
        Assert.Single(written);
        Assert.NotNull(written[0].PhysicalWritten);
        Assert.Equal(location, written[0].PhysicalWritten!.Value.Location);

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.Index.TryGet(record.ArtId, out var present));
        Assert.Equal(ArticleStorageState.Present, present.State);
        Assert.Equal(location, present.Location);
        AssertReadable(engine, record);
        Assert.Equal(1, Count(dir, "seg-*.closed"));
    }

    [Fact]
    public async Task Age_seal_is_a_no_op_after_the_segment_is_closed_or_retired()
    {
        var delay = TimeSpan.FromSeconds(25);
        var time = new FakeTimeProvider(Start);
        using var dir = TempStorageDir.Create(delay, ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        await using var engine = Open(dir, time);
        var record = CreateRecord("<retired@seg.test>", "retired-body\r\n");
        await AcceptAndPersistAsync(engine, record);
        var id = ActiveId(engine);
        time.SetUtcNow(Start + delay);
        await engine.Segments.AgeSealTask.WaitAsync(Safety);
        Assert.Equal(1, engine.Segments.SegmentSealByAgeCount);
        Assert.False(engine.Segments.TrySealActiveForAge(id));

        Assert.True(engine.Segments.Catalogue.TryGet(new SegmentId(id), out var closed));
        Assert.Equal(SegmentState.Closed, closed.State);
        Assert.True(engine.Segments.Catalogue.TryRetire(closed.SegmentId, closed.Generation, time.GetUtcNow()));
        Assert.True(engine.Segments.Catalogue.TryGet(closed.SegmentId, out var retired));
        Assert.Equal(SegmentState.Retired, retired.State);
        Assert.False(engine.Segments.TrySealActiveForAge(id));
        Assert.Equal(1, engine.Segments.SegmentSealByAgeCount);
        Assert.Equal(0, engine.Segments.SegmentSealBySizeCount);
        Assert.Equal(1, Count(dir, "seg-*.retired"));
        Assert.Equal(0, Count(dir, "seg-*.closed"));
        Assert.Equal(0, Count(dir, "seg-*.active"));
    }

    private static async Task AssertAgeSealBelowTargetAsync(TimeSpan delay, string name)
    {
        var time = new FakeTimeProvider(Start);
        using var dir = TempStorageDir.Create(delay, ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        await using var engine = Open(dir, time);
        var record = CreateRecord($"<{name}@seg.test>", "small-body\r\n");
        await AcceptAndPersistAsync(engine, record);
        var info = Single(engine, SegmentState.Active);
        Assert.True(info.SizeBytes > 0);
        Assert.True(info.SizeBytes < dir.Options.SegmentTargetSizeBytes);
        Assert.Equal(ArticleStorageState.Present, Index(engine, record).State);
        Assert.Empty(engine.Journal.EnumerateIncomplete());

        time.Advance(delay);
        await engine.Segments.AgeSealTask.WaitAsync(Safety);

        var closed = Single(engine, SegmentState.Closed);
        Assert.Equal(info.SegmentId, closed.SegmentId);
        Assert.Equal(info.SizeBytes, closed.SizeBytes);
        Assert.True(closed.SizeBytes < dir.Options.SegmentTargetSizeBytes);
        Assert.Equal(1, engine.Segments.SegmentSealByAgeCount);
        Assert.Equal(0, engine.Segments.SegmentSealBySizeCount);
        Assert.Equal(ArticleStorageState.Present, Index(engine, record).State);
        Assert.Equal(info.SegmentId, Index(engine, record).Location.SegmentId);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(1, Count(dir, "seg-*.closed"));
        Assert.False(File.Exists(ActivationPath(dir)));
        AssertReadable(engine, record);
    }

    private static FileArticleStorageEngine Open(TempStorageDir dir, TimeProvider time)
    {
        var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        engine.SuspendBackgroundPersist = true;
        return engine;
    }

    private static async Task AcceptAndPersistAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.RecoverAsync(CancellationToken.None);
    }

    private static ulong ActiveId(FileArticleStorageEngine engine) =>
        Single(engine, SegmentState.Active).SegmentId.Value;

    private static SegmentInfo Single(FileArticleStorageEngine engine, SegmentState state) =>
        engine.Segments.Catalogue.Snapshot().Single(info => info.State == state);

    private static SegmentInfo Info(FileArticleStorageEngine engine, ulong segmentId)
    {
        Assert.True(engine.Segments.Catalogue.TryGet(new SegmentId(segmentId), out var info));
        return info;
    }

    private static StoredArticleMetadata Index(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        return meta;
    }

    private static void AssertReadable(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(record.ArtHash, read.Metadata.ArtHash);
        Assert.Equal(record.ArtSize, read.Metadata.ArtSize);
    }

    private static int Count(TempStorageDir dir, string pattern) =>
        Directory.EnumerateFiles(dir.Options.SegmentDir, pattern).Count();

    private static string ActivationPath(TempStorageDir dir) =>
        Path.Combine(dir.Options.SegmentDir, "segment-activation");

    private static string ClosedPath(TempStorageDir dir) =>
        Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.closed").Single();

    private static (ulong SegmentId, long Ticks) ReadActivation(TempStorageDir dir)
    {
        var bytes = File.ReadAllBytes(ActivationPath(dir));
        Assert.Equal(16, bytes.Length);
        return (
            BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)));
    }

    private static ArticleRecord CreateRecord(string messageId, string body)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: seal-delay\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    /// <summary>
    /// Wall clock the seal delay can observe without firing <see cref="Task.Delay(TimeSpan, TimeProvider)"/>.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _utcTicks;

        public ManualTimeProvider(DateTimeOffset utc) => _utcTicks = utc.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Volatile.Read(ref _utcTicks), TimeSpan.Zero);

        public void Set(DateTimeOffset utc) => Volatile.Write(ref _utcTicks, utc.UtcTicks);
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

        public static TempStorageDir Create(TimeSpan maxSegmentSealDelay, long segmentTarget)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-seal-delay-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            var options = new ArticleStorageRuntimeOptions(
                ControlDir: control,
                SegmentDir: cache,
                JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                SegmentTargetSizeBytes: segmentTarget,
                MaxSegmentSealDelay: maxSegmentSealDelay);
            return new TempStorageDir(root, options);
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
