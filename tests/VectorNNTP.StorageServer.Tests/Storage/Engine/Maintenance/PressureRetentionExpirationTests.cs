using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;
using VectorNNTP.StorageServer.Tests.Fixtures;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Maintenance;

/// <summary>
/// Pressure expiration logically evicts old Present rows. It does not replace MaxRetentionAge
/// and it does not delete segment bytes.
/// </summary>
public sealed class PressureRetentionExpirationTests
{
    private const long Total = 10_000_000;

    private static readonly DateTimeOffset Arrival = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    [Fact]
    public void Negative_minimum_retention_age_is_rejected()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.BulkPressure.MinimumRetentionAge = TimeSpan.FromTicks(-1);
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static failure => failure.Contains("MinimumRetentionAge", StringComparison.Ordinal));
    }

    [Fact]
    public void Recovery_margin_above_the_pressure_watermark_is_rejected()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.BulkPressure.PressureRecoveryMarginPercent = 81;
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static failure => failure.Contains("PressureRecoveryMarginPercent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Normal_does_not_pressure_expire_and_age_policy_still_expires()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 1_000_000);
        await using var engine = Open(dir, reader, time);
        var byAge = await PublishAsync(engine, "<pressure-age@example>");
        time.Advance(TimeSpan.FromHours(1));
        Assert.True(engine.TryRead(byAge.ArtId, out _));
        var aged = await Coordinator(engine, reader, TimeSpan.FromHours(1)).RunOnceAsync(CancellationToken.None);
        Assert.Equal(nameof(BulkStoragePressureState.Normal), aged.BulkPressureState);
        Assert.False(engine.TryRead(byAge.ArtId, out _));

        var kept = await PublishAsync(engine, "<pressure-normal@example>");
        time.Advance(Grace);
        var normal = await Coordinator(engine, reader).RunOnceAsync(CancellationToken.None);
        Assert.Equal(nameof(BulkStoragePressureState.Normal), normal.BulkPressureState);
        AssertPresent(engine, kept.ArtId);
    }

    [Fact]
    public async Task Warning_does_not_start_pressure_expiration()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 7_600_000);
        await using var engine = Open(dir, reader, time);
        var record = await PublishAsync(engine, "<pressure-warning@example>");
        time.Advance(Grace + TimeSpan.FromHours(1));

        _ = await Coordinator(engine, reader).RunOnceAsync(CancellationToken.None);

        AssertPresent(engine, record.ArtId);
    }

    [Fact]
    public async Task Pressure_expires_an_old_article_and_keeps_a_recent_one()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 8_200_000);
        await using var engine = Open(dir, reader, time);
        var old = await PublishAsync(engine, "<pressure-old@example>");
        time.Advance(Grace);
        var recent = await PublishAsync(engine, "<pressure-recent@example>");

        var result = engine.ExpirePressureRetentionBatch(
            Grace,
            ArticlePressureRetentionPolicy.PressureExpirationLimit,
            BulkStoragePressureState.Pressure,
            CancellationToken.None);

        Assert.Equal(1, result.Expired);
        Assert.True(result.BytesLogicallyExpired > 0);
        AssertEvicted(engine, old.ArtId);
        AssertPresent(engine, recent.ArtId);
        Assert.False(engine.TryRead(old.ArtId, out _));
        Assert.True(engine.TryRead(recent.ArtId, out _));
    }

    [Fact]
    public async Task Disabled_max_retention_age_still_allows_pressure_expiration()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 8_200_000);
        await using var engine = Open(dir, reader, time);
        var record = await PublishAsync(engine, "<pressure-no-age@example>");
        time.Advance(Grace);

        Assert.True(engine.TryRead(record.ArtId, out _));
        _ = await Coordinator(engine, reader, TimeSpan.Zero).RunOnceAsync(CancellationToken.None);

        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task High_prefers_the_older_article_and_respects_the_batch_limit()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 8_700_000);
        await using var engine = Open(dir, reader, time);
        var oldest = await PublishAsync(engine, "<pressure-high-oldest@example>");
        time.Advance(TimeSpan.FromDays(1));
        var records = new ArticleRecord[8];
        for (var i = 0; i < records.Length; i++)
        {
            records[i] = await PublishAsync(engine, $"<pressure-high-{i}@example>");
        }

        time.Advance(Grace);
        var recent = await PublishAsync(engine, "<pressure-high-recent@example>");
        var result = engine.ExpirePressureRetentionBatch(
            Grace,
            ArticlePressureRetentionPolicy.HighExpirationLimit,
            BulkStoragePressureState.High,
            CancellationToken.None);

        Assert.Equal(ArticlePressureRetentionPolicy.HighExpirationLimit, result.Expired);
        Assert.Equal(ArticlePressureRetentionPolicy.HighExpirationLimit, result.Selected);
        AssertEvicted(engine, oldest.ArtId);
        AssertPresent(engine, recent.ArtId);
        Assert.Equal(1, records.Count(record => State(engine, record.ArtId) == ArticleStorageState.Present));
    }

    [Fact]
    public async Task Critical_expires_more_than_pressure_and_keeps_the_grace_window()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 9_200_000);
        await using var engine = Open(dir, reader, time);
        var first = await PublishAsync(engine, "<pressure-critical-a@example>");
        var second = await PublishAsync(engine, "<pressure-critical-b@example>");
        time.Advance(Grace);
        var recent = await PublishAsync(engine, "<pressure-critical-recent@example>");

        var conservative = engine.ExpirePressureRetentionBatch(
            Grace,
            ArticlePressureRetentionPolicy.PressureExpirationLimit,
            BulkStoragePressureState.Pressure,
            CancellationToken.None,
            batchSize: 8);
        Assert.Equal(1, conservative.Expired);

        var aggressive = engine.ExpirePressureRetentionBatch(
            Grace,
            ArticlePressureRetentionPolicy.CriticalExpirationLimit,
            BulkStoragePressureState.Critical,
            CancellationToken.None,
            batchSize: 8);
        Assert.Equal(1, aggressive.Expired);
        AssertEvicted(engine, first.ArtId);
        AssertEvicted(engine, second.ArtId);
        AssertPresent(engine, recent.ArtId);
    }

    [Fact]
    public async Task Emergency_expires_old_articles_and_leaves_ingress_and_files()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time);
        var old = await PublishAsync(engine, "<pressure-emergency-old@example>");
        var segment = Location(engine, old.ArtId).SegmentId;
        time.Advance(Grace);
        var recent = await PublishAsync(engine, "<pressure-emergency-recent@example>");
        engine.SuspendBackgroundPersist = true;
        var ingress = Article("<pressure-emergency-ingress@example>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(ingress, CancellationToken.None)).Outcome);
        var outstanding = engine.Journal.OutstandingRecoverableBytes;
        reader.UsedBytes = 9_600_000;
        var expired = engine.ExpirePressureRetentionBatch(
            Grace,
            ArticlePressureRetentionPolicy.EmergencyExpirationLimit,
            BulkStoragePressureState.Emergency,
            CancellationToken.None);
        Assert.Equal(1, expired.Expired);
        AssertEvicted(engine, old.ArtId);
        AssertPresent(engine, recent.ArtId);
        Assert.True(engine.Catalogue.TryGet(segment, out _));

        _ = await Coordinator(engine, reader).RunOnceAsync(CancellationToken.None);

        Assert.True(engine.TryRead(ingress.ArtId, out _));
        Assert.False(engine.TryRead(old.ArtId, out _));
        Assert.Equal(outstanding, engine.Journal.OutstandingRecoverableBytes);
        AssertPresent(engine, recent.ArtId);
    }

    [Fact]
    public async Task Arrival_boundary_legacy_and_future_rows()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = Open(dir, new MutableCapacityReader(Total, 0), time);
        var record = await PublishAsync(engine, "<pressure-boundary@example>");
        Assert.True(engine.Index.TryGet(record.ArtId, out var published));

        time.SetUtcNow(published.AcceptedUtc.Add(Grace).AddTicks(-1));
        var young = engine.ExpirePressureRetentionBatch(
            Grace,
            8,
            BulkStoragePressureState.Emergency,
            CancellationToken.None);
        Assert.Equal(0, young.Expired);
        Assert.Equal(1, young.TooYoung);

        time.SetUtcNow(published.AcceptedUtc.Add(Grace));
        var due = engine.ExpirePressureRetentionBatch(
            Grace,
            8,
            BulkStoragePressureState.Emergency,
            CancellationToken.None);
        Assert.Equal(1, due.Expired);

        var legacy = Synthetic(engine, "<pressure-legacy@example>", DateTimeOffset.MinValue, 50);
        var future = Synthetic(engine, "<pressure-future@example>", time.GetUtcNow().AddDays(1), 51);
        var skipped = engine.ExpirePressureRetentionBatch(
            Grace,
            8,
            BulkStoragePressureState.Emergency,
            CancellationToken.None);
        Assert.Equal(0, skipped.Expired);
        Assert.Equal(1, skipped.MissingArrival);
        Assert.Equal(1, skipped.FutureArrival);
        Assert.Equal(ArticleStorageState.Present, State(engine, legacy));
        Assert.Equal(ArticleStorageState.Present, State(engine, future));
    }

    [Fact]
    public async Task Known_cold_access_is_preferred_over_a_missing_hint()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = Open(dir, new MutableCapacityReader(Total, 8_200_000), time);
        var unread = await PublishAsync(engine, "<pressure-unread@example>");
        var cold = await PublishAsync(engine, "<pressure-cold@example>");
        time.Advance(TimeSpan.FromHours(1));
        Assert.True(engine.TryRead(cold.ArtId, out _));
        time.Advance(Grace);

        var result = engine.ExpirePressureRetentionBatch(
            Grace,
            ArticlePressureRetentionPolicy.PressureExpirationLimit,
            BulkStoragePressureState.Pressure,
            CancellationToken.None);

        Assert.Equal(1, result.Expired);
        AssertEvicted(engine, cold.ArtId);
        AssertPresent(engine, unread.ArtId);
    }

    [Fact]
    public async Task A_recent_read_is_kept_ahead_of_an_unread_article()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = Open(dir, new MutableCapacityReader(Total, 8_200_000), time);
        var unread = await PublishAsync(engine, "<pressure-unread-2@example>");
        var touched = await PublishAsync(engine, "<pressure-touched@example>");
        time.Advance(Grace);
        Assert.True(engine.TryRead(touched.ArtId, out _));

        var result = engine.ExpirePressureRetentionBatch(
            Grace,
            ArticlePressureRetentionPolicy.PressureExpirationLimit,
            BulkStoragePressureState.Pressure,
            CancellationToken.None);

        Assert.Equal(1, result.Expired);
        AssertEvicted(engine, unread.ArtId);
        AssertPresent(engine, touched.ArtId);
    }

    [Fact]
    public async Task Relocation_during_pressure_expiration_does_not_evict()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = Open(dir, new MutableCapacityReader(Total, 9_000_000), time);
        var record = await PublishAsync(engine, "<pressure-race@example>");
        Assert.True(engine.Index.TryGet(record.ArtId, out var published));
        time.Advance(Grace);
        engine.TestHookBeforeRetentionExpire = candidate =>
        {
            var moved = candidate.Location with { Offset = candidate.Location.Offset + 64 };
            Assert.Equal(
                ArticleRelocateOutcome.Relocated,
                engine.Index.TryRelocate(
                    candidate.ArtId,
                    candidate.Location,
                    moved,
                    record.ArtHash,
                    record.ArtSize));
        };

        var result = engine.ExpirePressureRetentionBatch(
            Grace,
            4,
            BulkStoragePressureState.Critical,
            CancellationToken.None);

        Assert.Equal(0, result.Expired);
        Assert.Equal(1, result.StateChanged);
        Assert.True(engine.Index.TryGet(record.ArtId, out var current));
        Assert.Equal(ArticleStorageState.Present, current.State);
        Assert.Equal(published.AcceptedUtc, current.AcceptedUtc);
        Assert.Equal(published.Location.Offset + 64, current.Location.Offset);
    }

    [Fact]
    public async Task Invalidation_and_a_competing_evict_lose_the_compare_and_transition()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = Open(dir, new MutableCapacityReader(Total, 0), time);
        var invalidated = await PublishAsync(engine, "<pressure-invalid@example>");
        var already = await PublishAsync(engine, "<pressure-already@example>");
        time.Advance(Grace);
        engine.TestHookBeforeRetentionExpire = candidate =>
        {
            if (candidate.ArtId == invalidated.ArtId)
            {
                Assert.True(engine.TryRead(candidate.ArtId, out _));
                Assert.True(engine.TryInvalidate(candidate.ArtId));
            }
            else
            {
                Assert.True(engine.TryEvict(candidate.ArtId));
            }
        };

        var result = engine.ExpirePressureRetentionBatch(
            Grace,
            4,
            BulkStoragePressureState.Critical,
            CancellationToken.None);

        Assert.Equal(0, result.Expired);
        Assert.Equal(2, result.StateChanged);
        Assert.Equal(ArticleStorageState.Invalid, State(engine, invalidated.ArtId));
        Assert.Equal(ArticleStorageState.Evicted, State(engine, already.ArtId));
        Assert.False(engine.TryRead(invalidated.ArtId, out _));
        Assert.False(engine.TryRead(already.ArtId, out _));
    }

    [Fact]
    public async Task Hysteresis_stops_below_the_recovery_target()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 8_200_000);
        await using var engine = Open(dir, reader, time);
        var first = await PublishAsync(engine, "<pressure-hyst-a@example>");
        var second = await PublishAsync(engine, "<pressure-hyst-b@example>");
        var third = await PublishAsync(engine, "<pressure-hyst-c@example>");
        time.Advance(Grace);
        var coordinator = Coordinator(engine, reader);
        AssertPresent(engine, first.ArtId);
        AssertPresent(engine, second.ArtId);
        AssertPresent(engine, third.ArtId);

        _ = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, CountPresent(engine, first, second, third));

        reader.UsedBytes = 7_800_000;
        _ = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, CountPresent(engine, first, second, third));

        reader.UsedBytes = 7_000_000;
        _ = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, CountPresent(engine, first, second, third));
    }

    [Fact]
    public async Task Expired_articles_can_be_reclaimed_by_the_existing_fully_dead_path()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time);
        var first = await PublishAsync(engine, "<pressure-reclaim-a@example>");
        var second = await PublishAsync(engine, "<pressure-reclaim-b@example>");
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var segment = Location(engine, first.ArtId).SegmentId;
        time.Advance(Grace);
        reader.UsedBytes = 9_200_000;

        var result = await Coordinator(engine, reader).RunOnceAsync(CancellationToken.None);

        Assert.Equal(nameof(BulkStoragePressureState.Critical), result.BulkPressureState);
        Assert.False(engine.TryRead(first.ArtId, out _));
        Assert.False(engine.TryRead(second.ArtId, out _));
        Assert.False(engine.Catalogue.TryGet(segment, out _));
    }

    private static int CountPresent(
        FileArticleStorageEngine engine,
        params ArticleRecord[] records) =>
        records.Count(record =>
            engine.Index.TryGet(record.ArtId, out var row)
            && row.State == ArticleStorageState.Present);

    private static StorageMaintenanceCoordinator Coordinator(
        FileArticleStorageEngine engine,
        MutableCapacityReader reader,
        TimeSpan? maxRetentionAge = null)
    {
        _ = reader;
        return new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: 0, minimumDeadRatio: 10),
            logger: NullLogger.Instance,
            maxRetentionAge: maxRetentionAge ?? TimeSpan.Zero,
            bulkPressure: new BulkStoragePressureOptions
            {
                MinimumRetentionAge = Grace,
            });
    }

    private static FileArticleStorageEngine Open(TempDir dir, IStorageCapacityReader reader, TimeProvider time) =>
        FileArticleStorageEngine.Open(
            dir.Options with
            {
                CapacityMaximumUtilization = 100,
                CapacityCompactionHeadroom = 0,
                CapacityMaximumUsageCapacity = 100,
                CapacityFreeCapacity = 1,
            },
            logger: NullLogger.Instance,
            timeProvider: time,
            capacityReader: reader);

    private static async Task<ArticleRecord> PublishAsync(FileArticleStorageEngine engine, string messageId)
    {
        var record = Article(messageId);
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        return record;
    }

    private static ArticleRecord Article(string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase28\r\n\r\nbody\r\n");
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private static ArticleId Synthetic(
        FileArticleStorageEngine engine,
        string messageId,
        DateTimeOffset acceptedUtc,
        ulong sequence)
    {
        var id = ArticleId.FromMessageId(Encoding.ASCII.GetBytes(messageId));
        Assert.True(engine.Index.TryCommitPresent(new StoredArticleMetadata(
            id,
            3,
            10,
            new StoredArticleLocation(new SegmentId(9), (long)sequence, 10),
            ArticleStorageState.Present,
            Arrival,
            sequence,
            acceptedUtc)));
        return id;
    }

    private static ArticleStorageState State(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        return row.State;
    }

    private static StoredArticleLocation Location(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        return row.Location;
    }

    private static void AssertPresent(FileArticleStorageEngine engine, ArticleId artId) =>
        Assert.Equal(ArticleStorageState.Present, State(engine, artId));

    private static void AssertEvicted(FileArticleStorageEngine engine, ArticleId artId) =>
        Assert.Equal(ArticleStorageState.Evicted, State(engine, artId));

    private sealed class MutableCapacityReader : IStorageCapacityReader
    {
        public MutableCapacityReader(long total, long used)
        {
            TotalBytes = total;
            UsedBytes = used;
        }

        public long TotalBytes { get; }

        public long UsedBytes { get; set; }

        public StorageCapacitySnapshot Read() =>
            new(TotalBytes, UsedBytes, Math.Max(0L, TotalBytes - UsedBytes));
    }

    private sealed class TempDir : IDisposable
    {
        private TempDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public ArticleStorageRuntimeOptions Options { get; }

        private string Root { get; }

        public static TempDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase28-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "control"));
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            return new TempDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: Path.Combine(root, "control"),
                    SegmentDir: Path.Combine(root, "cache"),
                    JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                    SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes,
                    MaxSegmentSealDelay: TimeSpan.FromHours(1)));
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
