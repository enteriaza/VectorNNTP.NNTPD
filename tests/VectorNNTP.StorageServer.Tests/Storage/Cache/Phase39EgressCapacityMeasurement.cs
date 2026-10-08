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
/// Phase 39 measurement harness. Skipped unless VECTORNNTP_EGRESS_PHASE39=1.
/// Does not change shipped cache defaults.
/// </summary>
public sealed class Phase39EgressCapacityMeasurement
{
    private const long JournalHard = 134217728;
    private const long JournalCheckpoint = 33554432;
    private const long IndexCheckpoint = 134217728;
    private const int BodyBytes = 16 * 1024;

    private readonly StringBuilder _log = new();

    [Fact]
    public async Task Measure()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("VECTORNNTP_EGRESS_PHASE39"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var root = RepoRoot();
        var outDir = Path.Combine(root, ".artifacts", "storage-egress-cache");
        Directory.CreateDirectory(outDir);
        var outFile = Path.Combine(outDir, "phase39-run.txt");
        using var workspace = Workspace.Create();
        try
        {
            Log($"host={Environment.MachineName}");
            Log($"os={Environment.OSVersion}");
            Log($"processor_count={Environment.ProcessorCount}");
            Log($"framework={Environment.Version}");
            Log($"control_root={Path.GetPathRoot(Path.GetFullPath(workspace.Control))}");
            Log($"segment_root={Path.GetPathRoot(Path.GetFullPath(workspace.Segment))}");
            var controlDrive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(workspace.Control))!);
            var segmentDrive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(workspace.Segment))!);
            Log($"same_volume={string.Equals(controlDrive.Name, segmentDrive.Name, StringComparison.OrdinalIgnoreCase)}");
            Log($"volume_name={controlDrive.Name} format={controlDrive.DriveFormat} total={controlDrive.TotalSize} free={controlDrive.AvailableFreeSpace}");
            Log($"topology=NON_PRODUCTION_EQUIVALENT single volume; no separate SATA device was visible to this process");
            var complement = controlDrive.TotalSize / 100 * 30 + controlDrive.TotalSize % 100 * 30 / 100;
            Log($"production_floor_bytes={complement + JournalHard + JournalCheckpoint + IndexCheckpoint} utilization_complement={complement} journal_hard={JournalHard} journal_checkpoint={JournalCheckpoint} index_checkpoint={IndexCheckpoint}");
            Log("accept_timer=AcceptAsync durable append+flush combined; separate append and flush timers are not exposed");

            await BaselineAsync(workspace.Dir("baseline-isolated"), isolatedJournal: true);
            await BaselineAsync(workspace.Dir("baseline-overlapped"), isolatedJournal: false);
            await RamBypassesEgressAsync(workspace.Dir("ram"));
            await CacheBenefitAsync(workspace.Dir("benefit"), capacity: 64L * 1024 * 1024, label: "64MiB");
            await ContentionAsync(workspace.Dir("contention-off"), capacity: 0, mode: "disabled");
            await ContentionAsync(workspace.Dir("contention-idle"), capacity: 64L * 1024 * 1024, mode: "idle");
            await ContentionAsync(workspace.Dir("contention-hits"), capacity: 64L * 1024 * 1024, mode: "hits");
            await ContentionAsync(workspace.Dir("contention-populate"), capacity: 64L * 1024 * 1024, mode: "populate");
            await MixedAsync(workspace.Dir("mixed"));
            await CapacityPointAsync(workspace.Dir("cap-small"), capacity: 2L * 1024 * 1024, articles: 80, label: "2MiB");
            await CapacityPointAsync(workspace.Dir("cap-medium"), capacity: 16L * 1024 * 1024, articles: 40, label: "16MiB");
            await CapacityPointAsync(workspace.Dir("cap-large"), capacity: 128L * 1024 * 1024, articles: 40, label: "128MiB");
            await ReserveSweepAsync(workspace.Dir("reserve"));
            await RealReserveDenialAsync(workspace.Dir("reserve-real"));
            await QueueBoundAsync(workspace.Dir("queue"));
            await MemoryAsync(workspace.Dir("memory"));
            await RestartAsync(workspace.Dir("restart"));
            await DegradedAsync(workspace.Dir("degraded"));
        }
        finally
        {
            await File.WriteAllTextAsync(outFile, _log.ToString());
        }
    }

    private async Task BaselineAsync(ArticleStorageRuntimeOptions options, bool isolatedJournal)
    {
        await using var engine = Open(options, egress: null, ramBytes: 0);
        var records = Create(24, isolatedJournal ? "base-iso" : "base-over");
        if (isolatedJournal)
        {
            engine.SuspendBackgroundPersist = true;
        }

        var accepts = new List<double>(records.Length);
        foreach (var record in records)
        {
            var start = Stopwatch.GetTimestamp();
            var accept = await engine.AcceptAsync(record, CancellationToken.None);
            accepts.Add(Micros(start));
            Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);
        }

        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        var pressure = engine.GetWritePressure();
        engine.SuspendBackgroundPersist = false;
        var drainStart = Stopwatch.GetTimestamp();
        await engine.DrainPendingAsync(CancellationToken.None);
        var drainUs = Micros(drainStart);
        var checkpointJournal = Time(() => engine.CheckpointTruncateCommitted());
        var checkpointIndex = Time(() => engine.CheckpointIndex());
        var beforeCold = Snapshot(engine);
        var cold = await TimeReadsAsync(engine, records, repeats: 1);
        var afterCold = Snapshot(engine);
        var hot = await TimeReadsAsync(engine, [records[0]], repeats: 200);
        var afterHot = Snapshot(engine);
        var concurrent = await TimeConcurrentAsync(engine, records[1].ArtId, readers: 8, each: 20);
        var afterConcurrent = Snapshot(engine);
        var bytes = records.Sum(static record => (long)record.ArtSize);
        Log($"BASELINE isolated={isolatedJournal} articles={records.Length} art_bytes={bytes}");
        LogStats("  accept_us", accepts);
        Log($"  outstanding_before_drain={outstanding} pressure_before_drain={pressure} pressure_after_drain={engine.GetWritePressure()}");
        Log($"  drain_us={drainUs:F1} drain_Bps={bytes / Math.Max(drainUs, 1) * 1_000_000:F0}");
        Log($"  checkpoint_journal_us={checkpointJournal.Micros:F1} released={checkpointJournal.Value}");
        Log($"  checkpoint_index_us={checkpointIndex.Micros:F1} covered={checkpointIndex.Value}");
        Log($"  before_reads {beforeCold}");
        LogStats("  cold_read_us", cold);
        Log($"  after_cold {afterCold}");
        LogStats("  repeat_same_us", hot);
        Log($"  after_repeat {afterHot}");
        LogStats("  concurrent_same_us", concurrent);
        Log($"  after_concurrent {afterConcurrent}");
        Log($"  appends={engine.PhysicalAppendCount}");
    }

    private async Task RamBypassesEgressAsync(ArticleStorageRuntimeOptions options)
    {
        await using var engine = Open(options, Start(options.ControlDir, 32L * 1024 * 1024), ramBytes: 4L * 1024 * 1024);
        var record = Create(1, "ram")[0];
        await PublishAsync(engine, record);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out _));
        await engine.Egress.DrainFillsAsync();
        Log($"RAM_BYPASS cache_hits={engine.CacheArticleReadCount} egress_hits={engine.EgressCacheHitCount} egress_populate={engine.EgressCachePopulateCount} segment_reads={engine.SegmentArticleReadCount}");
    }

    private async Task CacheBenefitAsync(ArticleStorageRuntimeOptions options, long capacity, string label)
    {
        await using var engine = Open(options, Start(options.ControlDir, capacity), ramBytes: 0);
        var records = Create(24, "benefit-" + label);
        foreach (var record in records)
        {
            await PublishAsync(engine, record);
        }

        var cold = await TimeReadsAsync(engine, records, repeats: 1);
        var second = await TimeReadsAsync(engine, records, repeats: 1);
        var maxDepth = engine.Egress.FillQueueDepth;
        await engine.Egress.DrainFillsAsync();
        var beforeSegment = engine.SegmentArticleReadCount;
        var beforePhysical = engine.ArticleReadPhysicalReadCount;
        var hits = await TimeReadsAsync(engine, [records[0]], repeats: 300);
        var concurrent = await TimeConcurrentAsync(engine, records[0].ArtId, readers: 8, each: 25);
        Log($"BENEFIT capacity={label} entries={engine.EgressCacheEntryCount} cache_bytes={engine.EgressCacheBytes}");
        LogStats("  first_read_us", cold);
        LogStats("  second_read_us", second);
        Log($"  populate={engine.EgressCachePopulateCount} dropped={engine.EgressCachePopulateDroppedCount} rejected={engine.EgressCacheAdmissionRejectedCount} max_depth_seen_after_second={maxDepth}");
        LogStats("  hit_us", hits);
        LogStats("  concurrent_hit_us", concurrent);
        Log($"  segment_reads_during_hits={engine.SegmentArticleReadCount - beforeSegment} physical_reads_during_hits={engine.ArticleReadPhysicalReadCount - beforePhysical}");
        Log($"  hit_count={engine.EgressCacheHitCount} miss_count={engine.EgressCacheMissCount} ram_hits={engine.CacheArticleReadCount} segment_reads={engine.SegmentArticleReadCount} physical_reads={engine.ArticleReadPhysicalReadCount}");
    }

    private async Task ContentionAsync(ArticleStorageRuntimeOptions options, long capacity, string mode)
    {
        await using var engine = Open(options, capacity > 0 ? Start(options.ControlDir, capacity) : null, ramBytes: 0);
        var warm = Create(8, "warm-" + mode);
        foreach (var record in warm)
        {
            await PublishAsync(engine, record);
        }

        if (mode == "hits")
        {
            foreach (var record in warm)
            {
                Assert.True(engine.TryRead(record.ArtId, out _));
                Assert.True(engine.TryRead(record.ArtId, out _));
            }

            await engine.Egress.DrainFillsAsync();
        }

        using var stop = new CancellationTokenSource();
        var filler = Task.CompletedTask;
        if (mode is "hits" or "populate")
        {
            filler = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    foreach (var record in warm)
                    {
                        _ = engine.TryRead(record.ArtId, out ArticleReadResult _);
                    }
                }
            });
        }

        var records = Create(24, "ingress-" + mode);
        var accepts = new List<double>(records.Length);
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.GetTimestamp();
        foreach (var record in records)
        {
            var start = Stopwatch.GetTimestamp();
            var accept = await engine.AcceptAsync(record, CancellationToken.None);
            accepts.Add(Micros(start));
            Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);
        }

        var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
        var wallMs = Micros(wall) / 1000;
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        var drainStart = Stopwatch.GetTimestamp();
        await engine.DrainPendingAsync(CancellationToken.None);
        var drainUs = Micros(drainStart);
        if (capacity > 0)
        {
            await engine.Egress.DrainFillsAsync();
        }

        var checkpoint = Time(() => engine.CheckpointTruncateCommitted());
        await stop.CancelAsync();
        await filler;
        var bytes = records.Sum(static record => (long)record.ArtSize);
        Log($"CONTENTION mode={mode} capacity={capacity}");
        LogStats("  accept_us", accepts);
        Log($"  outstanding_after_accepts={outstanding} pressure={engine.GetWritePressure()} drain_us={drainUs:F1} drain_Bps={bytes / Math.Max(drainUs, 1) * 1_000_000:F0}");
        Log($"  checkpoint_journal_us={checkpoint.Micros:F1} released={checkpoint.Value}");
        Log($"  cpu_ms={cpuMs:F1} wall_ms={wallMs:F1}");
        Log($"  populate={engine.EgressCachePopulateCount} dropped={engine.EgressCachePopulateDroppedCount} rejected={engine.EgressCacheAdmissionRejectedCount} evict={engine.EgressCacheEvictionCount} entries={engine.EgressCacheEntryCount} cache_bytes={engine.EgressCacheBytes} queue={engine.Egress.FillQueueDepth} hits={engine.EgressCacheHitCount}");
    }

    private async Task MixedAsync(ArticleStorageRuntimeOptions options)
    {
        await using var engine = Open(options, Start(options.ControlDir, 64L * 1024 * 1024), ramBytes: 0);
        var hot = Create(4, "mixed-hot");
        var cold = Create(12, "mixed-cold");
        foreach (var record in hot.Concat(cold))
        {
            await PublishAsync(engine, record);
        }

        var arrivals = Create(8, "mixed-arrive");
        var accepts = new List<double>(arrivals.Length);
        var ingress = Task.Run(async () =>
        {
            foreach (var record in arrivals)
            {
                var start = Stopwatch.GetTimestamp();
                var accept = await engine.AcceptAsync(record, CancellationToken.None);
                accepts.Add(Micros(start));
                Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);
            }
        });

        var coldSamples = await TimeReadsAsync(engine, cold, repeats: 1);
        var hotSamples = await TimeReadsAsync(engine, hot, repeats: 40);
        var concurrent = await TimeConcurrentAsync(engine, hot[0].ArtId, readers: 8, each: 10);
        await ingress;
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Egress.DrainFillsAsync();
        var lookups = engine.EgressCacheHitCount + engine.EgressCacheMissCount;
        Log("MIXED capacity=64MiB hot=4 cold=12 arrivals=8 readers=8");
        LogStats("  cold_us", coldSamples);
        LogStats("  hot_us", hotSamples);
        LogStats("  concurrent_us", concurrent);
        LogStats("  arrival_accept_us", accepts);
        Log($"  outstanding_after_arrivals={outstanding} pressure={engine.GetWritePressure()}");
        Log($"  hits={engine.EgressCacheHitCount} misses={engine.EgressCacheMissCount} hit_rate={(lookups == 0 ? 0 : engine.EgressCacheHitCount / (double)lookups):F4}");
        Log($"  populate={engine.EgressCachePopulateCount} dropped={engine.EgressCachePopulateDroppedCount} evict={engine.EgressCacheEvictionCount} entries={engine.EgressCacheEntryCount} cache_bytes={engine.EgressCacheBytes}");
        Log($"  segment_reads={engine.SegmentArticleReadCount} physical_reads={engine.ArticleReadPhysicalReadCount} coalesced={engine.ArticleReadCoalescedCount} ram_hits={engine.CacheArticleReadCount}");
    }

    private async Task CapacityPointAsync(ArticleStorageRuntimeOptions options, long capacity, int articles, string label)
    {
        await using var engine = Open(options, Start(options.ControlDir, capacity), ramBytes: 0);
        var records = Create(articles, "cap-" + label);
        long maxBytes = 0;
        var samples = new List<long>();
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            await PublishAsync(engine, record);
            Assert.True(engine.TryRead(record.ArtId, out _));
            Assert.True(engine.TryRead(record.ArtId, out _));
            if (i % 10 == 0)
            {
                await engine.Egress.DrainFillsAsync();
                samples.Add(engine.EgressCacheBytes);
                maxBytes = Math.Max(maxBytes, engine.EgressCacheBytes);
            }
        }

        await engine.Egress.DrainFillsAsync();
        maxBytes = Math.Max(maxBytes, engine.EgressCacheBytes);
        var present = 0;
        foreach (var record in records)
        {
            if (engine.Index.TryGet(record.ArtId, out var meta) && meta.State == ArticleStorageState.Present)
            {
                present++;
            }
        }

        var readStart = Stopwatch.GetTimestamp();
        Assert.True(engine.TryRead(records[0].ArtId, out _));
        var readUs = Micros(readStart);
        Log($"CAPACITY {label} capacity_bytes={capacity} articles={articles} present={present}");
        Log($"  final_bytes={engine.EgressCacheBytes} max_bytes_sampled={maxBytes} entries={engine.EgressCacheEntryCount}");
        Log($"  populate={engine.EgressCachePopulateCount} evict={engine.EgressCacheEvictionCount} rejected={engine.EgressCacheAdmissionRejectedCount} dropped={engine.EgressCachePopulateDroppedCount}");
        Log($"  occupancy_samples={string.Join(',', samples)} read_during_us={readUs:F1}");
        Log($"  high_watermark={capacity / 100 * 50} low_watermark={capacity / 100 * 45}");
    }

    private async Task ReserveSweepAsync(ArticleStorageRuntimeOptions options)
    {
        const long available = 20_000_000;
        const long total = 100_000_000;
        foreach (var reserve in new long[] { 0, 10_000_000, 19_000_000, 20_000_000, 50_000_000 })
        {
            var space = new FixedSpace(available, total);
            var start = Start(
                options.ControlDir,
                capacity: 8L * 1024 * 1024,
                reserve: reserve,
                journalHard: 0,
                journalCheckpoint: 0,
                indexCheckpoint: 0,
                utilization: 100,
                space: space,
                injectedSpace: true);
            await using var engine = Open(options, start, ramBytes: 0);
            var record = Create(1, "reserve-" + reserve)[0];
            await PublishAsync(engine, record);
            Assert.True(engine.TryRead(record.ArtId, out var read));
            Assert.True(engine.TryRead(record.ArtId, out _));
            await engine.Egress.DrainFillsAsync();
            var extra = Create(1, "reserve-accept-" + reserve)[0];
            var accept = await engine.AcceptAsync(extra, CancellationToken.None);
            Log($"RESERVE_INJECTED reserve={reserve} available={available} total={total} populate={engine.EgressCachePopulateCount} rejected={engine.EgressCacheAdmissionRejectedCount} entries={engine.EgressCacheEntryCount} read_bytes={read.ArtData.Length} accept={accept.Outcome} pressure={engine.GetWritePressure()}");
        }
    }

    private async Task RealReserveDenialAsync(ArticleStorageRuntimeOptions options)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(options.ControlDir))!;
        var available = new DriveInfo(root).AvailableFreeSpace;
        await using var engine = Open(
            options,
            Start(options.ControlDir, capacity: 32L * 1024 * 1024, reserve: available),
            ramBytes: 0);
        var record = Create(1, "reserve-real")[0];
        await PublishAsync(engine, record);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out _));
        await engine.Egress.DrainFillsAsync();
        var accept = await engine.AcceptAsync(Create(1, "reserve-real-accept")[0], CancellationToken.None);
        var checkpoint = Time(() => engine.CheckpointTruncateCommitted());
        Log($"RESERVE_REAL reserve={available} free_now={new DriveInfo(root).AvailableFreeSpace} populate={engine.EgressCachePopulateCount} rejected={engine.EgressCacheAdmissionRejectedCount} entries={engine.EgressCacheEntryCount} accept={accept.Outcome} pressure={engine.GetWritePressure()} checkpoint_us={checkpoint.Micros:F1} checkpoint_released={checkpoint.Value}");
    }

    private async Task QueueBoundAsync(ArticleStorageRuntimeOptions options)
    {
        var start = new EgressCacheStart
        {
            ControlDir = options.ControlDir,
            CapacityBytes = 32L * 1024 * 1024,
            ReserveBytes = 0,
            JournalHardLimitBytes = 0,
            JournalCheckpointThresholdBytes = 0,
            IndexCheckpointThresholdBytes = 0,
            MaximumUtilizationPercent = 100,
            MaximumUsageCapacityPercent = 50,
            FreeCapacityPercent = 5,
            Space = new FixedSpace(1L << 40, 1L << 40),
            FillQueueMaxBytes = 1,
            FillQueueDepth = 1,
        };
        await using var engine = Open(options, start, ramBytes: 0);
        var records = Create(8, "queue");
        foreach (var record in records)
        {
            await PublishAsync(engine, record);
        }

        var reads = new List<double>();
        foreach (var record in records)
        {
            var startTs = Stopwatch.GetTimestamp();
            Assert.True(engine.TryRead(record.ArtId, out _));
            Assert.True(engine.TryRead(record.ArtId, out _));
            reads.Add(Micros(startTs));
        }

        await engine.Egress.DrainFillsAsync();
        Log($"QUEUE cap_entries=1 cap_bytes=1 depth_after={engine.Egress.FillQueueDepth} queued_bytes={engine.Egress.FillQueueBytes}");
        Log($"  populate={engine.EgressCachePopulateCount} dropped={engine.EgressCachePopulateDroppedCount}");
        LogStats("  reader_pair_us", reads);

        var depthControl = Path.Combine(options.ControlDir, "depth");
        var depthSegment = Path.Combine(options.SegmentDir, "depth");
        Directory.CreateDirectory(depthControl);
        Directory.CreateDirectory(depthSegment);
        var depthStart = new EgressCacheStart
        {
            ControlDir = depthControl,
            CapacityBytes = 32L * 1024 * 1024,
            ReserveBytes = 0,
            JournalHardLimitBytes = 0,
            JournalCheckpointThresholdBytes = 0,
            IndexCheckpointThresholdBytes = 0,
            MaximumUtilizationPercent = 100,
            MaximumUsageCapacityPercent = 50,
            FreeCapacityPercent = 5,
            Space = new FixedSpace(1L << 40, 1L << 40),
            FillQueueDepth = 1,
        };
        await using var depth = Open(options with { ControlDir = depthControl, SegmentDir = depthSegment }, depthStart, ramBytes: 0);
        var depthRecords = Create(32, "depth");
        foreach (var record in depthRecords)
        {
            await PublishAsync(depth, record);
        }

        var maxDepth = 0;
        foreach (var record in depthRecords)
        {
            Assert.True(depth.TryRead(record.ArtId, out _));
            Assert.True(depth.TryRead(record.ArtId, out _));
            maxDepth = Math.Max(maxDepth, depth.Egress.FillQueueDepth);
        }

        await depth.Egress.DrainFillsAsync();
        Log($"QUEUE_DEPTH cap_entries=1 sampled_max_depth={maxDepth} depth_after={depth.Egress.FillQueueDepth} populate={depth.EgressCachePopulateCount} dropped={depth.EgressCachePopulateDroppedCount}");

        var burstControl = Path.Combine(options.ControlDir, "burst");
        var burstSegment = Path.Combine(options.SegmentDir, "burst");
        Directory.CreateDirectory(burstControl);
        Directory.CreateDirectory(burstSegment);
        await using var burst = Open(
            options with { ControlDir = burstControl, SegmentDir = burstSegment },
            Start(burstControl, 32L * 1024 * 1024),
            ramBytes: 0);
        var one = Create(1, "burst")[0];
        await PublishAsync(burst, one);
        var physicalBefore = burst.ArticleReadPhysicalReadCount;
        var readers = await TimeConcurrentAsync(burst, one.ArtId, readers: 16, each: 1);
        await burst.Egress.DrainFillsAsync();
        Log($"BURST physical_reads={burst.ArticleReadPhysicalReadCount - physicalBefore} populate={burst.EgressCachePopulateCount} coalesced={burst.ArticleReadCoalescedCount}");
        LogStats("  burst_us", readers);
    }

    private async Task MemoryAsync(ArticleStorageRuntimeOptions options)
    {
        foreach (var count in new[] { 100, 300 })
        {
            var control = Path.Combine(options.ControlDir, count.ToString());
            var segment = Path.Combine(options.SegmentDir, count.ToString());
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(segment);
            var scoped = options with { ControlDir = control, SegmentDir = segment };
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
            var wsBefore = Process.GetCurrentProcess().WorkingSet64;
            await using var engine = Open(scoped, Start(control, 64L * 1024 * 1024), ramBytes: 0);
            var records = Create(count, "mem-" + count, bodyBytes: 256);
            foreach (var record in records)
            {
                await PublishAsync(engine, record);
                Assert.True(engine.TryRead(record.ArtId, out _));
                Assert.True(engine.TryRead(record.ArtId, out _));
            }

            await engine.Egress.DrainFillsAsync();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
            var wsAfter = Process.GetCurrentProcess().WorkingSet64;
            var entries = Math.Max(1, engine.EgressCacheEntryCount);
            Log($"MEMORY articles={count} entries={engine.EgressCacheEntryCount} cache_bytes={engine.EgressCacheBytes}");
            Log($"  heap_before={heapBefore} heap_after={heapAfter} heap_delta={heapAfter - heapBefore} heap_per_entry={(heapAfter - heapBefore) / entries}");
            Log($"  ws_before={wsBefore} ws_after={wsAfter} ws_delta={wsAfter - wsBefore} ws_per_entry={(wsAfter - wsBefore) / entries}");
        }
    }

    private async Task RestartAsync(ArticleStorageRuntimeOptions options)
    {
        var record = Create(1, "restart")[0];
        var emptyStart = Stopwatch.GetTimestamp();
        await using (var engine = Open(options, Start(options.ControlDir, 16L * 1024 * 1024), ramBytes: 0))
        {
            Log($"RESTART empty_open_us={Micros(emptyStart):F1} entries={engine.EgressCacheEntryCount}");
            await PublishAsync(engine, record);
            Assert.True(engine.TryRead(record.ArtId, out _));
            Assert.True(engine.TryRead(record.ArtId, out _));
            await engine.Egress.DrainFillsAsync();
            Log($"  populated_entries={engine.EgressCacheEntryCount} cache_bytes={engine.EgressCacheBytes}");
        }

        var live = Path.Combine(options.ControlDir, "egress", "live");
        File.WriteAllBytes(Path.Combine(live, "partial.bin"), [1, 2, 3, 4, 5]);
        var reopen = Stopwatch.GetTimestamp();
        await using var restarted = Open(options, Start(options.ControlDir, 16L * 1024 * 1024), ramBytes: 0);
        var openUs = Micros(reopen);
        Assert.True(restarted.TryRead(record.ArtId, out var read));
        Assert.Equal(ArticleStorageState.Present, restarted.Index.TryGet(record.ArtId, out var meta) ? meta.State : ArticleStorageState.Invalid);
        Log($"RESTART corrupt_partial_open_us={openUs:F1} entries={restarted.EgressCacheEntryCount} segment_reads={restarted.SegmentArticleReadCount} bytes_match={read.ArtData.Span.SequenceEqual(record.ArtData.Span)} state={(restarted.Index.TryGet(record.ArtId, out meta) ? meta.State : ArticleStorageState.Invalid)}");
    }

    private async Task DegradedAsync(ArticleStorageRuntimeOptions options)
    {
        var blocked = Path.Combine(options.ControlDir, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "x");
        var disabled = ArticleEgressCache.Open(Start(blocked, 1024));
        Log($"DEGRADED path_is_file enabled={disabled.IsEnabled}");
        disabled.Dispose();

        await using var engine = Open(options, Start(options.ControlDir, 16L * 1024 * 1024), ramBytes: 0);
        var record = Create(1, "degraded")[0];
        await PublishAsync(engine, record);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out _));
        await engine.Egress.DrainFillsAsync();
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var stale = engine.Egress.TryCopy(record.ArtId, meta.Sequence + 1, meta.ArtHash, meta.ArtSize, out _);
        Assert.True(engine.TryRead(record.ArtId, out var afterStale));
        Log($"DEGRADED stale_copy={stale} read_after_stale={afterStale.ArtData.Length} invalidation={engine.EgressCacheInvalidationCount} state={meta.State}");
        var corruptControl = Path.Combine(options.ControlDir, "corrupt");
        var corruptSegment = Path.Combine(options.SegmentDir, "corrupt");
        Directory.CreateDirectory(corruptControl);
        Directory.CreateDirectory(corruptSegment);
        await using var corruptEngine = Open(
            options with { ControlDir = corruptControl, SegmentDir = corruptSegment },
            Start(corruptControl, 16L * 1024 * 1024),
            ramBytes: 0);
        var corruptRecord = Create(1, "corrupt-entry")[0];
        await PublishAsync(corruptEngine, corruptRecord);
        await WarmOneAsync(corruptEngine, corruptRecord);
        var corruptSlab = Directory.EnumerateFiles(Path.Combine(corruptControl, "egress", "live"), "slab-*.bin").Single();
        using (var stream = new FileStream(corruptSlab, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var bytes = new byte[stream.Length];
            _ = stream.Read(bytes, 0, bytes.Length);
            bytes[Math.Min(70, bytes.Length - 1)] ^= 0xFF;
            stream.Position = 0;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        var corruptSegments = corruptEngine.SegmentArticleReadCount;
        var corruptRead = corruptEngine.TryRead(corruptRecord.ArtId, out var corruptResult)
            && corruptResult.ArtData.Span.SequenceEqual(corruptRecord.ArtData.Span);
        Log($"DEGRADED corrupt_fallback={corruptRead} corruption_count={corruptEngine.EgressCacheCorruptionCount} invalidation={corruptEngine.EgressCacheInvalidationCount} segment_delta={corruptEngine.SegmentArticleReadCount - corruptSegments} entries={corruptEngine.EgressCacheEntryCount} state={(corruptEngine.Index.TryGet(corruptRecord.ArtId, out var corruptMeta) ? corruptMeta.State : ArticleStorageState.Invalid)}");
    }

    private static async Task WarmOneAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out _));
        await engine.Egress.DrainFillsAsync();
    }

    private static string Snapshot(FileArticleStorageEngine engine) =>
        $"segment={engine.SegmentArticleReadCount} physical={engine.ArticleReadPhysicalReadCount} journal_reads={engine.JournalArticleReadCount} ram={engine.CacheArticleReadCount} coalesced={engine.ArticleReadCoalescedCount}";

    private static async Task<List<double>> TimeReadsAsync(FileArticleStorageEngine engine, IReadOnlyList<ArticleRecord> records, int repeats)
    {
        var samples = new List<double>(records.Count * repeats);
        for (var i = 0; i < repeats; i++)
        {
            foreach (var record in records)
            {
                var start = Stopwatch.GetTimestamp();
                Assert.True(engine.TryRead(record.ArtId, out _));
                samples.Add(Micros(start));
            }
        }

        await Task.CompletedTask;
        return samples;
    }

    private static async Task<List<double>> TimeConcurrentAsync(FileArticleStorageEngine engine, ArticleId artId, int readers, int each)
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

    private static (double Micros, long Value) Time(Func<long> call)
    {
        var start = Stopwatch.GetTimestamp();
        var value = call();
        return (Micros(start), value);
    }

    private static async Task PublishAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
    }

    private static FileArticleStorageEngine Open(ArticleStorageRuntimeOptions options, EgressCacheStart? egress, long ramBytes) =>
        FileArticleStorageEngine.Open(
            options,
            volumeProbe: null,
            articleCache: new ArticleMemoryCache(ramBytes),
            egressCache: egress);

    private static EgressCacheStart Start(
        string controlDir,
        long capacity,
        long reserve = 0,
        long? journalHard = null,
        long? journalCheckpoint = null,
        long? indexCheckpoint = null,
        int utilization = 70,
        IEgressVolumeSpace? space = null,
        bool injectedSpace = false) =>
        new()
        {
            ControlDir = controlDir,
            CapacityBytes = capacity,
            ReserveBytes = reserve,
            JournalHardLimitBytes = journalHard ?? JournalHard,
            JournalCheckpointThresholdBytes = journalCheckpoint ?? JournalCheckpoint,
            IndexCheckpointThresholdBytes = indexCheckpoint ?? IndexCheckpoint,
            MaximumUtilizationPercent = utilization,
            MaximumUsageCapacityPercent = 50,
            FreeCapacityPercent = 5,
            Space = injectedSpace ? space : null,
        };

    private void Log(string line)
    {
        _log.AppendLine(line);
    }

    private void LogStats(string name, IReadOnlyList<double> samples)
    {
        if (samples.Count == 0)
        {
            Log($"{name} n=0");
            return;
        }

        var ordered = samples.ToArray();
        Array.Sort(ordered);
        var sum = ordered.Sum();
        Log($"{name} n={ordered.Length} mean={sum / ordered.Length:F1} p50={Pct(ordered, 50):F1} p95={Pct(ordered, 95):F1} p99={Pct(ordered, 99):F1} max={ordered[^1]:F1}");
    }

    private static double Pct(double[] ordered, int percentile)
    {
        var index = (int)Math.Ceiling(percentile / 100d * ordered.Length) - 1;
        if (index < 0)
        {
            index = 0;
        }

        if (index >= ordered.Length)
        {
            index = ordered.Length - 1;
        }

        return ordered[index];
    }

    private static double Micros(long start) => Stopwatch.GetElapsedTime(start).TotalMicroseconds;

    private static ArticleRecord[] Create(int count, string prefix, int bodyBytes = BodyBytes)
    {
        var body = WrappedBody(bodyBytes);
        var records = new ArticleRecord[count];
        for (var i = 0; i < count; i++)
        {
            records[i] = CreateRecord($"<{prefix}-{i}@phase39.test>", body);
        }

        return records;
    }

    private static string WrappedBody(int bodyBytes)
    {
        var remaining = Math.Max(1, bodyBytes);
        var builder = new StringBuilder(remaining + remaining / 900 * 2 + 2);
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
        var builder = new StringBuilder();
        builder.Append("Path: peer.example\r\n");
        builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        builder.Append("Newsgroups: alt.test\r\n");
        builder.Append("From: user@example.test\r\n");
        builder.Append("Subject: phase39\r\n");
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
        private Workspace(string root)
        {
            Root = root;
        }

        public string Root { get; }

        public string Control => Path.Combine(Root, "control");

        public string Segment => Path.Combine(Root, "segment");

        public static Workspace Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase39-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "control"));
            Directory.CreateDirectory(Path.Combine(root, "segment"));
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
                JournalSoftLimitBytes: 67108864,
                JournalHardLimitBytes: JournalHard,
                SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
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
