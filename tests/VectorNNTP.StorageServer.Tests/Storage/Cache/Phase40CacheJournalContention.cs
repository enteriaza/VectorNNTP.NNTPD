using System.Diagnostics;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Cache.Egress;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Tests.Storage.Cache;

/// <summary>
/// Phase 40 same-volume journal/cache contention harness.
/// Skipped unless VECTORNNTP_EGRESS_PHASE40=1. Does not change shipped defaults.
/// </summary>
public sealed class Phase40CacheJournalContention
{
    private const long JournalHard = 134217728;
    private const long JournalSoft = 67108864;
    private const long JournalCheckpoint = 33554432;
    private const long IndexCheckpoint = 134217728;
    private const int BodyBytes = 1024 * 1024;
    private const int LargeBodyBytes = 4 * 1024 * 1024;
    private const int Batch = 24;

    private readonly StringBuilder _log = new();
    private readonly Dictionary<int, string> _bodies = [];
    private double _cacheBytesPerSec;

    [Fact]
    public async Task Measure()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("VECTORNNTP_EGRESS_PHASE40"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var root = RepoRoot();
        var outFile = Path.Combine(root, ".artifacts", "storage-egress-cache", "phase40-run.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        using var workspace = Workspace.Create();
        try
        {
            Log($"host={Environment.MachineName} os={Environment.OSVersion} processors={Environment.ProcessorCount} framework={Environment.Version}");
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(workspace.Root))!);
            Log($"volume={drive.Name} format={drive.DriveFormat} total={drive.TotalSize} free={drive.AvailableFreeSpace}");
            Log("topology=NON_PRODUCTION_EQUIVALENT same-volume contention; ControlDir and segment directory are both this volume. Not NVMe-vs-SATA.");
            Log("cache_slab_write=FileStream.Write + Flush(flushToDisk: false); journal durability=Flush(flushToDisk: true) via IndexCommittedProbe acceptFlushedFlush");
            Log($"journal_soft={JournalSoft} journal_hard={JournalHard} max_article={ArticleResourceLimits.MaxArticleBytes}");
            var sample = Create(8, "size", BodyBytes);
            LogSizes("article_1MiB_class", sample);
            LogSizes("article_4MiB_class", Create(4, "size4", LargeBodyBytes));

            await JournalOnlyAsync(workspace.Dir("journal"), BodyBytes, Batch, "A_journal_1MiB");
            await PressureClimbAsync(workspace.Dir("pressure"));
            var cacheRate = await CacheOnlyAsync(workspace.Dir("cache"), BodyBytes, Batch, "B_cache_1MiB");
            _cacheBytesPerSec = cacheRate;
            var lowPace = PaceMs(cacheRate, 0.25, Nominal(sample[0]));
            var medPace = PaceMs(cacheRate, 0.50, Nominal(sample[0]));
            Log($"pace_from_cache_rate_Bps={cacheRate:F0} low_ms={lowPace} medium_ms={medPace} high_ms=0 fraction_of_unpaced_fill_rate");
            await CombinedAsync(workspace.Dir("low"), BodyBytes, Batch, lowPace, "C_low");
            await CombinedAsync(workspace.Dir("medium"), BodyBytes, Batch, medPace, "C_medium");
            await CombinedAsync(workspace.Dir("high"), BodyBytes, Batch, 0, "C_high");
            await JournalOnlyAsync(workspace.Dir("journal4"), LargeBodyBytes, 8, "A_journal_4MiB");
            await CombinedAsync(workspace.Dir("high4"), LargeBodyBytes, 8, 0, "C_high_4MiB");
            await EvictIngressAsync(workspace.Dir("evict"));
            await QueueSaturationAsync(workspace.Dir("queue"));
            await HotDuringPopulateAsync(workspace.Dir("hot"));
            await CheckpointDuringPopulateAsync(workspace.Dir("checkpoint"));
            await DegradedDuringJournalAsync(workspace.Dir("degraded"));
        }
        finally
        {
            IndexCommittedProbe.Disarm();
            await File.WriteAllTextAsync(outFile, _log.ToString());
        }
    }

    private async Task JournalOnlyAsync(ArticleStorageRuntimeOptions options, int bodyBytes, int count, string label)
    {
        await using var engine = Open(options, egress: null);
        var records = Create(count, label, bodyBytes);
        var flushes = engine.Journal.DurableFlushCount;
        IndexCommittedProbe.Arm(8);
        var cpu = Cpu();
        var wall = Stopwatch.GetTimestamp();
        var samples = await AcceptLoopAsync(engine, records);
        var wallUs = Micros(wall);
        var cpuMs = CpuMs(cpu);
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        var pressure = engine.GetWritePressure();
        var drain = await TimeDrainAsync(engine);
        var checkpoint = Time(() => engine.CheckpointTruncateCommitted());
        var index = Time(() => engine.CheckpointIndex());
        Log($"JOURNAL_ONLY {label} NON_PRODUCTION_EQUIVALENT");
        LogAccept(samples, wallUs, records);
        Log($"  outstanding_after_accepts={outstanding} pressure={pressure} flushes={engine.Journal.DurableFlushCount - flushes}");
        Log($"  drain_us={drain.Us:F1} drain_Bps={Rate(Sum(records), drain.Us):F0}");
        Log($"  checkpoint_us={checkpoint.Micros:F1} released={checkpoint.Value} checkpoint_Bps={Rate(checkpoint.Value, checkpoint.Micros):F0} index_checkpoint_us={index.Micros:F1}");
        Log($"  cpu_ms={cpuMs:F1} ws={WorkingSet()}");
        LogProbe(label);
        IndexCommittedProbe.Disarm();
    }

    private async Task PressureClimbAsync(ArticleStorageRuntimeOptions options)
    {
        await using var engine = Open(options, egress: null);
        engine.SuspendBackgroundPersist = true;
        IndexCommittedProbe.Arm(8);
        var samples = new List<double>();
        var accepted = 0;
        var rejected = 0;
        long outstandingAtSoft = -1;
        long outstandingAtReject = -1;
        var pressure = StorageWritePressure.Normal;
        var wall = Stopwatch.GetTimestamp();
        for (var i = 0; i < 220; i++)
        {
            var record = Create(1, "pressure-" + i, BodyBytes)[0];
            var start = Stopwatch.GetTimestamp();
            var accept = await engine.AcceptAsync(record, CancellationToken.None);
            samples.Add(Micros(start));
            var now = engine.GetWritePressure();
            var outstanding = engine.Journal.OutstandingRecoverableBytes;
            if (pressure != StorageWritePressure.Elevated && now == StorageWritePressure.Elevated)
            {
                outstandingAtSoft = outstanding;
            }

            pressure = now;
            if (accept.Outcome == ArticleAcceptOutcome.Accepted)
            {
                accepted++;
                continue;
            }

            rejected++;
            outstandingAtReject = outstanding;
            Assert.Equal(ArticleAcceptOutcome.RejectedPressure, accept.Outcome);
            break;
        }

        var wallUs = Micros(wall);
        Log("PRESSURE cache_disabled persist_suspended NON_PRODUCTION_EQUIVALENT");
        Log($"  accepted={accepted} rejected={rejected} pressure_at_stop={pressure} outstanding_at_elevated={outstandingAtSoft} outstanding_at_reject={outstandingAtReject}");
        Log($"  soft={JournalSoft} hard={JournalHard}");
        LogStats("  accept_us", samples);
        Log($"  wall_us={wallUs:F1} wall_includes_canonicalization=true");
        LogProbe("pressure");
        IndexCommittedProbe.Disarm();
        engine.SuspendBackgroundPersist = false;
        var drain = await TimeDrainAsync(engine);
        var checkpoint = Time(() => engine.CheckpointTruncateCommitted());
        Log($"  after_resume drain_us={drain.Us:F1} pressure={engine.GetWritePressure()} outstanding={engine.Journal.OutstandingRecoverableBytes} checkpoint_us={checkpoint.Micros:F1} released={checkpoint.Value}");
        Assert.Equal(1, rejected);
        Assert.Equal(StorageWritePressure.Normal, engine.GetWritePressure());
    }

    private async Task<double> CacheOnlyAsync(ArticleStorageRuntimeOptions options, int bodyBytes, int count, string label)
    {
        await using var engine = Open(options, Start(options.ControlDir, 512L * 1024 * 1024));
        var records = Create(count, label, bodyBytes);
        await AcceptAndDrainAsync(engine, records);
        var ids = records.Select(static record => record.ArtId).ToArray();
        Sight(engine, ids);
        var pop = engine.EgressCachePopulateCount;
        var cpu = Cpu();
        var ws = WorkingSet();
        var pumped = await PumpAsync(engine, ids, paceMs: 0);
        var cpuMs = CpuMs(cpu);
        var written = (engine.EgressCachePopulateCount - pop) * Nominal(records[0]);
        var rate = Rate(written, pumped.Us);
        Log($"CACHE_ONLY {label} NON_PRODUCTION_EQUIVALENT");
        Log($"  articles={count} nominal_record={Nominal(records[0])} written_est={written} occupancy={engine.EgressCacheBytes} entries={engine.EgressCacheEntryCount}");
        Log($"  pump_us={pumped.Us:F1} max_depth={pumped.MaxDepth} populate_delta={engine.EgressCachePopulateCount - pop} dropped={engine.EgressCachePopulateDroppedCount} evict={engine.EgressCacheEvictionCount} rejected={engine.EgressCacheAdmissionRejectedCount}");
        Log($"  cache_Bps={rate:F0} cpu_ms={cpuMs:F1} ws_delta={WorkingSet() - ws}");
        Log($"  corruption={engine.EgressCacheCorruptionCount}");
        return rate;
    }

    private async Task CombinedAsync(ArticleStorageRuntimeOptions options, int bodyBytes, int count, int paceMs, string label)
    {
        await using var engine = Open(options, Start(options.ControlDir, 512L * 1024 * 1024));
        var warm = Create(count, label + "-warm", bodyBytes);
        await AcceptAndDrainAsync(engine, warm);
        var ids = warm.Select(static record => record.ArtId).ToArray();
        Sight(engine, ids);
        var fresh = Create(count, label + "-accept", bodyBytes);
        var pop = engine.EgressCachePopulateCount;
        var flushes = engine.Journal.DurableFlushCount;
        IndexCommittedProbe.Arm(8);
        var cpu = Cpu();
        var ws = WorkingSet();
        var wall = Stopwatch.GetTimestamp();
        var acceptTask = Task.Run(() => AcceptLoopAsync(engine, fresh));
        var pumped = await PumpAsync(engine, ids, paceMs);
        var samples = await acceptTask;
        var wallUs = Micros(wall);
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        var cpuMs = CpuMs(cpu);
        var written = (engine.EgressCachePopulateCount - pop) * Nominal(warm[0]);
        Log($"COMBINED {label} pace_ms={paceMs} NON_PRODUCTION_EQUIVALENT");
        LogAccept(samples, wallUs, fresh);
        Log($"  cache_written_est={written} cache_Bps={Rate(written, pumped.Us):F0} pump_us={pumped.Us:F1} max_depth={pumped.MaxDepth} occupancy={engine.EgressCacheBytes}");
        Log($"  populate_delta={engine.EgressCachePopulateCount - pop} dropped={engine.EgressCachePopulateDroppedCount} evict={engine.EgressCacheEvictionCount} rejected={engine.EgressCacheAdmissionRejectedCount} corruption={engine.EgressCacheCorruptionCount}");
        Log($"  outstanding={outstanding} pressure={engine.GetWritePressure()} flushes={engine.Journal.DurableFlushCount - flushes}");
        Log($"  cpu_ms={cpuMs:F1} ws_delta={WorkingSet() - ws}");
        LogProbe(label);
        IndexCommittedProbe.Disarm();
        var checkpoint = Time(() => engine.CheckpointTruncateCommitted());
        Log($"  checkpoint_after_window_us={checkpoint.Micros:F1} released={checkpoint.Value} checkpoint_Bps={Rate(checkpoint.Value, checkpoint.Micros):F0}");
        Assert.All(samples, static sample => Assert.Equal(ArticleAcceptOutcome.Accepted, sample.Outcome));
    }

    private async Task EvictIngressAsync(ArticleStorageRuntimeOptions options)
    {
        await using var engine = Open(options, Start(options.ControlDir, 8L * 1024 * 1024));
        var warm = Create(16, "evict", BodyBytes);
        await AcceptAndDrainAsync(engine, warm);
        var ids = warm.Select(static record => record.ArtId).ToArray();
        Sight(engine, ids);
        var fresh = Create(8, "evict-accept", BodyBytes);
        IndexCommittedProbe.Arm(8);
        var wall = Stopwatch.GetTimestamp();
        var acceptTask = Task.Run(() => AcceptLoopAsync(engine, fresh));
        var pumped = await PumpAsync(engine, ids, 0);
        var samples = await acceptTask;
        var readStart = Stopwatch.GetTimestamp();
        Assert.True(engine.TryRead(ids[0], out var read));
        var readUs = Micros(readStart);
        var checkpoint = Time(() => engine.CheckpointTruncateCommitted());
        var present = ids.Count(id => engine.Index.TryGet(id, out var meta) && meta.State == ArticleStorageState.Present);
        Log("EVICT capacity=8MiB articles=16 accepts=8 NON_PRODUCTION_EQUIVALENT");
        LogAccept(samples, Micros(wall), fresh);
        Log($"  pump_us={pumped.Us:F1} max_depth={pumped.MaxDepth} occupancy={engine.EgressCacheBytes} entries={engine.EgressCacheEntryCount} evict={engine.EgressCacheEvictionCount} rejected={engine.EgressCacheAdmissionRejectedCount} populate={engine.EgressCachePopulateCount}");
        Log($"  high={8L * 1024 * 1024 / 100 * 50} low={8L * 1024 * 1024 / 100 * 45}");
        Log($"  segment_read_us={readUs:F1} bytes_match={read.ArtData.Length > 0} present={present} pressure={engine.GetWritePressure()}");
        Log($"  checkpoint_us={checkpoint.Micros:F1} released={checkpoint.Value}");
        LogProbe("evict");
        IndexCommittedProbe.Disarm();
        Assert.Equal(16, present);
        Assert.All(samples, static sample => Assert.Equal(ArticleAcceptOutcome.Accepted, sample.Outcome));
    }

    private async Task QueueSaturationAsync(ArticleStorageRuntimeOptions options)
    {
        var blocked = Start(options.ControlDir, 64L * 1024 * 1024, injected: true, fillQueueDepth: 1, fillQueueMaxBytes: 1024);
        await using var engine = Open(options, blocked);
        var records = Create(8, "queue", BodyBytes);
        await AcceptAndDrainAsync(engine, records);
        var flushes = engine.Journal.DurableFlushCount;
        var reads = new List<double>();
        var maxDepth = 0;
        foreach (var record in records)
        {
            var start = Stopwatch.GetTimestamp();
            Assert.True(engine.TryRead(record.ArtId, out var read));
            Assert.True(engine.TryRead(record.ArtId, out _));
            reads.Add(Micros(start));
            maxDepth = Math.Max(maxDepth, engine.Egress.FillQueueDepth);
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        }

        await engine.Egress.DrainFillsAsync();
        IndexCommittedProbe.Arm(8);
        var accepts = await AcceptLoopAsync(engine, Create(4, "queue-accept", BodyBytes));
        Log("QUEUE byte_cap=1024 depth_cap=1 NON_PRODUCTION_EQUIVALENT");
        Log($"  max_depth={maxDepth} depth_after={engine.Egress.FillQueueDepth} populate={engine.EgressCachePopulateCount} dropped={engine.EgressCachePopulateDroppedCount}");
        LogStats("  reader_pair_us", reads);
        LogAccept(accepts, 0, []);
        Log($"  pressure={engine.GetWritePressure()} flushes={engine.Journal.DurableFlushCount - flushes}");
        LogProbe("queue");
        IndexCommittedProbe.Disarm();
        Assert.All(accepts, static sample => Assert.Equal(ArticleAcceptOutcome.Accepted, sample.Outcome));
        Assert.True(engine.EgressCachePopulateDroppedCount > 0);
        Assert.Equal(0, engine.EgressCachePopulateCount);
    }

    private async Task HotDuringPopulateAsync(ArticleStorageRuntimeOptions options)
    {
        await using var engine = Open(options, Start(options.ControlDir, 512L * 1024 * 1024));
        var records = Create(12, "hot", BodyBytes);
        await AcceptAndDrainAsync(engine, records);
        var hot = records.Take(4).Select(static record => record.ArtId).ToArray();
        var cold = records.Skip(4).Select(static record => record.ArtId).ToArray();
        Sight(engine, hot);
        await PumpAsync(engine, hot, 0);
        Sight(engine, cold);
        var physical = engine.ArticleReadPhysicalReadCount;
        var hitsBefore = engine.EgressCacheHitCount;
        var pop = engine.EgressCachePopulateCount;
        var acceptTask = Task.Run(() => AcceptLoopAsync(engine, Create(4, "hot-accept", BodyBytes)));
        var readers = TimeConcurrent(engine, hot[0], 8, 20);
        var pumped = await PumpAsync(engine, cold, 0);
        var readerSamples = await readers;
        var accepts = await acceptTask;
        var lookups = engine.EgressCacheHitCount + engine.EgressCacheMissCount;
        Log("HOT concurrent_hits_during_populate NON_PRODUCTION_EQUIVALENT");
        LogStats("  hit_or_shared_us", readerSamples);
        Log($"  hits={engine.EgressCacheHitCount - hitsBefore} lookups={lookups} hit_rate={(lookups == 0 ? 0 : engine.EgressCacheHitCount / (double)lookups):F4}");
        Log($"  physical_delta={engine.ArticleReadPhysicalReadCount - physical} coalesced={engine.ArticleReadCoalescedCount} populate_delta={engine.EgressCachePopulateCount - pop} corruption={engine.EgressCacheCorruptionCount}");
        Log($"  pump_us={pumped.Us:F1} max_depth={pumped.MaxDepth}");
        LogAccept(accepts, 0, []);
        Assert.Equal(0, engine.EgressCacheCorruptionCount);
        Assert.All(accepts, static sample => Assert.Equal(ArticleAcceptOutcome.Accepted, sample.Outcome));
    }

    private async Task CheckpointDuringPopulateAsync(ArticleStorageRuntimeOptions options)
    {
        await using var engine = Open(options, Start(options.ControlDir, 512L * 1024 * 1024));
        var warm = Create(Batch, "ck", BodyBytes);
        await AcceptAndDrainAsync(engine, warm);
        var ids = warm.Select(static record => record.ArtId).ToArray();
        Sight(engine, ids);
        var pump = PumpAsync(engine, ids, 0);
        var acceptTask = Task.Run(() => AcceptLoopAsync(engine, Create(8, "ck-accept", BodyBytes)));
        var checkpoint = Time(() => engine.CheckpointTruncateCommitted());
        var samples = await acceptTask;
        var pumped = await pump;
        Log("CHECKPOINT_DURING_POPULATE NON_PRODUCTION_EQUIVALENT");
        Log($"  checkpoint_us={checkpoint.Micros:F1} released={checkpoint.Value} occupancy={engine.EgressCacheBytes} cache_Bps={Rate((pumped.PopulateDelta) * Nominal(warm[0]), pumped.Us):F0}");
        Log($"  outstanding={engine.Journal.OutstandingRecoverableBytes} pressure={engine.GetWritePressure()} max_depth={pumped.MaxDepth}");
        LogAccept(samples, 0, []);
        Assert.All(samples, static sample => Assert.Equal(ArticleAcceptOutcome.Accepted, sample.Outcome));
    }

    private async Task DegradedDuringJournalAsync(ArticleStorageRuntimeOptions options)
    {
        var blockedPath = Path.Combine(options.ControlDir, "not-a-directory");
        await File.WriteAllTextAsync(blockedPath, "x");
        var disabledControl = Path.Combine(options.ControlDir, "off");
        var disabledSegment = Path.Combine(options.SegmentDir, "off");
        Directory.CreateDirectory(disabledControl);
        Directory.CreateDirectory(disabledSegment);
        await using var off = Open(
            options with { ControlDir = disabledControl, SegmentDir = disabledSegment },
            Start(blockedPath, 8L * 1024 * 1024));
        var offAccepts = await AcceptLoopAsync(off, Create(2, "cache-off", BodyBytes));
        Log($"DEGRADED cache_open_failed enabled={off.Egress.IsEnabled} NON_PRODUCTION_EQUIVALENT");
        LogAccept(offAccepts, 0, []);

        await using var engine = Open(options, Start(options.ControlDir, 64L * 1024 * 1024));
        var record = Create(1, "degraded", BodyBytes)[0];
        await AcceptAndDrainAsync(engine, [record]);
        Sight(engine, [record.ArtId]);
        await PumpAsync(engine, [record.ArtId], 0);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var stale = engine.Egress.TryCopy(record.ArtId, meta.Sequence + 1, meta.ArtHash, meta.ArtSize, out _);
        Assert.True(engine.TryRead(record.ArtId, out var afterStale));
        await PumpAsync(engine, [record.ArtId], 0);
        var slab = Directory.EnumerateFiles(Path.Combine(options.ControlDir, "egress", "live"), "slab-*.bin").Single();
        using (var stream = new FileStream(slab, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var bytes = new byte[stream.Length];
            _ = stream.Read(bytes, 0, bytes.Length);
            bytes[Math.Min(70, bytes.Length - 1)] ^= 0xFF;
            stream.Position = 0;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        var corruptOk = engine.TryRead(record.ArtId, out var corrupt) && corrupt.ArtData.Span.SequenceEqual(record.ArtData.Span);
        var accepts = await AcceptLoopAsync(engine, Create(2, "degraded-accept", BodyBytes));
        Log($"DEGRADED stale={stale} read_after_stale={afterStale.ArtData.Length} corrupt_fallback={corruptOk} corruption={engine.EgressCacheCorruptionCount} invalidation={engine.EgressCacheInvalidationCount} state={meta.State}");
        LogAccept(accepts, 0, []);

        var reserveControl = Path.Combine(options.ControlDir, "reserve");
        var reserveSegment = Path.Combine(options.SegmentDir, "reserve");
        Directory.CreateDirectory(reserveControl);
        Directory.CreateDirectory(reserveSegment);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(reserveControl))!).AvailableFreeSpace;
        await using var reserved = Open(
            options with { ControlDir = reserveControl, SegmentDir = reserveSegment },
            Start(reserveControl, 32L * 1024 * 1024, reserve: free));
        var reservedRecord = Create(1, "reserve", BodyBytes)[0];
        await AcceptAndDrainAsync(reserved, [reservedRecord]);
        Sight(reserved, [reservedRecord.ArtId]);
        await PumpAsync(reserved, [reservedRecord.ArtId], 0);
        var reserveRead = reserved.TryRead(reservedRecord.ArtId, out var reserveBytes);
        var reserveAccepts = await AcceptLoopAsync(reserved, Create(2, "reserve-accept", BodyBytes));
        var reserveCheckpoint = Time(() => reserved.CheckpointTruncateCommitted());
        Log($"DEGRADED reserve={free} populate={reserved.EgressCachePopulateCount} rejected={reserved.EgressCacheAdmissionRejectedCount} read={reserveRead} read_bytes={reserveBytes.ArtData.Length} checkpoint_us={reserveCheckpoint.Micros:F1} pressure={reserved.GetWritePressure()}");
        LogAccept(reserveAccepts, 0, []);
        Assert.All(offAccepts, static sample => Assert.Equal(ArticleAcceptOutcome.Accepted, sample.Outcome));
        Assert.All(accepts, static sample => Assert.Equal(ArticleAcceptOutcome.Accepted, sample.Outcome));
        Assert.All(reserveAccepts, static sample => Assert.Equal(ArticleAcceptOutcome.Accepted, sample.Outcome));
        Assert.False(off.Egress.IsEnabled);
        Assert.True(corruptOk);
        Assert.True(reserveRead);
        Assert.True(reserved.EgressCacheAdmissionRejectedCount > 0);
    }

    private static async Task<List<AcceptSample>> AcceptLoopAsync(FileArticleStorageEngine engine, IReadOnlyList<ArticleRecord> records)
    {
        var samples = new List<AcceptSample>(records.Count);
        foreach (var record in records)
        {
            var start = Stopwatch.GetTimestamp();
            var accept = await engine.AcceptAsync(record, CancellationToken.None);
            samples.Add(new AcceptSample(Micros(start), accept.Outcome, engine.Journal.OutstandingRecoverableBytes, engine.GetWritePressure()));
        }

        return samples;
    }

    private static async Task AcceptAndDrainAsync(FileArticleStorageEngine engine, IReadOnlyList<ArticleRecord> records)
    {
        foreach (var record in records)
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
    }

    private static void Sight(FileArticleStorageEngine engine, IReadOnlyList<ArticleId> ids)
    {
        foreach (var id in ids)
        {
            Assert.True(engine.TryRead(id, out _));
        }
    }

    private static async Task<Pump> PumpAsync(FileArticleStorageEngine engine, IReadOnlyList<ArticleId> ids, int paceMs)
    {
        var pop = engine.EgressCachePopulateCount;
        var maxDepth = 0;
        var start = Stopwatch.GetTimestamp();
        foreach (var id in ids)
        {
            Assert.True(engine.TryRead(id, out _));
            maxDepth = Math.Max(maxDepth, engine.Egress.FillQueueDepth);
            if (paceMs > 0)
            {
                await Task.Delay(paceMs);
            }
        }

        await engine.Egress.DrainFillsAsync();
        return new Pump(Micros(start), maxDepth, engine.EgressCachePopulateCount - pop);
    }

    private static async Task<List<double>> TimeConcurrent(FileArticleStorageEngine engine, ArticleId artId, int readers, int each)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, readers).Select(_ => Task.Run(() =>
        {
            gate.Task.GetAwaiter().GetResult();
            var local = new double[each];
            for (var i = 0; i < each; i++)
            {
                var start = Stopwatch.GetTimestamp();
                if (!engine.TryRead(artId, out ArticleReadResult _))
                {
                    local[i] = -1;
                }
                else
                {
                    local[i] = Micros(start);
                }
            }

            return local;
        })).ToArray();
        gate.TrySetResult();
        var all = await Task.WhenAll(tasks);
        return all.SelectMany(static sample => sample).Where(static sample => sample >= 0).ToList();
    }

    private static async Task<(double Us, long Outstanding)> TimeDrainAsync(FileArticleStorageEngine engine)
    {
        var start = Stopwatch.GetTimestamp();
        await engine.DrainPendingAsync(CancellationToken.None);
        return (Micros(start), engine.Journal.OutstandingRecoverableBytes);
    }

    private void LogAccept(IReadOnlyList<AcceptSample> samples, double wallUs, IReadOnlyList<ArticleRecord> records)
    {
        LogStats("  accept_us", samples.Select(static sample => sample.Us).ToList());
        var bytes = records.Count == 0 ? 0 : records.Sum(static record => (long)record.ArtSize);
        var accepted = samples.Count(static sample => sample.Outcome == ArticleAcceptOutcome.Accepted);
        Log($"  accepted={accepted} rejected={samples.Count - accepted} wall_us={wallUs:F1} accept_Bps={(wallUs <= 0 ? 0 : Rate(bytes, wallUs)):F0}");
        if (samples.Count > 0)
        {
            Log($"  outstanding_last={samples[^1].Outstanding} pressure_last={samples[^1].Pressure}");
            Log(Histogram(samples.Select(static sample => sample.Us)));
        }
    }

    private void LogProbe(string label)
    {
        var report = IndexCommittedProbe.Snapshot(0, 0);
        Log($"  PROBE {label} samples={report.AcceptGateSamples} flushed={report.AcceptFlushedSamples} overflow={report.AcceptGateOverflow} unit=ms");
        foreach (var name in new[] { "acceptFlushedFlush", "acceptDurableFlush", "acceptAppendOutsideFlush", "acceptTryAppend" })
        {
            var row = report.Components.FirstOrDefault(component => component.Name == name);
            if (row.Name is null)
            {
                continue;
            }

            Log($"  {name} n={row.Count} median={row.Median:F3} p90={row.P90:F3} p99={row.P99:F3} max={row.Max:F3} total={row.Total:F3}");
        }
    }

    private void LogSizes(string name, IReadOnlyList<ArticleRecord> records)
    {
        var sizes = records.Select(static record => (double)record.ArtSize).Order().ToArray();
        Log($"{name} n={sizes.Length} mean={sizes.Average():F0} p50={Pct(sizes, 50):F0} p95={Pct(sizes, 95):F0} max={sizes[^1]:F0}");
    }

    private void Log(string line) => _log.AppendLine(line);

    private void LogStats(string name, IReadOnlyList<double> samples)
    {
        if (samples.Count == 0)
        {
            Log($"{name} n=0");
            return;
        }

        var ordered = samples.Order().ToArray();
        Log($"{name} n={ordered.Length} mean={ordered.Average():F1} p50={Pct(ordered, 50):F1} p95={Pct(ordered, 95):F1} p99={Pct(ordered, 99):F1} max={ordered[^1]:F1}");
    }

    private static string Histogram(IEnumerable<double> microseconds)
    {
        var bins = new int[7];
        foreach (var sample in microseconds)
        {
            var ms = sample / 1000d;
            var index = ms < 1 ? 0 : ms < 2 ? 1 : ms < 5 ? 2 : ms < 10 ? 3 : ms < 20 ? 4 : ms < 50 ? 5 : 6;
            bins[index]++;
        }

        return $"  accept_hist_ms <1={bins[0]} 1-2={bins[1]} 2-5={bins[2]} 5-10={bins[3]} 10-20={bins[4]} 20-50={bins[5]} >=50={bins[6]}";
    }

    private static int PaceMs(double bytesPerSec, double fraction, long artBytes)
    {
        if (bytesPerSec <= 1 || fraction <= 0 || artBytes <= 0)
        {
            return 0;
        }

        var ms = artBytes / (bytesPerSec * fraction) * 1000d;
        if (ms < 1)
        {
            return 0;
        }

        return (int)Math.Min(ms, 2000);
    }

    private static long Nominal(ArticleRecord record) => EgressCacheRecordCodec.HeaderBytes + record.ArtSize;

    private static long Sum(IReadOnlyList<ArticleRecord> records) => records.Sum(static record => (long)record.ArtSize);

    private static double Rate(long bytes, double microseconds) => microseconds <= 0 ? 0 : bytes / microseconds * 1_000_000d;

    private static (double Micros, long Value) Time(Func<long> call)
    {
        var start = Stopwatch.GetTimestamp();
        var value = call();
        return (Micros(start), value);
    }

    private static double Micros(long start) => Stopwatch.GetElapsedTime(start).TotalMicroseconds;

    private static TimeSpan Cpu() => Process.GetCurrentProcess().TotalProcessorTime;

    private static double CpuMs(TimeSpan started) => (Process.GetCurrentProcess().TotalProcessorTime - started).TotalMilliseconds;

    private static long WorkingSet() => Process.GetCurrentProcess().WorkingSet64;

    private static double Pct(double[] ordered, int percentile)
    {
        var index = (int)Math.Ceiling(percentile / 100d * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static FileArticleStorageEngine Open(ArticleStorageRuntimeOptions options, EgressCacheStart? egress) =>
        FileArticleStorageEngine.Open(
            options,
            volumeProbe: null,
            articleCache: new ArticleMemoryCache(0),
            egressCache: egress);

    private static EgressCacheStart Start(
        string controlDir,
        long capacity,
        long reserve = 0,
        bool injected = false,
        int fillQueueDepth = 0,
        long fillQueueMaxBytes = 0) =>
        new()
        {
            ControlDir = controlDir,
            CapacityBytes = capacity,
            ReserveBytes = reserve,
            JournalHardLimitBytes = JournalHard,
            JournalCheckpointThresholdBytes = JournalCheckpoint,
            IndexCheckpointThresholdBytes = IndexCheckpoint,
            MaximumUtilizationPercent = 70,
            MaximumUsageCapacityPercent = 50,
            FreeCapacityPercent = 5,
            Space = injected ? new FixedSpace(1L << 40, 1L << 40) : null,
            FillQueueDepth = fillQueueDepth,
            FillQueueMaxBytes = fillQueueMaxBytes,
        };

    private ArticleRecord[] Create(int count, string prefix, int bodyBytes)
    {
        if (!_bodies.TryGetValue(bodyBytes, out var body))
        {
            body = WrappedBody(bodyBytes);
            _bodies[bodyBytes] = body;
        }

        var records = new ArticleRecord[count];
        for (var i = 0; i < count; i++)
        {
            records[i] = CreateRecord($"<{prefix}-{i}@phase40.test>", body);
        }

        return records;
    }

    private static string WrappedBody(int bodyBytes)
    {
        var remaining = bodyBytes;
        var builder = new StringBuilder(bodyBytes + bodyBytes / 900 * 2 + 2);
        while (remaining > 0)
        {
            var count = Math.Min(900, remaining);
            builder.Append('a', count);
            builder.Append("\r\n");
            remaining -= count;
        }

        return builder.ToString();
    }

    private static ArticleRecord CreateRecord(string messageId, string body)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder(body.Length + 256);
        builder.Append("Path: peer.example\r\n");
        builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        builder.Append("Newsgroups: alt.test\r\n");
        builder.Append("From: user@example.test\r\n");
        builder.Append("Subject: phase40\r\n");
        builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VectorNNTP.NNTPD.sln")))
            {
                return dir.FullName;
            }
        }

        return Path.GetTempPath();
    }

    private readonly record struct AcceptSample(double Us, ArticleAcceptOutcome Outcome, long Outstanding, StorageWritePressure Pressure);

    private readonly record struct Pump(double Us, int MaxDepth, long PopulateDelta);

    private sealed class FixedSpace(long available, long total) : IEgressVolumeSpace
    {
        public bool TryRead(out long availableBytes, out long totalBytes)
        {
            availableBytes = available;
            totalBytes = total;
            return true;
        }
    }

    private sealed class Workspace : IDisposable
    {
        private Workspace(string root) => Root = root;

        public string Root { get; }

        public static Workspace Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase40-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Workspace(root);
        }

        public ArticleStorageRuntimeOptions Dir(string name)
        {
            var control = Path.Combine(Root, name, "control");
            var segment = Path.Combine(Root, name, "segment");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(segment);
            return new ArticleStorageRuntimeOptions(
                control,
                segment,
                JournalSoft,
                JournalHard,
                ArticleStorageOptions.DefaultSegmentTargetSizeBytes,
                CapacityMaximumUtilization: 70,
                CapacityCompactionHeadroom: 10,
                CapacityMaximumUsageCapacity: 50,
                CapacityFreeCapacity: 5);
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
