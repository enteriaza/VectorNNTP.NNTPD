using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Tests.Fixtures;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Age eligibility is a pure decision. It does not evict, and a missing arrival time does not expire.
/// </summary>
public sealed class RetentionEligibilityTests
{
    private static readonly DateTimeOffset Arrival = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    [Fact]
    public void MaxRetentionAge_is_disabled_by_default()
    {
        Assert.Equal(TimeSpan.Zero, new ArticleStorageOptions().MaxRetentionAge);
        Assert.Equal(TimeSpan.Zero, ArticleStorageOptions.DefaultMaxRetentionAge);
        var bound = new ArticleStorageOptions();
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "VectorNNTP.StorageServer.json"))
            .Build()
            .GetSection(StorageServerOptions.SectionName)
            .GetSection("Storage")
            .Bind(bound);
        Assert.Equal(TimeSpan.Zero, bound.MaxRetentionAge);
    }

    [Fact]
    public void Positive_MaxRetentionAge_is_accepted()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.MaxRetentionAge = TimeSpan.FromDays(90);
        var result = new StorageServerOptionsValidator().Validate(Options.DefaultName, options);
        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
        Assert.Equal(TimeSpan.FromDays(90), options.Storage.MaxRetentionAge);
    }

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
    public void Zero_MaxRetentionAge_disables_expiration()
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.MaxRetentionAge = TimeSpan.Zero;
        Assert.True(new StorageServerOptionsValidator().Validate(Options.DefaultName, options).Succeeded);
        Assert.False(ArticleRetentionPolicy.IsExpirationEligible(
            bulkCommitted: true,
            Arrival,
            TimeSpan.Zero,
            Arrival.AddYears(10)));
        Assert.False(ArticleRetentionPolicy.IsExpirationEligible(
            bulkCommitted: true,
            Arrival,
            TimeSpan.FromTicks(-1),
            Arrival.AddYears(10)));
    }

    [Fact]
    public void Age_boundaries_are_deterministic()
    {
        var now = Arrival.Add(MaxAge);
        Assert.False(ArticleRetentionPolicy.IsExpirationEligible(true, Arrival, MaxAge, now.AddTicks(-1)));
        Assert.True(ArticleRetentionPolicy.IsExpirationEligible(true, Arrival, MaxAge, now));
        Assert.True(ArticleRetentionPolicy.IsExpirationEligible(true, Arrival, MaxAge, now.AddDays(1)));
        Assert.False(ArticleRetentionPolicy.IsExpirationEligible(true, now.AddMinutes(1), MaxAge, now));
        Assert.False(ArticleRetentionPolicy.IsExpirationEligible(true, null, MaxAge, now.AddYears(1)));
        Assert.False(ArticleRetentionPolicy.IsExpirationEligible(
            true,
            DateTimeOffset.MinValue,
            MaxAge,
            now.AddYears(1)));
    }

    [Fact]
    public async Task Ingress_article_is_not_age_expired()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        engine.SuspendBackgroundPersist = true;
        var record = Article("<phase21-ingress@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out var accept));
        time.Advance(TimeSpan.FromDays(400));
        Assert.False(ArticleRetentionPolicy.IsExpirationEligible(
            bulkCommitted: false,
            accept.AcceptedUtc,
            MaxAge,
            time.GetUtcNow()));
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task Eligibility_does_not_evict_when_arrival_is_durable()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var record = Article("<phase21-bulk@seg.test>");
        DateTimeOffset accepted;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out var accept));
            accepted = accept.AcceptedUtc;
            time.Advance(TimeSpan.FromHours(2));
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var published));
            Assert.Equal(ArticleStorageState.Present, published.State);
            Assert.True(published.LastAccessUtc > accepted);
            Assert.True(ArticleRetentionPolicy.IsExpirationEligible(true, accepted, TimeSpan.FromHours(1), time.GetUtcNow()));
            Assert.True(engine.TryRead(record.ArtId, out _));
            Assert.False(engine.Journal.TryGetOutstanding(record.ArtId, out _));
        }

        time.Advance(TimeSpan.FromDays(40));
        await using var restarted = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.True(restarted.Index.TryGet(record.ArtId, out var restored));
        Assert.Equal(ArticleStorageState.Present, restored.State);
        Assert.Equal(accepted, restored.AcceptedUtc);
        Assert.False(restarted.Journal.TryGetOutstanding(record.ArtId, out _));
        Assert.True(ArticleRetentionPolicy.IsExpirationEligible(
            bulkCommitted: true,
            restored.AcceptedUtc,
            MaxAge,
            time.GetUtcNow()));
        Assert.False(ArticleRetentionPolicy.IsExpirationEligible(
            bulkCommitted: true,
            arrivalUtc: null,
            MaxAge,
            time.GetUtcNow()));
        Assert.True(restarted.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.NotEqual(accepted, restored.LastAccessUtc);
    }

    [Fact]
    public async Task Eviction_hides_every_copy_while_segment_bytes_remain()
    {
        using var dir = TempDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        var record = Article("<phase21-evict@seg.test>");
        await using (var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache))
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.TryRead(record.ArtId, out _));
            Assert.True(engine.CacheArticleReadCount >= 1 || cache.TryGet(record.ArtId, out _));
            Assert.True(cache.Remove(record.ArtId) || !cache.TryGet(record.ArtId, out _));
            Assert.True(engine.TryRead(record.ArtId, out var cold));
            Assert.True(cold.ArtData.Span.SequenceEqual(record.ArtData.Span));
            Assert.Equal(ArticleStorageState.Present, engine.Index.TryGet(record.ArtId, out var still) ? still.State : default);

            Assert.True(engine.Index.TryGet(record.ArtId, out var present));
            Assert.True(engine.TryEvict(record.ArtId));
            Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(in record));
            Assert.False(engine.TryRead(record.ArtId, out _));
            Assert.False(cache.TryGet(record.ArtId, out _));
            Assert.True(engine.Index.TryGet(record.ArtId, out var tombstone));
            Assert.Equal(ArticleStorageState.Evicted, tombstone.State);
            Assert.Equal(present.Location, tombstone.Location);
            Assert.True(engine.Segments.TryReadProven(
                tombstone.Location,
                record.ArtId,
                record.ArtHash,
                record.ArtSize,
                out var physical));
            Assert.True(physical.Span.SequenceEqual(record.ArtData.Span));
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options, articleCache: new ArticleMemoryCache(4L * 1024 * 1024));
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.False(restarted.TryRead(record.ArtId, out _));
        Assert.True(restarted.Index.TryGet(record.ArtId, out var restored));
        Assert.Equal(ArticleStorageState.Evicted, restored.State);
        Assert.True(restarted.Segments.TryReadProven(
            restored.Location,
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            out _));
    }

    private static ArticleRecord Article(string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase21\r\n\r\nbody\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase21-" + Guid.NewGuid().ToString("N"));
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
