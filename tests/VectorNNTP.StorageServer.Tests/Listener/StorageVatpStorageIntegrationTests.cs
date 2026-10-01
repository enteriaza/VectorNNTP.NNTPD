using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Listener;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Tests.Fixtures;

namespace VectorNNTP.StorageServer.Tests.Listener;

public sealed partial class StorageVatpArticleServingTests
{
    [Fact]
    public async Task EngineNotReady_OpenRejected()
    {
        await using var hosted = CreateHosted();
        var boundary = new StorageArticleOpenBoundary(hosted.Service);
        var id = ArticleId.FromMessageId("<not-ready@seg.test>"u8);
        var rejected = boundary.TryOpen(Guid.NewGuid(), id);
        Assert.False(rejected.Accepted);

        await using var session = await EngineSession.StartAsync(boundary);
        await session.SendOpenAsync(id, 1);
        var frames = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.OpenRejected, FailCode(frames));
    }

    [Fact]
    public async Task MissingEvictedAndInvalid_OpenRejected_ConnectionStaysUsable()
    {
        await using var hosted = await CreateHosted().StartAsync();
        var present = BuildArticle("<wire-present@seg.test>", "kept\r\n");
        var doomed = BuildArticle("<wire-doomed@seg.test>", "gone\r\n");
        await AcceptAsync(hosted, present.Record);
        await AcceptAsync(hosted, doomed.Record);
        var boundary = new StorageArticleOpenBoundary(hosted.Service);
        await using var session = await EngineSession.StartAsync(boundary, maxFramePayload: 64 * 1024);

        await session.SendOpenAsync(ArticleId.FromMessageId("<missing@seg.test>"u8), 1);
        var missing = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.StreamId == 1 && frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.OpenRejected, FailCode(missing, 1));

        Assert.True(hosted.Service.Engine.TryEvict(doomed.Record.ArtId));
        await session.SendOpenAsync(doomed.Record.ArtId, 2);
        var evicted = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.StreamId == 2 && frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.OpenRejected, FailCode(evicted, 2));

        Assert.True(hosted.Service.Engine.TryInvalidate(present.Record.ArtId));
        await session.SendOpenAsync(present.Record.ArtId, 3);
        var invalid = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.StreamId == 3 && frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.OpenRejected, FailCode(invalid, 3));
        Assert.DoesNotContain(invalid, static frame => frame.Header.Type == VatpFrameType.Meta);
    }

    [Fact]
    public async Task CacheDisabled_ReturnsDurableBytes_WithoutCapacityChange()
    {
        await using var hosted = await CreateHosted(cacheMaxBytes: 0).StartAsync();
        var built = BuildArticle("<cache-off@seg.test>", "durable\r\n");
        await AcceptAsync(hosted, built.Record);
        Assert.True(hosted.Service.Engine.Index.TryGet(built.Record.ArtId, out var meta));
        Assert.True(hosted.Service.Engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var before));
        var reads = 0;
        hosted.Service.Engine.TestHookAfterIndexSnapshotBeforeSegmentRead = (_, _) => reads++;

        var boundary = new StorageArticleOpenBoundary(hosted.Service);
        await using var session = await EngineSession.StartAsync(boundary, maxFramePayload: (uint)built.Record.ArtSize + 8);
        await session.SendOpenAsync(built.Record.ArtId, 1);
        var frames = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.Type == VatpFrameType.End));
        AssertTransfer(frames, 1, built);
        Assert.Equal(1, reads);
        Assert.Equal(0, hosted.Service.Engine.ArticleCache.Count);
        Assert.True(hosted.Service.Engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var after));
        Assert.Equal(before.LiveBytes, after.LiveBytes);
        Assert.Equal(before.DeadBytes, after.DeadBytes);
        Assert.Equal(before.SizeBytes, after.SizeBytes);
    }

    [Fact]
    public async Task CacheEnabled_SecondOpenDoesNotReadSegment()
    {
        await using var hosted = await CreateHosted(cacheMaxBytes: 8 * 1024 * 1024).StartAsync();
        var built = BuildArticle("<cache-on@seg.test>", "cached\r\n");
        await AcceptAsync(hosted, built.Record);
        var boundary = new StorageArticleOpenBoundary(hosted.Service);
        await using var session = await EngineSession.StartAsync(boundary, maxFramePayload: 64 * 1024);
        await session.SendOpenAsync(built.Record.ArtId, 1);
        _ = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.StreamId == 1 && frame.Header.Type == VatpFrameType.End));
        Assert.Equal(1, hosted.Service.Engine.ArticleCache.Count);

        var reads = 0;
        hosted.Service.Engine.TestHookAfterIndexSnapshotBeforeSegmentRead = (_, _) => reads++;
        await session.SendOpenAsync(built.Record.ArtId, 2);
        var frames = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.StreamId == 2 && frame.Header.Type == VatpFrameType.End));
        AssertTransfer(frames, 2, built);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task CanonicalMismatch_OpenRejected_LeavesPresent()
    {
        await using var hosted = await CreateHosted().StartAsync();
        var built = BuildArticle("<mismatch@seg.test>", "body\r\n");
        await AcceptAsync(hosted, built.Record);
        var boundary = new StorageArticleOpenBoundary(
            hosted.Service,
            new NntpArticleParser("not-a-stored-hop.example"));
        var rejected = boundary.TryOpen(Guid.NewGuid(), built.Record.ArtId);
        Assert.False(rejected.Accepted);
        Assert.True(hosted.Service.Engine.Index.TryGet(built.Record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);

        await using var session = await EngineSession.StartAsync(boundary, maxFramePayload: 64 * 1024);
        await session.SendOpenAsync(built.Record.ArtId, 1);
        var frames = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.OpenRejected, FailCode(frames, 1));
        Assert.DoesNotContain(frames, static frame => frame.Header.Type == VatpFrameType.Meta);
    }

    [Fact]
    public async Task PhysicalCorruption_OpenRejected_AndInvalidates()
    {
        await using var hosted = await CreateHosted().StartAsync();
        var built = BuildArticle("<corrupt@seg.test>", "body\r\n");
        await AcceptAsync(hosted, built.Record);
        await hosted.Service.Engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(hosted.Service.Engine.Index.TryGet(built.Record.ArtId, out var meta));
        CorruptRecordPayload(hosted.Runtime.Storage.SegmentDir, meta.Location);

        var boundary = new StorageArticleOpenBoundary(hosted.Service);
        await using var session = await EngineSession.StartAsync(boundary, maxFramePayload: 64 * 1024);
        await session.SendOpenAsync(built.Record.ArtId, 1);
        var frames = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.OpenRejected, FailCode(frames, 1));
        Assert.True(hosted.Service.Engine.Index.TryGet(built.Record.ArtId, out meta));
        Assert.Equal(ArticleStorageState.Invalid, meta.State);
    }

    [Fact]
    public async Task RelocationThenOpen_ReturnsOriginalBytes()
    {
        await using var hosted = await CreateHosted().StartAsync();
        var built = BuildArticle("<reloc@seg.test>", "moved\r\n");
        await AcceptAsync(hosted, built.Record);
        Assert.True(hosted.Service.Engine.Index.TryGet(built.Record.ArtId, out var before));
        await hosted.Service.Engine.Segments.CloseActiveAsync(CancellationToken.None);
        var compact = await hosted.Service.Engine.CompactClosedSegmentAsync(before.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);

        var boundary = new StorageArticleOpenBoundary(hosted.Service);
        await using var session = await EngineSession.StartAsync(boundary, maxFramePayload: (uint)built.Record.ArtSize + 16);
        await session.SendOpenAsync(built.Record.ArtId, 1);
        var frames = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.Type == VatpFrameType.End));
        AssertTransfer(frames, 1, built);
        Assert.True(hosted.Service.Engine.Index.TryGet(built.Record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Present, after.State);
        Assert.NotEqual(before.Location, after.Location);
    }

    [Fact]
    public async Task StaleLocationDuringRead_StillReturnsArticle()
    {
        await using var hosted = await CreateHosted().StartAsync();
        var built = BuildArticle("<stale@seg.test>", "follow\r\n");
        await AcceptAsync(hosted, built.Record);
        var engine = hosted.Service.Engine;
        Assert.True(engine.Index.TryGet(built.Record.ArtId, out var source));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        engine.TestHookAfterIndexSnapshotBeforeSegmentRead = (_, _) =>
        {
            var compact = engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
            var retired = engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retired.Outcome);
        };

        var boundary = new StorageArticleOpenBoundary(hosted.Service);
        await using var session = await EngineSession.StartAsync(boundary, maxFramePayload: (uint)built.Record.ArtSize + 16);
        await session.SendOpenAsync(built.Record.ArtId, 1);
        var frames = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.Type == VatpFrameType.End));
        AssertTransfer(frames, 1, built);
        Assert.True(engine.Index.TryGet(built.Record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.NotEqual(source.Location, meta.Location);
    }

    [Fact]
    public async Task EvictDuringIndexedRead_RejectsWithoutServing()
    {
        await using var hosted = await CreateHosted().StartAsync();
        var built = BuildArticle("<evict-race@seg.test>", "race\r\n");
        await AcceptAsync(hosted, built.Record);
        hosted.Service.Engine.TestHookAfterIndexSnapshotBeforeSegmentRead = (id, _) =>
        {
            if (!hosted.Service.Engine.TryEvict(id))
            {
                throw new InvalidOperationException("Evict during indexed read did not apply.");
            }
        };

        var boundary = new StorageArticleOpenBoundary(hosted.Service);
        await using var session = await EngineSession.StartAsync(boundary, maxFramePayload: (uint)built.Record.ArtSize + 16);
        await session.SendOpenAsync(built.Record.ArtId, 1);
        var frames = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.StreamId == 1 && frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.OpenRejected, FailCode(frames, 1));
        Assert.DoesNotContain(frames, frame => frame.Header.Type == VatpFrameType.Data && frame.Header.StreamId == 1);
        Assert.True(hosted.Service.Engine.Index.TryGet(built.Record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Evicted, meta.State);

        await session.SendOpenAsync(built.Record.ArtId, 2);
        var rejected = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.StreamId == 2 && frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.OpenRejected, FailCode(rejected, 2));
    }

    [Fact]
    public async Task EvictAfterRead_InFlightTransferStillCompletes()
    {
        await using var hosted = await CreateHosted().StartAsync();
        var built = BuildArticle("<inflight@seg.test>", "inflight\r\n");
        await AcceptAsync(hosted, built.Record);
        var boundary = new StorageArticleOpenBoundary(hosted.Service);
        await using var session = await EngineSession.StartAsync(boundary, maxFramePayload: (uint)built.Record.ArtSize + 8, blockFirstData: true);
        await session.SendOpenAsync(built.Record.ArtId, 1);
        await session.WaitUntilFirstDataAsync();
        Assert.True(hosted.Service.Engine.TryEvict(built.Record.ArtId));
        session.ReleaseData();
        var frames = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.Type == VatpFrameType.End));
        AssertTransfer(frames, 1, built);

        await session.SendOpenAsync(built.Record.ArtId, 2);
        var rejected = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.StreamId == 2 && frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.OpenRejected, FailCode(rejected, 2));
    }

    [Fact]
    public async Task CancelDuringTransfer_DoesNotChangeDurableState()
    {
        await using var hosted = await CreateHosted().StartAsync();
        var built = BuildArticle("<cancel-store@seg.test>", new string('q', 80));
        await AcceptAsync(hosted, built.Record);
        var limits = new ArticleTransferLimits { InitialStreamWindowBytes = 16, MaxStreamCreditBytes = 1024 * 1024 };
        var boundary = new StorageArticleOpenBoundary(hosted.Service);
        await using var session = await EngineSession.StartAsync(boundary, maxFramePayload: 64 * 1024, limits: limits);
        await session.SendOpenAsync(built.Record.ArtId, 1);
        _ = await session.ReadUntilAsync(frames => DataBytes(frames, 1) == 16);
        await session.SendCancelAsync(1);
        await session.SendOpenAsync(ArticleId.FromMessageId("<after-cancel@seg.test>"u8), 2);
        var frames = await session.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.StreamId == 2 && frame.Header.Type == VatpFrameType.Fail));
        Assert.DoesNotContain(frames, static frame => frame.Header.Type == VatpFrameType.End && frame.Header.StreamId == 1);
        Assert.True(hosted.Service.Engine.Index.TryGet(built.Record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(built.Record.ArtHash, meta.ArtHash);
    }

    [Fact]
    public async Task ListenerCancellationDuringTransfer_LeavesArticlePresent()
    {
        await using var hosted = await CreateHosted().StartAsync();
        var built = BuildArticle("<shutdown@seg.test>", new string('s', 80));
        await AcceptAsync(hosted, built.Record);
        var limits = new ArticleTransferLimits { InitialStreamWindowBytes = 16, MaxStreamCreditBytes = 1024 * 1024 };
        var boundary = new StorageArticleOpenBoundary(hosted.Service);
        var session = await EngineSession.StartAsync(boundary, maxFramePayload: 64 * 1024, limits: limits);
        await session.SendOpenAsync(built.Record.ArtId, 1);
        _ = await session.ReadUntilAsync(frames => DataBytes(frames, 1) == 16);
        await session.CancelSessionAsync();
        Assert.True(hosted.Service.Engine.Index.TryGet(built.Record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(built.Record.ArtHash, meta.ArtHash);
    }

    [Fact]
    public async Task DisposedEngine_DoesNotReportOpenRejected()
    {
        await using var hosted = await CreateHosted().StartAsync();
        var built = BuildArticle("<disposed@seg.test>", "body\r\n");
        await AcceptAsync(hosted, built.Record);
        await hosted.Service.Engine.DisposeAsync();
        var boundary = new StorageArticleOpenBoundary(hosted.Service);
        var thrown = Assert.Throws<ObjectDisposedException>(() => boundary.TryOpen(Guid.NewGuid(), built.Record.ArtId));
        Assert.IsType<ObjectDisposedException>(thrown);
    }

    private static VatpErrorCode FailCode(IReadOnlyList<DecodedFrame> frames, uint? streamId = null)
    {
        var fail = frames.Last(frame =>
            frame.Header.Type == VatpFrameType.Fail && (streamId is null || frame.Header.StreamId == streamId));
        return DecodeFail(fail);
    }

    private static async Task AcceptAsync(HostedEngine hosted, ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await hosted.Service.Engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await hosted.Service.Engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(hosted.Service.Engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
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

    private static HostedEngine CreateHosted(long cacheMaxBytes = 0)
    {
        var root = Path.Combine(Path.GetTempPath(), "vectornntp-p5g2-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cache");
        var control = Path.Combine(root, "control");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(control);
        var options = StorageServerTestOptions.CreateValid();
        options.CacheDir = cache;
        options.Storage.ControlDir = control;
        options.Storage.ArticleCache.MaxBytes = cacheMaxBytes;
        var runtime = StorageServerRuntimeOptionsFactory.Create(options, StorageServerTestOptions.CreateValidAcme(options));
        var service = new StorageEngineApplicationService(
            runtime,
            Options.Create(options),
            NullLogger<StorageEngineApplicationService>.Instance);
        return new HostedEngine(root, runtime, service);
    }

    private sealed class HostedEngine : IAsyncDisposable
    {
        public HostedEngine(string root, StorageServerRuntimeOptions runtime, StorageEngineApplicationService service)
        {
            Root = root;
            Runtime = runtime;
            Service = service;
        }

        public string Root { get; }

        public StorageServerRuntimeOptions Runtime { get; }

        public StorageEngineApplicationService Service { get; }

        public async Task<HostedEngine> StartAsync()
        {
            await Service.StartAsync(CancellationToken.None);
            return this;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
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

    private sealed class EngineSession : IAsyncDisposable
    {
        private readonly CancellationTokenSource _sessionCts = new();
        private readonly Task _run;
        private readonly StorageVatpSession _session;

        private EngineSession(LoopbackVatpTransport transport, StorageVatpSession session, Task firstData)
        {
            Transport = transport;
            _session = session;
            FirstData = firstData;
            _run = session.RunAsync(_sessionCts.Token);
        }

        private LoopbackVatpTransport Transport { get; }

        private Task FirstData { get; }

        public static async Task<EngineSession> StartAsync(
            IStorageArticleOpenBoundary boundary,
            uint maxFramePayload = 64 * 1024,
            ArticleTransferLimits? limits = null,
            bool blockFirstData = false)
        {
            var transport = new LoopbackVatpTransport { BlockFirstDataHeader = blockFirstData };
            var session = new StorageVatpSession(transport, boundary, ListenerOptions(), NullLogger.Instance, limits);
            var engineSession = new EngineSession(transport, session, transport.FirstDataHeader.Task);
            await engineSession.Transport.ClientWriteAsync(
                VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeHello(maxFramePayload)));
            return engineSession;
        }

        public async Task SendOpenAsync(ArticleId articleId, uint streamId) =>
            await Transport.ClientWriteAsync(OpenFrame(articleId, streamId));

        public async Task SendCancelAsync(uint streamId) =>
            await Transport.ClientWriteAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeCancel(streamId)));

        public async Task<IReadOnlyList<DecodedFrame>> ReadUntilAsync(Func<IReadOnlyList<DecodedFrame>, bool> done)
        {
            using var timeout = new CancellationTokenSource(Safety);
            return await Transport.ReadUntilAsync(done, timeout.Token);
        }

        public async Task WaitUntilFirstDataAsync() => await FirstData.WaitAsync(Safety);

        public void ReleaseData() => Transport.ReleaseData();

        public async Task CancelSessionAsync()
        {
            Transport.ClientClose();
            _sessionCts.Cancel();
            try
            {
                await _run.WaitAsync(Safety);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            Transport.ReleaseData();
            await CancelSessionAsync();
            await _session.DisposeAsync();
            _sessionCts.Dispose();
        }
    }
}
