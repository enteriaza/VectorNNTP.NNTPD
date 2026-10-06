using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
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
using VectorNNTP.StorageServer.Tests.Logging;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>Phase 5E.1 process-local capacity reservation.</summary>
public sealed class ProcessLocalCapacityAdmissionTests
{
    [Fact]
    public void A_Capacity_defaults_are_integer_percents_and_cannot_be_disabled()
    {
        var capacity = new ArticleCapacityOptions();
        Assert.Null(typeof(ArticleCapacityOptions).GetProperty("Enabled"));
        Assert.Equal(90, capacity.MaximumUtilization);
        Assert.Equal(10, capacity.CompactionHeadroom);
        Assert.Equal(80, capacity.MaximumUsageCapacity);
        Assert.Equal(5, capacity.FreeCapacity);
    }

    [Fact]
    public void B_C_Utilization_validation()
    {
        var options = StorageServerTestOptionsCreateValid();
        options.Storage.Capacity.MaximumUtilization = 0;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Capacity.MaximumUtilization = 101;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
        options.Storage.Capacity.MaximumUtilization = 80;
        options.Storage.Capacity.MaximumUsageCapacity = 70;
        Assert.False(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Failed);
    }

    [Fact]
    public async Task A2_Capacity_disabled_unchanged()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 1_000_000, used: 999_000);
        await using var engine = FileArticleStorageEngine.Open(
            dir.Options,
            capacityReader: capacity);
        var result = await engine.AcceptAsync(CreateRecord("<cap-off@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Outcome);
        Assert.Equal(0, engine.ProcessLocalReservedBytes);
    }

    [Fact]
    public async Task D_E_Below_and_above_threshold()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<cap-thr@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var occupied = required + ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize) + ArticleIndexRecordCodec.RecordLength;
        var total = 10_000L;
        var maxUtil = 80;
        var maxAllowed = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, maxUtil);

        var capacity = new MutableCapacityReader(total, used: maxAllowed - occupied);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maxUtil),
            capacityReader: capacity);
        engine.SuspendBackgroundPersist = true;

        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Equal(occupied, engine.ProcessLocalReservedBytes);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);

        capacity.UsedBytes = maxAllowed;
        var rejected = await engine.AcceptAsync(CreateRecord("<cap-thr-2@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, rejected.Outcome);
        Assert.Equal(occupied, engine.ProcessLocalReservedBytes);
    }

    [Fact]
    public async Task E2_RequiredBytes_uses_physical_record_length()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<cap-phys@seg.test>", body: new string('x', 100) + "\r\n");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        Assert.True(required > record.ArtSize);

        // Room for ArtSize alone but not framed record → reject proves framing is used.
        var total = 1_000L;
        var capacity = new MutableCapacityReader(total, used: total - record.ArtSize - 1);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 99),
            capacityReader: capacity);
        var result = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Outcome);
        Assert.Equal(0, engine.ProcessLocalReservedBytes);
        _ = required;
    }

    [Fact]
    public async Task F_G_Duplicate_and_conflict_do_not_reserve()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        var first = CreateRecord("<cap-dup@seg.test>", "a\r\n");
        var required = SegmentRecordCodec.RecordLengthForArtSize(first.ArtSize);
        var occupied = required + ArticleJournalFrameCodec.SequenceReservationBytes(first.ArtSize) + ArticleIndexRecordCodec.RecordLength;
        _ = await engine.AcceptAsync(first, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        var retained = occupied - required;
        Assert.Equal(retained, engine.ProcessLocalReservedBytes);

        var dup = await engine.AcceptAsync(first, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Duplicate, dup.Outcome);
        Assert.Equal(retained, engine.ProcessLocalReservedBytes);

        var conflict = CreateRecord("<cap-dup@seg.test>", "b\r\n");
        var conflictResult = await engine.AcceptAsync(conflict, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Conflict, conflictResult.Outcome);
        Assert.Equal(retained, engine.ProcessLocalReservedBytes);
    }

    [Fact]
    public async Task H_J_K_Reservation_through_PhysicalWritten()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<cap-hold@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var occupied = required + ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize) + ArticleIndexRecordCodec.RecordLength;
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Equal(occupied, engine.ProcessLocalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalReservationCount);

        // Suspended Accept is not queued; RecoverAsync completes PhysicalWritten.
        // The written copy stays tracked and leaves admission.
        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(occupied - required, engine.ProcessLocalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalReservationCount);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task L_Sata_failure_keeps_reservation_and_is_not_RejectedCapacity()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<cap-sata@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var occupied = required + ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize) + ArticleIndexRecordCodec.RecordLength;
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Equal(occupied, engine.ProcessLocalReservedBytes);

        // SATA I/O failure must not become RejectedCapacity; reservation stays while Accept is outstanding.
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;
        var fault = await Assert.ThrowsAsync<IOException>(
            () => engine.RecoverAsync(CancellationToken.None));
        Assert.Contains("BeforeSataAppend", fault.Message, StringComparison.Ordinal);
        Assert.Equal(occupied, engine.ProcessLocalReservedBytes);
        Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.NotEqual(ArticleAcceptOutcome.RejectedCapacity, accepted.Outcome);

        // Retry without fault completes PhysicalWritten. The written copy leaves admission.
        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(occupied - required, engine.ProcessLocalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task L2_PhysicalWritten_journal_failure_keeps_reservation()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<cap-pw-fail@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var occupied = required + ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize) + ArticleIndexRecordCodec.RecordLength;
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(occupied, engine.ProcessLocalReservedBytes);

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforePhysicalWritten;
        _ = await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Equal(occupied - required, engine.ProcessLocalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Null(Assert.Single(engine.Journal.EnumerateIncomplete()).PhysicalWritten);

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(occupied - required, engine.ProcessLocalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task L3_After_PhysicalWritten_reservation_stays_through_index_work()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<cap-after-pw@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var occupied = required + ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize) + ArticleIndexRecordCodec.RecordLength;
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(occupied, engine.ProcessLocalReservedBytes);

        // Fault after durable PhysicalWritten. The flushed copy has left admission.
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterPhysicalWritten;
        _ = await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Equal(occupied - required, engine.ProcessLocalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.NotNull(Assert.Single(engine.Journal.EnumerateIncomplete()).PhysicalWritten);

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(occupied - required, engine.ProcessLocalReservedBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task I_Journal_Accept_failure_rolls_back_reservation()
    {
        var probe = CreateRecord("<cap-jp-probe@seg.test>", "x\r\n");
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options) with
            {
                JournalSoftLimitBytes = Math.Max(1, probe.ArtSize / 2),
                JournalHardLimitBytes = probe.ArtSize,
            },
            capacityReader: capacity);
        engine.SuspendBackgroundPersist = true;
        var probeRequired = SegmentRecordCodec.RecordLengthForArtSize(probe.ArtSize);
        var occupied = probeRequired + ArticleJournalFrameCodec.SequenceReservationBytes(probe.ArtSize) + ArticleIndexRecordCodec.RecordLength;
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(probe, CancellationToken.None)).Outcome);
        Assert.Equal(occupied, engine.ProcessLocalReservedBytes);

        var rejected = await engine.AcceptAsync(
            CreateRecord("<cap-jp-reject@seg.test>", "y\r\n"),
            CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedPressure, rejected.Outcome);
        Assert.Equal(occupied, engine.ProcessLocalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalReservationCount);
    }

    [Fact]
    public async Task M_Retry_does_not_double_reserve()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<cap-retry@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var occupied = required + ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize) + ArticleIndexRecordCodec.RecordLength;
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(occupied, engine.ProcessLocalReservedBytes);
        engine.SuspendBackgroundPersist = false;
        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(occupied - required, engine.ProcessLocalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(occupied - required, engine.ProcessLocalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
    }

    [Fact]
    public async Task N_Concurrent_Accepts_cannot_oversubscribe()
    {
        using var dir = TempStorageDir.Create();
        const int attempts = 32;
        var sample = CreateRecord("<cap-conc-00@seg.test>");
        var pair = SegmentRecordCodec.RecordLengthForArtSize(sample.ArtSize)
            + ArticleJournalFrameCodec.SequenceReservationBytes(sample.ArtSize)
            + ArticleIndexRecordCodec.RecordLength;
        // With MaximumUtilization=30 and Total=10*pair, at most 3 articles fit.
        // Suspend persist so PhysicalWritten cannot release reservations mid-admission race.
        var total = pair * 10L;
        var capacity = new MutableCapacityReader(total, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization: 30),
            capacityReader: capacity);
        engine.SuspendBackgroundPersist = true;

        var gate = new ManualResetEventSlim(false);
        var outcomes = new ConcurrentBag<ArticleAcceptOutcome>();
        var tasks = Enumerable.Range(0, attempts).Select(i => Task.Run(async () =>
        {
            gate.Wait();
            var result = await engine.AcceptAsync(
                CreateRecord($"<cap-conc-{i:D2}@seg.test>"),
                CancellationToken.None);
            outcomes.Add(result.Outcome);
        })).ToArray();

        gate.Set();
        await Task.WhenAll(tasks);

        var accepted = outcomes.Count(static o => o == ArticleAcceptOutcome.Accepted);
        var rejected = outcomes.Count(static o => o == ArticleAcceptOutcome.RejectedCapacity);
        Assert.Equal(3, accepted);
        Assert.Equal(attempts - 3, rejected);
        Assert.Equal(pair * 3L, engine.ProcessLocalReservedBytes);
        Assert.Equal(3, engine.ProcessLocalReservationCount);

        await engine.RecoverAsync(CancellationToken.None);
        var segmentBytes = SegmentRecordCodec.RecordLengthForArtSize(sample.ArtSize);
        Assert.Equal((pair - segmentBytes) * 3L, engine.ProcessLocalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(3, engine.ProcessLocalReservationCount);
        Assert.Equal(3, engine.ProcessLocalSegmentCopyCount);
    }

    [Fact]
    public async Task O_Reads_work_when_writes_rejected()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        var record = CreateRecord("<cap-read@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);

        capacity.UsedBytes = capacity.TotalBytes;
        var rejected = await engine.AcceptAsync(CreateRecord("<cap-read-2@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, rejected.Outcome);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task P_Zero_size_rejected_before_capacity()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        // Non-canonical / ArtSize<=0 path uses RejectedInvalid (existing semantics).
        var bogus = default(ArticleRecord);
        var result = await engine.AcceptAsync(bogus, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedInvalid, result.Outcome);
        Assert.Equal(0, engine.ProcessLocalReservedBytes);
    }

    [Fact]
    public async Task Q_R_Restart_clears_reservation_recovery_unchanged()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        var record = CreateRecord("<cap-restart@seg.test>");
        await using (var engineA = FileArticleStorageEngine.Open(
                         WithCapacity(dir.Options),
                         capacityReader: capacity))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            Assert.True(engineA.ProcessLocalReservedBytes > 0);
        }

        await using var engineB = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        Assert.Equal(
            ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize),
            engineB.ProcessLocalReservedBytes);
        Assert.Equal(0, engineB.ProcessLocalArticleReservedBytes);
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.True(engineB.TryRead(record.ArtId, out _));
        Assert.Equal(
            ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize) + ArticleIndexRecordCodec.RecordLength,
            engineB.ProcessLocalReservedBytes);
        Assert.Equal(0, engineB.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engineB.ProcessLocalSegmentCopyCount);
    }

    [Fact]
    public async Task S_T_Evict_keeps_segment_and_journal_and_reserves_the_death_frame()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        var live = CreateRecord("<cap-live@seg.test>");
        var liveRequired = SegmentRecordCodec.RecordLengthForArtSize(live.ArtSize);
        var liveOccupied = liveRequired + ArticleJournalFrameCodec.SequenceReservationBytes(live.ArtSize) + ArticleIndexRecordCodec.RecordLength;
        _ = await engine.AcceptAsync(live, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        var liveRetained = liveOccupied - liveRequired;
        Assert.Equal(liveRetained, engine.ProcessLocalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);

        engine.SuspendBackgroundPersist = true;
        var pending = CreateRecord("<cap-pending@seg.test>");
        var pendingOccupied = SegmentRecordCodec.RecordLengthForArtSize(pending.ArtSize)
            + ArticleJournalFrameCodec.SequenceReservationBytes(pending.ArtSize) + ArticleIndexRecordCodec.RecordLength;
        _ = await engine.AcceptAsync(pending, CancellationToken.None);
        Assert.Equal(liveRetained + pendingOccupied, engine.ProcessLocalReservedBytes);

        Assert.True(engine.TryEvict(live.ArtId));
        Assert.Equal(
            liveRetained + pendingOccupied + ArticleIndexRecordCodec.RecordLength,
            engine.ProcessLocalReservedBytes);
        Assert.Equal(SegmentRecordCodec.RecordLengthForArtSize(pending.ArtSize), engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(
            ArticleJournalFrameCodec.SequenceReservationBytes(live.ArtSize)
                + ArticleJournalFrameCodec.SequenceReservationBytes(pending.ArtSize),
            engine.ProcessLocalJournalReservedBytes);
    }

    [Fact]
    public async Task U_V_Rejection_logs_structured_properties_without_success_noise()
    {
        var sink = new CollectingSink();
        using var loggerFactory = new SerilogLoggerFactory(
            new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger(),
            dispose: true);
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 100, used: 100);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            logger: loggerFactory.CreateLogger("capacity"),
            capacityReader: capacity);

        var rejected = await engine.AcceptAsync(CreateRecord("<cap-log@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, rejected.Outcome);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, rejected.Reason);
        Assert.Contains(
            sink.Events,
            static e => e.Level == LogEventLevel.Error
                && e.Properties.ContainsKey("ArtSize")
                && e.Properties.ContainsKey("FreeBytes")
                && e.Properties.ContainsKey("RecoveryReserveBytes")
                && e.Properties.ContainsKey("ProtectedHeadroomBytes")
                && e.Properties.ContainsKey("RecoveryAttempted"));

        // Successful Accept must not emit capacity-rejection warnings.
        capacity.UsedBytes = 0;
        capacity.TotalBytes = 10_000_000;
        var before = sink.Events.Count;
        var accepted = await engine.AcceptAsync(CreateRecord("<cap-log-ok@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.DoesNotContain(
            sink.Events.Skip(before),
            static e => e.Properties.ContainsKey("RequiredBytes")
                && e.MessageTemplate.Text.Contains("capacity", StringComparison.OrdinalIgnoreCase));
    }

    private static ArticleStorageRuntimeOptions WithCapacity(
        ArticleStorageRuntimeOptions options,
        int maximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization) =>
        options with
        {
            CapacityMaximumUtilization = maximumUtilization,
        };

    private static StorageServerOptions StorageServerTestOptionsCreateValid()
    {
        return new StorageServerOptions
        {
            ServerId = 1,
            DnsSuffix = "usenet.ninja",
            CloudFlareZoneId = "0123456789abcdef0123456789abcdef",
            BindAddress = ["127.0.0.1"],
            BindPort = 0,
            BindPortTls = 1191,
            AcmeDirectoryUrl = StorageServerOptions.DefaultAcmeDirectoryUrl,
            AcmeRenewalThresholdDays = StorageServerOptions.DefaultAcmeRenewalThresholdDays,
            AcmeStateDir = "certs/",
            LogDir = "/logs",
            CertificateDirectory = "certs",
            ApplicationName = "VectorNNTP.StorageServer",
            GracefulShutdownTimeout = TimeSpan.FromSeconds(30),
        };
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
        _ = builder.Append("Subject: capacity\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-cap-" + Guid.NewGuid().ToString("N"));
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
