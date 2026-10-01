using System.Collections.Concurrent;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>Phase 5E.2 compaction/relocation capacity headroom.</summary>
public sealed class ProcessLocalCompactionCapacityTests
{
    [Fact]
    public void Ledger_A_F_Article_and_compaction_counters_independent()
    {
        var ledger = new ProcessLocalCapacityLedger();
        Assert.True(ledger.WouldFit(0, 1000, 100, 80));
        ledger.TentativeAddArticle(100);
        ledger.BindArticleSequence(1, 100);
        Assert.Equal(100, ledger.ArticleReservedBytes);
        Assert.Equal(0, ledger.CompactionReservedBytes);

        Assert.True(ledger.WouldFit(0, 1000, 50, 90));
        ledger.ReserveCompaction(7, 1, 50);
        Assert.Equal(100, ledger.ArticleReservedBytes);
        Assert.Equal(50, ledger.CompactionReservedBytes);
        Assert.Equal(150, ledger.ReservedBytes);

        Assert.True(ledger.ReleaseArticle(1));
        Assert.Equal(0, ledger.ArticleReservedBytes);
        Assert.Equal(50, ledger.CompactionReservedBytes);
        Assert.True(ledger.ReleaseCompaction(7, 1));
        Assert.Equal(0, ledger.CompactionReservedBytes);
        Assert.False(ledger.ReleaseArticle(1));
        Assert.False(ledger.ReleaseCompaction(7, 1));
        Assert.Equal(0, ledger.ReservedBytes);
    }

    [Fact]
    public void Ledger_B_D_Shared_ceiling_includes_both_classes()
    {
        var ledger = new ProcessLocalCapacityLedger();
        ledger.TentativeAddArticle(700);
        ledger.BindArticleSequence(1, 700);
        // Used 0 + art 700 + req 101 against 0.80 of 1000 = 800 → reject
        Assert.False(ledger.WouldFit(0, 1000, 101, 80));
        // Against compaction ceiling 0.90 → admit
        Assert.True(ledger.WouldFit(0, 1000, 100, 90));

        ledger.ReserveCompaction(1, 1, 150);
        // Used 0 + 700 + 150 + 50 = 900 against 0.90 → exact admit
        Assert.True(ledger.WouldFit(0, 1000, 50, 90));
        Assert.False(ledger.WouldFit(0, 1000, 51, 90));
    }

    [Fact]
    public async Task Relocation_A_Full_volume_rejects_before_relocation()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 100, used: 100);
        await using var engine = FileArticleStorageEngine.Open(
            dir.Options,
            capacityReader: capacity);
        var record = CreateRecord("<cc-off@seg.test>");
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, accepted.Outcome);
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);
        Assert.NotNull(engine.SegmentCapacity);
    }

    [Fact]
    public async Task Relocation_B_C_D_Below_boundary_and_above()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<cc-thr@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var pins = required
            + ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize)
            + ArticleIndexRecordCodec.RecordLength;
        var total = pins * 20L;
        // Admit Accept under MaxUtil=0.20 with Used=0; then raise Used for relocate.
        var capacity = new MutableCapacityReader(total, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 20, compactionHeadroom: 10),
            capacityReader: capacity);

        var (sourceId, generation) = await AcceptCloseAsync(engine, record);
        var compactionId = await BeginAsync(engine, sourceId, generation);

        // Compaction ceiling includes the retained segment, journal, and index reservations,
        // plus the destination copy and the new Present frame.
        var compactionCeiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 30);
        var newFrame = (long)ArticleIndexRecordCodec.RecordLength;
        var intent = ArticleJournalFrameCodec.RelocationIntentFrameLength;
        capacity.UsedBytes = compactionCeiling - pins - required - newFrame - intent + 1;
        var rejected = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedCapacity, rejected.Outcome);
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);

        var written = ArticleJournalFrameCodec.RelocationWrittenFrameLength;
        capacity.UsedBytes = compactionCeiling - pins - required - newFrame - intent - written;
        var accepted = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, accepted.Outcome);
        Assert.Equal(required, engine.ProcessLocalCompactionReservedBytes);
    }

    [Fact]
    public async Task Relocation_E_F_Cross_class_reservation_interaction()
    {
        using var dir = TempStorageDir.Create();
        var live = CreateRecord("<cc-live@seg.test>");
        var pending = CreateRecord("<cc-pend@seg.test>");
        var requiredLive = SegmentRecordCodec.RecordLengthForArtSize(live.ArtSize);
        var requiredPending = SegmentRecordCodec.RecordLengthForArtSize(pending.ArtSize);
        var total = requiredLive * 20L;
        var capacity = new MutableCapacityReader(total, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 20),
            capacityReader: capacity);

        var (sourceId, generation) = await AcceptCloseAsync(engine, live);
        // Ceiling bytes via scaled util: use integer WouldFit semantics — reject when used + artRes + req > ceiling.
        // MaxUtil 0.50 + headroom 0.20 = 0.70. Hold article reservation and set Used so projected exceeds ceiling.
        capacity.UsedBytes = 0;
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(pending, CancellationToken.None)).Outcome);
        Assert.Equal(requiredLive + requiredPending, engine.ProcessLocalArticleReservedBytes);

        // Force reject: used + articleRes + requiredLive must exceed 0.70 * total.
        // Set used = TotalBytes so even with headroom nothing fits while article reservation is held.
        capacity.UsedBytes = total;
        var compactionId = await BeginAsync(engine, sourceId, generation);
        var relocate = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, live.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.RejectedCapacity, relocate.Outcome);

        // Both physical copies stay reserved. Free Used so compaction still admits under headroom.
        capacity.UsedBytes = 0;
        engine.SuspendBackgroundPersist = false;
        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(requiredLive + requiredPending, engine.ProcessLocalArticleReservedBytes);
        var relocateOk = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, live.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, relocateOk.Outcome);
    }

    [Fact]
    public async Task Relocation_H_I_Release_after_append_and_on_pre_append_failure()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        var record = CreateRecord("<cc-rel@seg.test>");
        var (sourceId, generation) = await AcceptCloseAsync(engine, record);
        var compactionId = await BeginAsync(engine, sourceId, generation);

        engine.TestRelocationFaultPoint =
            FileArticleStorageEngine.RelocationFaultPoint.AfterIntentBeforeAppend;
        _ = await Assert.ThrowsAsync<IOException>(() =>
            engine.RelocateArticleAsync(
                compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None));
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);

        var ok = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, ok.Outcome);
        Assert.Equal(
            SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize),
            engine.ProcessLocalCompactionReservedBytes);
    }

    [Fact]
    public async Task Relocation_J_Written_failure_after_append_does_not_reacquire()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        var record = CreateRecord("<cc-pw@seg.test>");
        var (sourceId, generation) = await AcceptCloseAsync(engine, record);
        var compactionId = await BeginAsync(engine, sourceId, generation);

        engine.TestRelocationFaultPoint =
            FileArticleStorageEngine.RelocationFaultPoint.AfterAppendBeforeWritten;
        _ = await Assert.ThrowsAsync<IOException>(() =>
            engine.RelocateArticleAsync(
                compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None));
        Assert.Equal(
            SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize),
            engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(1, engine.ProcessLocalCompactionReservationCount);
    }

    [Fact]
    public async Task Relocation_L_M_Intent_only_reserves_Written_reuse_does_not()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        var record = CreateRecord("<cc-intent@seg.test>");
        ulong compactionId;
        SegmentId sourceId;
        ulong generation;
        await using (var engineA = FileArticleStorageEngine.Open(
                         WithCapacity(dir.Options),
                         capacityReader: capacity))
        {
            (sourceId, generation) = await AcceptCloseAsync(engineA, record);
            compactionId = await BeginAsync(engineA, sourceId, generation);
            engineA.TestRelocationFaultPoint =
                FileArticleStorageEngine.RelocationFaultPoint.AfterIntentBeforeAppend;
            _ = await Assert.ThrowsAsync<IOException>(() =>
                engineA.RelocateArticleAsync(
                    compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None));
            Assert.True(engineA.Journal.TryGetCompaction(compactionId, out var snap));
            Assert.Null(Assert.Single(snap.Relocations).Written);
        }

        await using var engineB = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        Assert.Equal(0, engineB.ProcessLocalCompactionReservedBytes);
        // Durable Begin uses process-local catalogue generation at Begin time; after reopen
        // pass Begin.SourceGeneration from journal.
        Assert.True(engineB.Journal.TryGetCompaction(compactionId, out var open));
        var beginGen = open.Begin.SourceGeneration;
        var relocated = await engineB.RelocateArticleAsync(
            compactionId, 1, sourceId, beginGen, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, relocated.Outcome);
        var copyBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        Assert.Equal(copyBytes, engineB.ProcessLocalCompactionReservedBytes);

        var again = await engineB.RelocateArticleAsync(
            compactionId, 1, sourceId, beginGen, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.IdempotentNoOp, again.Outcome);
        Assert.Equal(copyBytes, engineB.ProcessLocalCompactionReservedBytes);
    }

    [Fact]
    public async Task Relocation_O_Sequential_relocations_do_not_leak()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        var a = CreateRecord("<cc-seq-a@seg.test>");
        var b = CreateRecord("<cc-seq-b@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(a, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(b, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(a.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        var compactionId = await BeginAsync(engine, sourceId, info.Generation);

        Assert.Equal(
            ArticleRelocationOutcome.Relocated,
            (await engine.RelocateArticleAsync(
                compactionId, 1, sourceId, info.Generation, a.ArtId, CancellationToken.None)).Outcome);
        var aBytes = SegmentRecordCodec.RecordLengthForArtSize(a.ArtSize);
        Assert.Equal(aBytes, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(
            ArticleRelocationOutcome.Relocated,
            (await engine.RelocateArticleAsync(
                compactionId, 2, sourceId, info.Generation, b.ArtId, CancellationToken.None)).Outcome);
        Assert.Equal(aBytes + SegmentRecordCodec.RecordLengthForArtSize(b.ArtSize), engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(2, engine.ProcessLocalCompactionReservationCount);
    }

    [Fact]
    public async Task Relocation_P_Concurrent_relocations_cannot_oversubscribe()
    {
        using var dir = TempStorageDir.Create();
        const int articles = 8;
        var body = new string('x', 400) + "\r\n";
        var records = Enumerable.Range(0, articles)
            .Select(i => CreateRecord($"<cc-conc-{i:D2}@seg.test>", body))
            .ToArray();
        var required = SegmentRecordCodec.RecordLengthForArtSize(records[0].ArtSize);
        var total = required * 100L;
        var capacity = new MutableCapacityReader(total, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 5),
            capacityReader: capacity);

        foreach (var r in records)
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(r, CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(records[0].ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        var compactionId = await BeginAsync(engine, sourceId, info.Generation);
        var pins = engine.ProcessLocalReservedBytes;
        Assert.Equal(
            (required * articles)
                + engine.ProcessLocalJournalReservedBytes
                + engine.ProcessLocalIndexReservedBytes,
            pins);
        // Ceiling 0.55*100r. Room for every intent plus exactly 3 destinations and
        // 3 new Present frames. A fourth destination still exceeds that room even
        // when the other intents have not been reserved yet.
        var intent = ArticleJournalFrameCodec.RelocationIntentFrameLength;
        var frame = ArticleIndexRecordCodec.RecordLength;
        var written = ArticleJournalFrameCodec.RelocationWrittenFrameLength;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 55);
        capacity.UsedBytes = ceiling - pins - (intent * articles) - (3L * (required + frame + written));
        Assert.True(capacity.UsedBytes >= 0);

        var hold = new ManualResetEventSlim(false);
        var reserved = new CountdownEvent(3);
        engine.TestHookAfterCompactionCapacityReserved = () =>
        {
            if (reserved.CurrentCount > 0)
            {
                reserved.Signal();
            }

            hold.Wait();
        };

        var gate = new ManualResetEventSlim(false);
        var outcomes = new ConcurrentBag<ArticleRelocationOutcome>();
        var tasks = records.Select((r, i) => Task.Run(async () =>
        {
            gate.Wait();
            var result = await engine.RelocateArticleAsync(
                compactionId,
                (ulong)(i + 1),
                sourceId,
                info.Generation,
                r.ArtId,
                CancellationToken.None);
            outcomes.Add(result.Outcome);
        })).ToArray();

        gate.Set();
        Assert.True(reserved.Wait(TimeSpan.FromSeconds(5)));
        // Three reservations held; remaining callers must RejectedCapacity.
        await Task.Delay(50);
        hold.Set();
        await Task.WhenAll(tasks);

        var relocated = outcomes.Count(static o => o == ArticleRelocationOutcome.Relocated);
        var rejected = outcomes.Count(static o => o == ArticleRelocationOutcome.RejectedCapacity);
        Assert.Equal(3, relocated);
        Assert.Equal(articles - 3, rejected);
        Assert.Equal(3L * required, engine.ProcessLocalCompactionReservedBytes);
    }

    [Fact]
    public async Task Coordinator_A_B_Capacity_skip_and_incomplete()
    {
        using var dir = TempStorageDir.Create();
        var records = Enumerable.Range(0, 3)
            .Select(i => CreateRecord($"<cc-mnt-{i}@seg.test>"))
            .ToArray();
        var required = SegmentRecordCodec.RecordLengthForArtSize(records[0].ArtSize);
        var total = required * 100L;
        var capacity = new MutableCapacityReader(total, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 50, compactionHeadroom: 5),
            capacityReader: capacity);

        foreach (var r in records)
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(r, CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(records[0].ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;

        Assert.True(engine.TryEvict(records[2].ArtId));
        // Full disk → Skipped (capacity before first relocate).
        capacity.UsedBytes = total;

        var coordinator = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: 0));
        var skipped = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Skipped, skipped.Outcome);
        Assert.Contains("capacity", skipped.SkipReason, StringComparison.Ordinal);
        Assert.NotEqual(StorageMaintenanceOutcome.Failed, skipped.Outcome);

        // Allow first relocate only: used = ceiling - required (exact), then second rejects.
        // Ceiling 0.55 * 100r = 55r. After first succeeds CompRes releases; set Used back to total-required
        // mid-flight via hook.
        capacity.UsedBytes = 0;
        var firstDone = new ManualResetEventSlim(false);
        engine.TestHookAfterCompactionCapacityReserved = () =>
        {
            if (!firstDone.IsSet)
            {
                firstDone.Set();
                capacity.UsedBytes = LeaveRoomForWrittenFrame(engine);
            }
        };
        var incomplete = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(StorageMaintenanceOutcome.Incomplete, incomplete.Outcome);
        Assert.True(incomplete.CompactionAttempted);
        Assert.False(incomplete.CompactionCommitted);
        Assert.True(incomplete.RelocatedArticleCount >= 1);
        Assert.Contains(engine.Journal.EnumerateOpenCompactions(), static c => !c.Committed);
    }

    private static long LeaveRoomForWrittenFrame(FileArticleStorageEngine engine)
    {
        var ceiling = engine.ObserveCapacityAdmissionPressure().CompactionCeilingBytes;
        return Math.Max(
            0,
            ceiling - engine.ProcessLocalReservedBytes - ArticleJournalFrameCodec.RelocationWrittenFrameLength);
    }

    private static ArticleStorageRuntimeOptions WithCapacity(
        ArticleStorageRuntimeOptions options,
        int maximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization,
        int compactionHeadroom = ArticleCapacityOptions.DefaultCompactionHeadroom) =>
        options with
        {
            CapacityMaximumUtilization = maximumUtilization,
            CapacityCompactionHeadroom = compactionHeadroom,
        };

    private static async Task<(SegmentId SourceId, ulong Generation)> AcceptCloseAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        return (sourceId, info.Generation);
    }

    private static async Task<ulong> BeginAsync(
        FileArticleStorageEngine engine,
        SegmentId sourceId,
        ulong generation)
    {
        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, sourceId, generation),
                CancellationToken.None));
        return compactionId;
    }

    private static ArticleRecord CreateRecord(string messageId, string body = "line1\r\nline2\r\n")
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: compaction-capacity\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class MutableCapacityReader : IStorageCapacityReader
    {
        public MutableCapacityReader(long total, long used)
        {
            TotalBytes = total;
            UsedBytes = used;
        }

        public long TotalBytes { get; set; }

        public long UsedBytes { get; set; }

        public StorageCapacitySnapshot Read() =>
            new(TotalBytes, UsedBytes, Math.Max(0L, TotalBytes - UsedBytes));
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-cc-" + Guid.NewGuid().ToString("N"));
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
            catch
            {
                // best-effort
            }
        }
    }
}
