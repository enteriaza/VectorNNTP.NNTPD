using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Storage.Engine.Policy;
using VectorNNTP.StorageServer.Tests.Fixtures;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Closed and retired segment payloads are not a startup proof. The active append
/// offset still is, and a targeted read still refuses corrupt bytes.
/// </summary>
public sealed class ImmutableSegmentStartupTests
{
    [Fact]
    public async Task A_ClosedValidRecords_StartupDoesNotReadPayload()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<imm-a@seg.test>");
        var location = await SeedClosedAsync(dir, record);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(0, engine.Segments.DiscoveryPayloadBytesRead);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, engine.Segments.DiscoveryPayloadBytesRead);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(location.Offset, read.Metadata.Location.Offset);
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
        Assert.Equal(new FileInfo(ClosedPath(dir, location.SegmentId)).Length, info.SizeBytes);
    }

    [Fact]
    public async Task B_UnindexedBadCrc_Ready_AndValidArticleRemainsReadable()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<imm-b@seg.test>", "kept-body\r\n");
        var location = await SeedClosedAsync(dir, record);
        var other = CreateRecord("<imm-b-other@seg.test>", "other-body\r\n");
        var encoded = SegmentRecordCodec.Encode(other.ArtId, other.ArtHash, other.ArtData.Span);
        encoded[^1] ^= 0xFF;
        AppendToClosed(dir, location.SegmentId, encoded);

        var service = CreateService(dir);
        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(service.IsReady);
            Assert.False(service.Engine.IsUnreferencedExtentAccountingComplete);
            Assert.Equal(0, service.Engine.Segments.DiscoveryPayloadBytesRead);
            Assert.True(service.Engine.TryRead(record.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
            Assert.False(service.Engine.Index.TryGet(other.ArtId, out _));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task C_IndexedBadCrc_TryReadRefusesBytes()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<imm-c@seg.test>");
        var location = await SeedClosedAsync(dir, record);
        CorruptByte(ClosedPath(dir, location.SegmentId), location.Offset + location.Length - 1);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.False(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.IsEmpty);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Invalid, meta.State);
    }

    [Fact]
    public async Task D_TruncatedUnindexedTrailer_ValidIndexedReadWorks()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<imm-d@seg.test>");
        var location = await SeedClosedAsync(dir, record);
        AppendToClosed(dir, location.SegmentId, [0x01, 0x02, 0x03, 0x04]);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(0, engine.Segments.DiscoveryPayloadBytesRead);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task E_RetiredCorruptPayload_NotServed_ReclaimByFilename()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<imm-e@seg.test>");
        StoredArticleLocation location;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            location = meta.Location;
            Assert.True(engine.TryEvict(record.ArtId));
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var closed));
            Assert.True(engine.Segments.Catalogue.TryRetire(location.SegmentId, closed.Generation, DateTimeOffset.UtcNow));
        }

        var retiredPath = Path.Combine(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(location.SegmentId, SegmentFileKind.Retired));
        await File.WriteAllBytesAsync(retiredPath, [0xDE, 0xAD]);

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(0, restarted.Segments.DiscoveryPayloadBytesRead);
        await restarted.RecoverAsync(CancellationToken.None);

        Assert.False(restarted.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.IsEmpty);
        Assert.False(restarted.Segments.TryRead(location, out var physical));
        Assert.Equal(0, physical.Length);
        Assert.True(restarted.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.Equal(SegmentState.Retired, info.State);

        var reclaimed = await restarted.ReclaimRetiredSegmentAsync(location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimed.Outcome);
        Assert.False(File.Exists(retiredPath));
    }

    [Fact]
    public async Task F_ActiveMiddleCorruption_FailsClosed()
    {
        using var dir = TempStorageDir.Create();
        StoredArticleLocation first;
        using (var store = FileSegmentStore.Open(dir.Options))
        {
            var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
            first = await appender.AppendAsync(CreateArtData("<imm-f1@seg.test>"), CancellationToken.None);
            _ = await appender.AppendAsync(CreateArtData("<imm-f2@seg.test>"), CancellationToken.None);
        }

        var active = Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.active").Single();
        var lengthBefore = new FileInfo(active).Length;
        CorruptByte(active, first.Offset + first.Length - 1);

        var ex = Assert.Throws<SegmentStoreCorruptException>(() => FileSegmentStore.Open(dir.Options));
        Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(lengthBefore, new FileInfo(active).Length);
    }

    [Fact]
    public async Task G_ActiveTornFinalRecord_TruncatesOnlyTheTail()
    {
        using var dir = TempStorageDir.Create();
        var article = CreateArtData("<imm-g@seg.test>");
        StoredArticleLocation location;
        using (var store = FileSegmentStore.Open(dir.Options))
        {
            location = await (await store.GetActiveAppenderAsync(CancellationToken.None))
                .AppendAsync(article, CancellationToken.None);
        }

        var active = Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.active").Single();
        var valid = await File.ReadAllBytesAsync(active);
        var torn = new byte[valid.Length + 3];
        valid.CopyTo(torn, 0);
        torn[^3] = 0x11;
        torn[^2] = 0x22;
        torn[^1] = 0x33;
        await File.WriteAllBytesAsync(active, torn);

        using var reopened = FileSegmentStore.Open(dir.Options);
        Assert.Equal(torn.Length, reopened.DiscoveryPayloadBytesRead);
        Assert.Equal(valid.Length, new FileInfo(active).Length);
        Assert.True(reopened.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.Equal(valid.Length, info.SizeBytes);
        Assert.True(reopened.TryRead(location, out var read));
        Assert.True(read.Span.SequenceEqual(article));
    }

    [Fact]
    public async Task H_IndexPastClosedEof_TryReadFailsWithoutBytes()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<imm-h@seg.test>");
        var location = await SeedClosedAsync(dir, record);
        var path = ClosedPath(dir, location.SegmentId);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(0);
        }

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.False(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.IsEmpty);
    }

    [Fact]
    public async Task I_PhysicalWrittenCorruptClosedRecord_IsNotPublished()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<imm-i@seg.test>");
        StoredArticleLocation location;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            engine.SuspendBackgroundPersist = true;
            var accept = await engine.AcceptAsync(record, CancellationToken.None);
            var appender = await engine.Segments.GetActiveAppenderAsync(CancellationToken.None);
            location = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engine.Journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                    CancellationToken.None));
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
        }

        CorruptByte(ClosedPath(dir, location.SegmentId), location.Offset + location.Length - 1);

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        restarted.SuspendBackgroundPersist = true;
        Assert.Equal(0, restarted.Segments.DiscoveryPayloadBytesRead);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => restarted.RecoverAsync(CancellationToken.None));
        Assert.Contains("integrity proof", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(restarted.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.IsEmpty);
        if (restarted.Index.TryGet(record.ArtId, out var meta))
        {
            Assert.NotEqual(ArticleStorageState.Present, meta.State);
        }
    }

    [Fact]
    public async Task J_SkippedOrphanScan_DoesNotInventDeadBytes_OrReclaimability()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<imm-j@seg.test>");
        var location = await SeedClosedAsync(dir, record);
        var extra = CreateRecord("<imm-j-extra@seg.test>", "orphan-body\r\n");
        var encoded = SegmentRecordCodec.Encode(extra.ArtId, extra.ArtHash, extra.ArtData.Span);
        AppendToClosed(dir, location.SegmentId, encoded);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.False(engine.IsUnreferencedExtentAccountingComplete);
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.Equal(location.Length, info.LiveBytes);
        Assert.Equal(0, info.DeadBytes);
        Assert.True(info.SizeBytes > info.LiveBytes + info.DeadBytes);
        Assert.Equal(SegmentState.Closed, info.State);
        Assert.False(SegmentLifecycle.IsReclaimable(in info));

        var policy = new ArticleSegmentPolicy(
            enabled: true,
            ArticleCompactionPolicyOptions.DefaultMinimumDeadBytes,
            ArticleCompactionPolicyOptions.DefaultMinimumDeadRatio);
        Assert.False(policy.EvaluateCompaction(in info).IsEligible);

        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.IsUnreferencedExtentAccountingComplete);
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var accounted));
        Assert.Equal(location.Length, accounted.LiveBytes);
        Assert.Equal(encoded.Length, accounted.DeadBytes);
        Assert.False(SegmentLifecycle.IsReclaimable(in accounted));
    }

    [Fact]
    public async Task K_AdvertisementProbe_StartsOnlyAfterEngineReady()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<imm-k@seg.test>");
        var location = await SeedClosedAsync(dir, record);
        AppendToClosed(dir, location.SegmentId, [0xAA, 0xBB, 0xCC]);

        var service = CreateService(dir);
        var probe = new AdvertisementProbe(service);
        var manager = new ApplicationServiceManager(
            [service, probe],
            StorageServerTestOptions.CreateValid(),
            NullLogger<ApplicationServiceManager>.Instance);

        await manager.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(1, probe.StartCount);
            Assert.True(probe.ObservedEngineReady);
            Assert.True(service.IsReady);
            Assert.False(service.Engine.IsUnreferencedExtentAccountingComplete);
            Assert.True(service.Engine.TryRead(record.ArtId, out _));
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task L_ClosedDiscovery_DoesNotInvokePayloadReader()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<imm-l@seg.test>");
        var location = await SeedClosedAsync(dir, record);
        var closedLength = new FileInfo(ClosedPath(dir, location.SegmentId)).Length;
        Assert.True(closedLength > 0);

        var calls = 0;
        using var store = FileSegmentStore.Open(
            dir.Options,
            path =>
            {
                calls++;
                throw new InvalidOperationException($"Discovery read payload '{path}'.");
            });

        Assert.Equal(0, calls);
        Assert.Equal(0, store.DiscoveryPayloadBytesRead);
        Assert.True(store.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.Equal(closedLength, info.SizeBytes);
        Assert.Equal(SegmentState.Closed, info.State);
    }

    private static async Task<StoredArticleLocation> SeedClosedAsync(TempStorageDir dir, ArticleRecord record)
    {
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        return meta.Location;
    }

    private static void AppendToClosed(TempStorageDir dir, SegmentId segmentId, byte[] bytes)
    {
        using var stream = new FileStream(
            ClosedPath(dir, segmentId),
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void CorruptByte(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Position = offset;
        var value = stream.ReadByte();
        Assert.True(value >= 0);
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0xFF));
        stream.Flush(flushToDisk: true);
    }

    private static string ClosedPath(TempStorageDir dir, SegmentId segmentId) =>
        Path.Combine(dir.Options.SegmentDir, SegmentFileNames.Format(segmentId, SegmentFileKind.Closed));

    private static StorageEngineApplicationService CreateService(TempStorageDir dir)
    {
        var options = StorageServerTestOptions.CreateValid();
        options.CacheDir = dir.Options.SegmentDir;
        options.Storage.ControlDir = dir.Options.ControlDir;
        var runtime = StorageServerRuntimeOptionsFactory.Create(
            options,
            StorageServerTestOptions.CreateValidAcme(options));
        return new StorageEngineApplicationService(
            runtime,
            Options.Create(options),
            NullLogger<StorageEngineApplicationService>.Instance);
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
        _ = builder.Append("Subject: immutable-startup\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private static byte[] CreateArtData(string messageId) => CreateRecord(messageId).ArtData.ToArray();

    private sealed class AdvertisementProbe : IApplicationService
    {
        private readonly StorageEngineApplicationService _engine;

        public AdvertisementProbe(StorageEngineApplicationService engine)
        {
            _engine = engine;
        }

        public string Name => "StorageServerAdvertisementPublisher";

        public Task? Execution => null;

        public int StartCount { get; private set; }

        public bool ObservedEngineReady { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            ObservedEngineReady = _engine.IsReady;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-imm-start-" + Guid.NewGuid().ToString("N"));
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
