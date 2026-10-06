using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Tests.Fixtures;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Age expiration writes the existing Evicted tombstone for eligible Present rows.
/// It does not read or delete segment bytes.
/// </summary>
public sealed class RetentionExpirationTests
{
    private static readonly DateTimeOffset Arrival = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    [Fact]
    public void Negative_MaxRetentionAge_is_rejected()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.MaxRetentionAge = TimeSpan.FromTicks(-1);
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            static failure => failure.Contains("MaxRetentionAge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabled_policy_does_not_scan_or_mutate()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        var record = await PublishAsync(engine, "<phase23-disabled@seg.test>");
        time.Advance(TimeSpan.FromDays(400));
        var writes = engine.Index.DurableWriteCount;
        var result = engine.ExpireRetentionBatch(TimeSpan.Zero, CancellationToken.None, batchSize: 8);
        Assert.True(result.Disabled);
        Assert.Equal(0, result.EntriesVisited);
        Assert.Equal(0, result.Expired);
        Assert.Equal(writes, engine.Index.DurableWriteCount);
        AssertPresent(engine, record.ArtId);
    }

    [Fact]
    public async Task Young_article_stays_present_and_exact_boundary_expires()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        var record = await PublishAsync(engine, "<phase23-boundary@seg.test>");
        Assert.True(engine.Index.TryGet(record.ArtId, out var published));
        var accepted = published.AcceptedUtc;

        time.SetUtcNow(accepted.Add(MaxAge).AddTicks(-1));
        var young = engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 8);
        Assert.Equal(0, young.Expired);
        Assert.Equal(1, young.NotEligible);
        AssertPresent(engine, record.ArtId);

        time.SetUtcNow(accepted.Add(MaxAge));
        var due = engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 8);
        Assert.Equal(1, due.Expired);
        AssertEvicted(engine, record.ArtId, accepted, published.Location);
    }

    [Fact]
    public async Task Future_and_min_value_arrivals_stay_present()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        var future = Synthetic(engine, "<phase23-future@seg.test>", Arrival.AddYears(10), sequence: 50);
        var unknown = Synthetic(engine, "<phase23-min@seg.test>", DateTimeOffset.MinValue, sequence: 51);
        time.Advance(TimeSpan.FromDays(30));
        var result = engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 8);
        Assert.Equal(0, result.Expired);
        Assert.Equal(2, result.NotEligible);
        Assert.Equal(ArticleStorageState.Present, State(engine, future));
        Assert.Equal(ArticleStorageState.Present, State(engine, unknown));
    }

    [Fact]
    public async Task Ingress_only_article_is_not_expired()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        engine.SuspendBackgroundPersist = true;
        var record = Article("<phase23-ingress@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        time.Advance(TimeSpan.FromDays(400));
        var result = engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 8);
        Assert.Equal(0, result.EntriesVisited);
        Assert.Equal(0, result.Expired);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Evicted_and_invalid_rows_are_not_rewritten()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        var evicted = await PublishAsync(engine, "<phase23-already-evicted@seg.test>");
        var invalid = await PublishAsync(engine, "<phase23-invalid@seg.test>");
        Assert.True(engine.TryEvict(evicted.ArtId));
        Assert.True(engine.TryInvalidate(invalid.ArtId));
        time.Advance(TimeSpan.FromDays(40));
        var writes = engine.Index.DurableWriteCount;
        var result = engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 8);
        Assert.Equal(0, result.PresentEvaluated);
        Assert.Equal(0, result.Expired);
        Assert.Equal(writes, engine.Index.DurableWriteCount);
        Assert.Equal(ArticleStorageState.Evicted, State(engine, evicted.ArtId));
        Assert.Equal(ArticleStorageState.Invalid, State(engine, invalid.ArtId));
    }

    [Fact]
    public async Task Eligible_batch_is_bounded_and_a_repeat_is_idempotent()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        var first = await PublishAsync(engine, "<phase23-batch-a@seg.test>");
        var second = await PublishAsync(engine, "<phase23-batch-b@seg.test>");
        var third = await PublishAsync(engine, "<phase23-batch-c@seg.test>");
        time.Advance(MaxAge);

        var firstPass = engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 1);
        Assert.Equal(1, firstPass.Expired);
        Assert.Equal(1, firstPass.EntriesVisited);
        Assert.Equal(ArticleStorageState.Evicted, State(engine, first.ArtId));
        AssertPresent(engine, second.ArtId);
        AssertPresent(engine, third.ArtId);
        Assert.True(engine.TryRead(second.ArtId, out _));

        Assert.Equal(1, engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 1).Expired);
        Assert.Equal(1, engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 1).Expired);
        var writes = engine.Index.DurableWriteCount;
        var again = engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 8);
        Assert.Equal(0, again.Expired);
        Assert.Equal(writes, engine.Index.DurableWriteCount);
        Assert.True(engine.Index.TryGet(first.ArtId, out var tombstone));
        Assert.True(engine.Segments.TryReadProven(
            tombstone.Location,
            first.ArtId,
            first.ArtHash,
            first.ArtSize,
            out var physical));
        Assert.True(physical.Span.SequenceEqual(first.ArtData.Span));
        Assert.False(engine.TryRead(first.ArtId, out _));
    }

    [Fact]
    public async Task Expiration_preserves_arrival_and_hides_cache_and_restart()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        var record = Article("<phase23-restart@seg.test>");
        DateTimeOffset accepted;
        StoredArticleLocation location;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time, articleCache: cache))
        {
            await PublishAsync(engine, record);
            Assert.True(engine.Index.TryGet(record.ArtId, out var published));
            accepted = published.AcceptedUtc;
            location = published.Location;
            _ = cache.Remove(record.ArtId);
            Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(in record));
            time.Advance(MaxAge);
            Assert.Equal(1, engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 4).Expired);
            AssertEvicted(engine, record.ArtId, accepted, location);
            Assert.False(engine.TryRead(record.ArtId, out _));
            Assert.False(cache.TryGet(record.ArtId, out _));
            Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(in record));
            Assert.False(engine.TryRead(record.ArtId, out _));
            Assert.False(cache.TryGet(record.ArtId, out _));
        }

        time.Advance(TimeSpan.FromDays(3));
        await using var restarted = FileArticleStorageEngine.Open(dir.Options, timeProvider: time, articleCache: cache);
        AssertEvicted(restarted, record.ArtId, accepted, location);
        Assert.False(restarted.TryRead(record.ArtId, out _));
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(in record));
        Assert.False(restarted.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Eligible_article_stays_present_until_its_batch()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        _ = await PublishAsync(engine, "<phase23-first@seg.test>");
        var held = await PublishAsync(engine, "<phase23-held@seg.test>");
        time.Advance(MaxAge);
        _ = engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 1);
        AssertPresent(engine, held.ArtId);
        Assert.True(engine.TryRead(held.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(held.ArtData.Span));
    }

    [Fact]
    public async Task Cancelled_batch_keeps_completed_tombstones_and_the_rest()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        var first = await PublishAsync(engine, "<phase23-cancel-a@seg.test>");
        var second = await PublishAsync(engine, "<phase23-cancel-b@seg.test>");
        time.Advance(MaxAge);
        using var cancel = new CancellationTokenSource();
        engine.TestHookBeforeRetentionExpire = candidate =>
        {
            if (candidate.ArtId == second.ArtId)
            {
                cancel.Cancel();
            }
        };
        _ = Assert.Throws<OperationCanceledException>(() =>
            engine.ExpireRetentionBatch(MaxAge, cancel.Token, batchSize: 8));
        Assert.Equal(ArticleStorageState.Evicted, State(engine, first.ArtId));
        AssertPresent(engine, second.ArtId);
        Assert.True(engine.TryRead(second.ArtId, out _));
        Assert.Equal(1, engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 8).Expired);
        Assert.Equal(ArticleStorageState.Evicted, State(engine, second.ArtId));
    }

    [Fact]
    public async Task Stale_candidate_does_not_overwrite_a_relocation()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        var record = await PublishAsync(engine, "<phase23-race@seg.test>");
        Assert.True(engine.Index.TryGet(record.ArtId, out var published));
        time.Advance(MaxAge);
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
        var result = engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 4);
        Assert.Equal(0, result.Expired);
        Assert.Equal(1, result.StateChanged);
        Assert.True(engine.Index.TryGet(record.ArtId, out var current));
        Assert.Equal(ArticleStorageState.Present, current.State);
        Assert.Equal(published.AcceptedUtc, current.AcceptedUtc);
        Assert.Equal(published.Location.Offset + 64, current.Location.Offset);
        Assert.NotEqual(ArticleStorageState.Evicted, current.State);
    }

    [Fact]
    public void Schema2_row_with_min_value_is_never_expired()
    {
        using var dir = TempDir.Create();
        var row = new StoredArticleMetadata(
            ArticleId.FromMessageId("<phase23-schema2@seg.test>"u8),
            17,
            10,
            new StoredArticleLocation(new SegmentId(4), 8, 10),
            ArticleStorageState.Present,
            Arrival,
            6UL,
            DateTimeOffset.MinValue);
        File.WriteAllBytes(
            Path.Combine(dir.Options.ControlDir, FileArticleIndex.IndexFileName),
            EncodeSchema2(row));
        var time = new FakeTimeProvider(Arrival.AddYears(5));
        using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        var writes = engine.Index.DurableWriteCount;
        var result = engine.ExpireRetentionBatch(MaxAge, CancellationToken.None, batchSize: 8);
        Assert.Equal(0, result.Expired);
        Assert.Equal(1, result.NotEligible);
        Assert.Equal(writes, engine.Index.DurableWriteCount);
        Assert.True(engine.Index.TryGet(row.ArtId, out var restored));
        Assert.Equal(ArticleStorageState.Present, restored.State);
        Assert.Equal(DateTimeOffset.MinValue, restored.AcceptedUtc);
    }

    [Fact]
    public async Task Maintenance_cycle_expires_without_deleting_the_segment()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        var record = await PublishAsync(engine, "<phase23-maint@seg.test>");
        Assert.True(engine.Index.TryGet(record.ArtId, out var published));
        var age = TimeSpan.FromMinutes(1);
        time.Advance(age);
        var coordinator = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(long.MaxValue, 100),
            maxRetentionAge: age);
        var maintenance = await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.False(maintenance.ReclamationAttempted);
        Assert.False(maintenance.CompactionAttempted);
        AssertEvicted(engine, record.ArtId, published.AcceptedUtc, published.Location);
        Assert.True(engine.Segments.TryReadProven(
            published.Location,
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            out _));
    }

    private static async Task<ArticleRecord> PublishAsync(FileArticleStorageEngine engine, string messageId)
    {
        var record = Article(messageId);
        await PublishAsync(engine, record);
        return record;
    }

    private static async Task PublishAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
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

    private static void AssertPresent(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.Equal(ArticleStorageState.Present, State(engine, artId));
        Assert.True(engine.TryRead(artId, out _));
    }

    private static void AssertEvicted(
        FileArticleStorageEngine engine,
        ArticleId artId,
        DateTimeOffset acceptedUtc,
        StoredArticleLocation location)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        Assert.Equal(ArticleStorageState.Evicted, row.State);
        Assert.Equal(acceptedUtc, row.AcceptedUtc);
        Assert.Equal(location, row.Location);
        Assert.False(engine.TryRead(artId, out _));
    }

    private static ArticleStorageState State(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        return row.State;
    }

    private static byte[] EncodeSchema2(in StoredArticleMetadata metadata)
    {
        var buffer = new byte[ArticleIndexRecordCodec.Schema2RecordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)buffer.Length);
        buffer[4] = ArticleIndexRecordCodec.Schema2Version;
        var offset = 8;
        metadata.ArtId.CopyTo(buffer.AsSpan(offset, ArticleId.Length));
        offset += ArticleId.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, 8), metadata.ArtHash);
        offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), metadata.ArtSize);
        offset += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, 8), metadata.Location.SegmentId.Value);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), metadata.Location.Offset);
        offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), metadata.Location.Length);
        offset += 4;
        buffer[offset++] = (byte)metadata.State;
        offset += 3;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), metadata.LastAccessUtc.UtcTicks);
        offset += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, 8), metadata.Sequence);
        offset += 8;
        var crc = Crc32.HashToUInt32(buffer.AsSpan(0, offset));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), crc);
        return buffer;
    }

    private static ArticleRecord Article(string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase23\r\n\r\nbody\r\n");
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase23-" + Guid.NewGuid().ToString("N"));
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
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
