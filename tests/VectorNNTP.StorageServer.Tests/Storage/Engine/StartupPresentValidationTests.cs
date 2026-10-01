using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog.Events;
using Serilog.Extensions.Logging;
using VectorNNTP.StorageServer.Tests.Logging;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Tests.Fixtures;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Startup proves every Present index row before the engine is published.
/// </summary>
public sealed class StartupPresentValidationTests
{
    [Fact]
    public async Task Valid_present_stays_present_and_presence_is_true()
    {
        var prepared = await PrepareAsync("<startup-valid@seg.test>");
        using (prepared.Dir)
        {
            var indexLength = IndexLength(prepared.Dir);
            await using var service = await StartReadyAsync(prepared.Dir);
            Assert.True(service.IsReady);
            Assert.Equal(indexLength, IndexLength(prepared.Dir));
            Assert.True(await PresenceAsync(service, prepared.Record.ArtId));
            Assert.True(service.Engine.Index.TryGet(prepared.Record.ArtId, out var meta));
            Assert.Equal(ArticleStorageState.Present, meta.State);
            Assert.Equal(prepared.Metadata.Location, meta.Location);
            Assert.Equal(prepared.Metadata.ArtHash, meta.ArtHash);
            Assert.Equal(prepared.Metadata.ArtSize, meta.ArtSize);
            Assert.Equal(prepared.Metadata.Sequence, meta.Sequence);
            Assert.True(service.Engine.TryRead(prepared.Record.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(prepared.Record.ArtData.Span));
        }
    }

    [Fact]
    public async Task Missing_segment_becomes_invalid_and_presence_is_false()
    {
        var prepared = await PrepareAsync("<startup-missing@seg.test>");
        using (prepared.Dir)
        {
            File.Delete(SegmentPath(prepared.Dir, prepared.Metadata.Location));
            await using var service = await StartReadyAsync(prepared.Dir);
            Assert.True(service.IsReady);
            await AssertInvalidAsync(service, prepared);
        }
    }

    [Fact]
    public async Task Retired_segment_becomes_invalid()
    {
        var prepared = await PrepareAsync("<startup-retired@seg.test>");
        using (prepared.Dir)
        {
            var path = SegmentPath(prepared.Dir, prepared.Metadata.Location);
            var retired = Path.Combine(
                Path.GetDirectoryName(path)!,
                Path.GetFileNameWithoutExtension(path) + SegmentFileNames.RetiredSuffix);
            File.Move(path, retired);
            await using var service = await StartReadyAsync(prepared.Dir);
            await AssertInvalidAsync(service, prepared);
            Assert.True(service.Engine.Segments.TryGetSegmentInfo(prepared.Metadata.Location.SegmentId, out var info));
            Assert.Equal(SegmentState.Retired, info.State);
        }
    }

    [Fact]
    public async Task Out_of_range_location_becomes_invalid()
    {
        var prepared = await PrepareAsync("<startup-range@seg.test>");
        using (prepared.Dir)
        {
            var path = SegmentPath(prepared.Dir, prepared.Metadata.Location);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                stream.SetLength(0);
                stream.Flush(true);
            }

            await using var service = await StartReadyAsync(prepared.Dir);
            await AssertInvalidAsync(service, prepared);
        }
    }

    [Fact]
    public async Task Bad_crc_becomes_invalid()
    {
        var prepared = await PrepareAsync("<startup-crc@seg.test>");
        using (prepared.Dir)
        {
            CorruptRecordPayload(prepared.Dir.Options.SegmentDir, prepared.Metadata.Location);
            await using var service = await StartReadyAsync(prepared.Dir);
            await AssertInvalidAsync(service, prepared);
        }
    }

    [Fact]
    public async Task Wrong_article_id_becomes_invalid()
    {
        var prepared = await PrepareAsync("<startup-ida@seg.test>");
        using (prepared.Dir)
        {
            var other = CreateRecord("<startup-idb@seg.test>");
            Assert.Equal(prepared.Record.ArtSize, other.ArtSize);
            OverwriteRecord(
                prepared.Dir,
                prepared.Metadata.Location,
                SegmentRecordCodec.Encode(other.ArtId, other.ArtHash, other.ArtData.Span));
            await using var service = await StartReadyAsync(prepared.Dir);
            await AssertInvalidAsync(service, prepared);
            Assert.False(service.Engine.Index.TryGet(other.ArtId, out _));
        }
    }

    [Fact]
    public async Task Wrong_art_hash_becomes_invalid()
    {
        var prepared = await PrepareAsync("<startup-hash@seg.test>");
        using (prepared.Dir)
        {
            var payload = prepared.Record.ArtData.ToArray();
            payload[^1] ^= 0x5A;
            var hash = XxHash3.HashToUInt64(payload);
            Assert.NotEqual(prepared.Record.ArtHash, hash);
            OverwriteRecord(
                prepared.Dir,
                prepared.Metadata.Location,
                SegmentRecordCodec.Encode(prepared.Record.ArtId, hash, payload));
            await using var service = await StartReadyAsync(prepared.Dir);
            await AssertInvalidAsync(service, prepared);
        }
    }

    [Fact]
    public async Task Wrong_art_size_becomes_invalid()
    {
        var dir = TempStorageDir.Create();
        using (dir)
        {
            var record = CreateRecord("<startup-size@seg.test>");
            StoredArticleMetadata forged = default;
            await using (var engine = FileArticleStorageEngine.Open(dir.Options))
            {
                _ = await AcceptCloseAndLocateAsync(engine, record);
                Assert.True(engine.TryEvict(record.ArtId));
                Assert.True(engine.Index.TryGet(record.ArtId, out var evicted));
                forged = evicted with
                {
                    State = ArticleStorageState.Present,
                    ArtSize = evicted.ArtSize + 1,
                };
                Assert.True(engine.Index.TryCommitPresent(in forged));
            }

            await using var service = await StartReadyAsync(dir);
            Assert.False(await PresenceAsync(service, record.ArtId));
            Assert.True(service.Engine.Index.TryGet(record.ArtId, out var meta));
            Assert.Equal(ArticleStorageState.Invalid, meta.State);
            Assert.Equal(forged.Location, meta.Location);
            Assert.Equal(forged.ArtHash, meta.ArtHash);
            Assert.Equal(forged.ArtSize, meta.ArtSize);
            Assert.Equal(forged.Sequence, meta.Sequence);
        }
    }

    [Fact]
    public async Task Evicted_row_with_missing_segment_stays_evicted()
    {
        var dir = TempStorageDir.Create();
        using (dir)
        {
            var record = CreateRecord("<startup-evicted@seg.test>");
            StoredArticleMetadata evicted = default;
            await using (var engine = FileArticleStorageEngine.Open(dir.Options))
            {
                var (_, _, location) = await AcceptCloseAndLocateAsync(engine, record);
                Assert.True(engine.TryEvict(record.ArtId));
                Assert.True(engine.Index.TryGet(record.ArtId, out evicted));
                Assert.Equal(ArticleStorageState.Evicted, evicted.State);
                Assert.Equal(location, evicted.Location);
            }

            File.Delete(SegmentPath(dir, evicted.Location));
            var indexLength = IndexLength(dir);
            await using var service = await StartReadyAsync(dir);
            Assert.Equal(indexLength, IndexLength(dir));
            Assert.False(await PresenceAsync(service, record.ArtId));
            Assert.True(service.Engine.Index.TryGet(record.ArtId, out var meta));
            Assert.Equal(ArticleStorageState.Evicted, meta.State);
            Assert.Equal(evicted.Location, meta.Location);
            Assert.Equal(evicted.Sequence, meta.Sequence);
        }
    }

    [Fact]
    public async Task Restart_after_invalidation_stays_invalid()
    {
        var prepared = await PrepareAsync("<startup-restart@seg.test>");
        using (prepared.Dir)
        {
            CorruptRecordPayload(prepared.Dir.Options.SegmentDir, prepared.Metadata.Location);
            var first = await StartReadyAsync(prepared.Dir);
            try
            {
                await AssertInvalidAsync(first, prepared);
            }
            finally
            {
                await first.DisposeAsync();
            }

            var indexLength = IndexLength(prepared.Dir);
            await using var second = await StartReadyAsync(prepared.Dir);
            Assert.True(second.IsReady);
            await AssertInvalidAsync(second, prepared);
            Assert.Equal(indexLength, IndexLength(prepared.Dir));
        }
    }

    [Fact]
    public async Task Invalidation_write_failure_does_not_publish_the_engine()
    {
        var prepared = await PrepareAsync("<startup-fail@seg.test>");
        using (prepared.Dir)
        {
            File.Delete(SegmentPath(prepared.Dir, prepared.Metadata.Location));
            var service = CreateService(prepared.Dir);
            service.TestBeforeRecover = engine =>
            {
                engine.TestHookBeforeExpectedInvalidation = (_, _) =>
                    throw new IOException("startup-invalidation-failed");
            };

            var thrown = await Assert.ThrowsAsync<IOException>(() => service.StartAsync(CancellationToken.None));
            Assert.Equal("startup-invalidation-failed", thrown.Message);
            Assert.False(service.IsReady);
            _ = Assert.Throws<InvalidOperationException>(() => service.Engine);

            await using var check = FileArticleStorageEngine.Open(prepared.Dir.Options);
            Assert.True(check.Index.TryGet(prepared.Record.ArtId, out var meta));
            Assert.Equal(ArticleStorageState.Present, meta.State);
            Assert.Equal(prepared.Metadata.Location, meta.Location);
            Assert.Equal(prepared.Metadata.ArtHash, meta.ArtHash);
            Assert.Equal(prepared.Metadata.ArtSize, meta.ArtSize);
            Assert.Equal(prepared.Metadata.Sequence, meta.Sequence);
        }
    }

    [Fact]
    public async Task Invalidation_capacity_denial_fails_recovery_and_leaves_present()
    {
        var prepared = await PrepareAsync("<startup-capacity@seg.test>");
        using (prepared.Dir)
        {
            File.Delete(SegmentPath(prepared.Dir, prepared.Metadata.Location));
            var reader = new FullVolumeReader(total: 1_000_000);
            await using var engine = FileArticleStorageEngine.Open(
                prepared.Dir.Options with { CapacityAdmissionEnabled = true },
                capacityReader: reader);
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.RecoverAsync(CancellationToken.None));
            Assert.Contains("could not be invalidated", thrown.Message, StringComparison.Ordinal);
            Assert.True(engine.Index.TryGet(prepared.Record.ArtId, out var meta));
            Assert.Equal(ArticleStorageState.Present, meta.State);
            Assert.Equal(prepared.Metadata.Location, meta.Location);
            Assert.Equal(prepared.Metadata.Sequence, meta.Sequence);
        }
    }

    [Fact]
    public async Task Recovery_relocation_destination_stays_present()
    {
        var dir = TempStorageDir.Create();
        using (dir)
        {
            var record = CreateRecord("<startup-reloc@seg.test>");
            SegmentId sourceId = default;
            await using (var engine = FileArticleStorageEngine.Open(dir.Options))
            {
                ulong generation;
                (sourceId, generation, _) = await AcceptCloseAndLocateAsync(engine, record);
                var compactionId = engine.Journal.AllocateCompactionId();
                Assert.Equal(
                    JournalAppendOutcome.Applied,
                    await engine.Journal.AppendCompactionBeginAsync(
                        new JournalCompactionBeginRecord(1, compactionId, sourceId, generation),
                        CancellationToken.None));
                engine.TestRelocationFaultPoint =
                    FileArticleStorageEngine.RelocationFaultPoint.AfterWrittenBeforeIndex;
                _ = await Assert.ThrowsAsync<IOException>(() =>
                    engine.RelocateArticleAsync(
                        compactionId,
                        1,
                        sourceId,
                        generation,
                        record.ArtId,
                        CancellationToken.None));
                Assert.True(engine.Index.TryGet(record.ArtId, out var before));
                Assert.Equal(ArticleStorageState.Present, before.State);
                Assert.Equal(sourceId, before.Location.SegmentId);
            }

            await using var service = await StartReadyAsync(dir);
            Assert.True(await PresenceAsync(service, record.ArtId));
            Assert.True(service.Engine.Index.TryGet(record.ArtId, out var meta));
            Assert.Equal(ArticleStorageState.Present, meta.State);
            Assert.NotEqual(sourceId, meta.Location.SegmentId);
            Assert.Equal(record.ArtHash, meta.ArtHash);
            Assert.Equal(record.ArtSize, meta.ArtSize);
            Assert.True(service.Engine.TryRead(record.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
            Assert.Equal(meta.Location, read.Metadata.Location);
        }
    }

    private static async Task AssertInvalidAsync(StorageEngineApplicationService service, Prepared prepared)
    {
        Assert.True(service.IsReady);
        Assert.False(await PresenceAsync(service, prepared.Record.ArtId));
        Assert.True(service.Engine.Index.TryGet(prepared.Record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Invalid, meta.State);
        Assert.Equal(prepared.Metadata.Location, meta.Location);
        Assert.Equal(prepared.Metadata.ArtHash, meta.ArtHash);
        Assert.Equal(prepared.Metadata.ArtSize, meta.ArtSize);
        Assert.Equal(prepared.Metadata.Sequence, meta.Sequence);
        Assert.False(service.Engine.TryRead(prepared.Record.ArtId, out _));
    }

    [Fact]
    public async Task Valid_present_does_not_emit_startup_invalidation()
    {
        var prepared = await PrepareAsync("<startup-log-valid@seg.test>");
        using (prepared.Dir)
        {
            var sink = new CollectingSink();
            await using var service = await StartReadyAsync(prepared.Dir, sink);
            Assert.True(service.IsReady);
            Assert.DoesNotContain(sink.Events, static e => IsEvent(e, StartupPresentInvalidatedEventId));
            Assert.Contains(sink.Events, static e => IsEvent(e, EngineReadyEventId));
        }
    }

    [Fact]
    public async Task Failed_proof_emits_one_invalidation_event_before_engine_ready()
    {
        var prepared = await PrepareAsync("<startup-log-missing@seg.test>");
        using (prepared.Dir)
        {
            File.Delete(SegmentPath(prepared.Dir, prepared.Metadata.Location));
            var sink = new CollectingSink();
            await using var service = await StartReadyAsync(prepared.Dir, sink);
            Assert.True(service.IsReady);
            await AssertInvalidAsync(service, prepared);

            var invalidated = sink.Events.Where(static e => IsEvent(e, StartupPresentInvalidatedEventId)).ToArray();
            var only = Assert.Single(invalidated);
            Assert.Equal(LogEventLevel.Warning, only.Level);
            Assert.Contains("previously Present", only.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            Assert.Contains("recovery is continuing", only.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            Assert.Equal(prepared.Metadata.ArtId.ToLowerHexString(), Scalar(only, "ArtId"));
            Assert.Equal(prepared.Metadata.Sequence.ToString(CultureInfo.InvariantCulture), Scalar(only, "Sequence"));
            Assert.Equal(prepared.Metadata.Location.SegmentId.Value.ToString(CultureInfo.InvariantCulture), Scalar(only, "SegmentId"));
            Assert.Equal(prepared.Metadata.Location.Offset.ToString(CultureInfo.InvariantCulture), Scalar(only, "Offset"));
            Assert.Equal(prepared.Metadata.Location.Length.ToString(CultureInfo.InvariantCulture), Scalar(only, "Length"));
            Assert.Equal(prepared.Metadata.ArtHash.ToString(CultureInfo.InvariantCulture), Scalar(only, "ArtHash"));
            Assert.Equal(prepared.Metadata.ArtSize.ToString(CultureInfo.InvariantCulture), Scalar(only, "ArtSize"));

            var readyIndex = Assert.Single(
                sink.Events.Select((e, i) => (e, i)).Where(static pair => IsEvent(pair.e, EngineReadyEventId)).Select(static pair => pair.i));
            var invalidatedIndex = Assert.Single(
                sink.Events.Select((e, i) => (e, i)).Where(static pair => IsEvent(pair.e, StartupPresentInvalidatedEventId)).Select(static pair => pair.i));
            Assert.True(invalidatedIndex < readyIndex);
        }
    }

    [Fact]
    public async Task Failed_invalidation_does_not_emit_the_success_event()
    {
        var prepared = await PrepareAsync("<startup-log-fail@seg.test>");
        using (prepared.Dir)
        {
            File.Delete(SegmentPath(prepared.Dir, prepared.Metadata.Location));
            var sink = new CollectingSink();
            var service = CreateService(prepared.Dir, sink);
            service.TestBeforeRecover = engine =>
            {
                engine.TestHookBeforeExpectedInvalidation = (_, _) =>
                    throw new IOException("startup-invalidation-failed");
            };

            var thrown = await Assert.ThrowsAsync<IOException>(() => service.StartAsync(CancellationToken.None));
            Assert.Equal("startup-invalidation-failed", thrown.Message);
            Assert.False(service.IsReady);
            Assert.DoesNotContain(sink.Events, static e => IsEvent(e, StartupPresentInvalidatedEventId));
            Assert.DoesNotContain(sink.Events, static e => IsEvent(e, EngineReadyEventId));
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task Each_failed_present_row_emits_one_invalidation_event()
    {
        var dir = TempStorageDir.Create();
        using (dir)
        {
            var first = CreateRecord("<startup-log-a@seg.test>");
            var second = CreateRecord("<startup-log-b@seg.test>");
            StoredArticleMetadata firstMeta = default;
            StoredArticleMetadata secondMeta = default;
            await using (var engine = FileArticleStorageEngine.Open(dir.Options))
            {
                Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
                await engine.DrainPendingAsync(CancellationToken.None);
                Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(second, CancellationToken.None)).Outcome);
                await engine.DrainPendingAsync(CancellationToken.None);
                Assert.True(engine.Index.TryGet(first.ArtId, out firstMeta));
                Assert.True(engine.Index.TryGet(second.ArtId, out secondMeta));
                await engine.Segments.CloseActiveAsync(CancellationToken.None);
            }

            foreach (var path in Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*"))
            {
                File.Delete(path);
            }

            var sink = new CollectingSink();
            await using var service = await StartReadyAsync(dir, sink);
            Assert.True(service.IsReady);
            var invalidated = sink.Events.Where(static e => IsEvent(e, StartupPresentInvalidatedEventId)).ToArray();
            Assert.Equal(2, invalidated.Length);
            var ids = invalidated.Select(static e => Scalar(e, "ArtId")).ToArray();
            Assert.Contains(firstMeta.ArtId.ToLowerHexString(), ids);
            Assert.Contains(secondMeta.ArtId.ToLowerHexString(), ids);
            Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
        }
    }

    private const int StartupPresentInvalidatedEventId = 3412;

    private const int EngineReadyEventId = 3002;

    private static bool IsEvent(LogEvent logEvent, int eventId) =>
        logEvent.Properties.TryGetValue("EventId", out var value)
        && value.ToString().Contains(eventId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string Scalar(LogEvent logEvent, string name)
    {
        var property = Assert.Contains(name, logEvent.Properties);
        return property.ToString().Trim('"');
    }

    private static async Task<bool> PresenceAsync(StorageEngineApplicationService service, ArticleId articleId)
    {
        var presence = new DurableIndexArticlePresence(service);
        return await presence.HasArticleAsync(articleId, CancellationToken.None);
    }

    private static async Task<StorageEngineApplicationService> StartReadyAsync(TempStorageDir dir) =>
        await StartReadyAsync(dir, sink: null);

    private static async Task<StorageEngineApplicationService> StartReadyAsync(TempStorageDir dir, CollectingSink? sink)
    {
        var service = CreateService(dir, sink);
        await service.StartAsync(CancellationToken.None);
        return service;
    }

    private static StorageEngineApplicationService CreateService(TempStorageDir dir, CollectingSink? sink = null)
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.CacheDir = dir.Options.SegmentDir;
        options.Storage.ControlDir = dir.Options.ControlDir;
        var runtime = StorageServerRuntimeOptionsFactory.Create(
            options,
            StorageServerTestOptions.CreateValidAcme(options));
        ILogger<StorageEngineApplicationService> logger = sink is null
            ? NullLogger<StorageEngineApplicationService>.Instance
            : new SerilogLoggerFactory(
                new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger(),
                dispose: true).CreateLogger<StorageEngineApplicationService>();
        return new StorageEngineApplicationService(
            runtime,
            Options.Create(options),
            logger);
    }

    private static async Task<Prepared> PrepareAsync(string messageId)
    {
        var dir = TempStorageDir.Create();
        var record = CreateRecord(messageId);
        StoredArticleMetadata metadata = default;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            _ = await AcceptCloseAndLocateAsync(engine, record);
            Assert.True(engine.Index.TryGet(record.ArtId, out metadata));
            Assert.Equal(ArticleStorageState.Present, metadata.State);
        }

        return new Prepared(dir, record, metadata);
    }

    private static long IndexLength(TempStorageDir dir) =>
        new FileInfo(Path.Combine(dir.Options.ControlDir, FileArticleIndex.IndexFileName)).Length;

    private static void OverwriteRecord(TempStorageDir dir, StoredArticleLocation location, byte[] record)
    {
        Assert.Equal(location.Length, record.Length);
        var path = SegmentPath(dir, location);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.Position = location.Offset;
        stream.Write(record, 0, record.Length);
        stream.Flush(true);
    }

    private static void CorruptRecordPayload(string segmentDir, StoredArticleLocation location)
    {
        var path = Directory.EnumerateFiles(segmentDir, "seg-*")
            .Single(candidate => candidate.Contains(location.SegmentId.Value.ToString("D20"), StringComparison.Ordinal));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.Position = location.Offset + SegmentRecordCodec.FixedHeaderLength;
        var one = new byte[1];
        _ = stream.Read(one, 0, 1);
        one[0] ^= 0xFF;
        stream.Position = location.Offset + SegmentRecordCodec.FixedHeaderLength;
        stream.Write(one, 0, 1);
        stream.Flush(true);
    }

    private static string SegmentPath(TempStorageDir dir, StoredArticleLocation location) =>
        Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*")
            .Single(candidate => candidate.Contains(location.SegmentId.Value.ToString("D20"), StringComparison.Ordinal));

    private static async Task<(SegmentId SourceId, ulong Generation, StoredArticleLocation Location)>
        AcceptCloseAndLocateAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
        return (meta.Location.SegmentId, info.Generation, meta.Location);
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
        _ = builder.Append("Subject: startup-present\r\n");
        _ = builder.Append("\r\nline1\r\nline2\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class Prepared(
        TempStorageDir dir,
        ArticleRecord record,
        StoredArticleMetadata metadata)
    {
        public TempStorageDir Dir { get; } = dir;

        public ArticleRecord Record { get; } = record;

        public StoredArticleMetadata Metadata { get; } = metadata;
    }

    private sealed class FullVolumeReader(long total) : IStorageCapacityReader
    {
        public StorageCapacitySnapshot Read() => new(total, total, 0);
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-startup-present-" + Guid.NewGuid().ToString("N"));
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
    }
}
