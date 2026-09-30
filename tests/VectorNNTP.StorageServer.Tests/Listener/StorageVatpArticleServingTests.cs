using System.Buffers;
using System.Text;
using System.Threading.Channels;
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
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Tests.Fixtures;

namespace VectorNNTP.StorageServer.Tests.Listener;

/// <summary>Phase 5G.2: StorageServer VATP OPEN serves a Present article.</summary>
public sealed partial class StorageVatpArticleServingTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PresentOpen_SendsMetaDataFinAndEnd()
    {
        var built = BuildArticle("<vatp-a@seg.test>", "line1\r\nline2\r\n");
        await using var harness = await Harness.StartAsync(built, maxFramePayload: (uint)built.Record.ArtSize + 32);
        var frames = await harness.OpenAndReadAsync(built.Record.ArtId, streamId: 1);

        AssertTransfer(frames, streamId: 1, built);
        Assert.Single(frames, static frame => frame.Header.Type == VatpFrameType.Data);
        Assert.True(frames.Single(static frame => frame.Header.Type == VatpFrameType.Data).Header.HasFin);
    }

    [Fact]
    public async Task ArticleLargerThanOneFrame_FinOnlyOnFinalData()
    {
        var built = BuildArticle("<vatp-b@seg.test>", new string('x', 200));
        var frame = (uint)Math.Max(1, built.Record.ArtSize / 3);
        await using var harness = await Harness.StartAsync(built, maxFramePayload: frame);
        var frames = await harness.OpenAndReadAsync(built.Record.ArtId, streamId: 1);

        var data = frames.Where(static frame => frame.Header.Type == VatpFrameType.Data).ToArray();
        Assert.True(data.Length > 1);
        Assert.All(data.Take(data.Length - 1), static frame => Assert.False(frame.Header.HasFin));
        Assert.True(data[^1].Header.HasFin);
        Assert.All(data, static frame => Assert.NotEmpty(frame.Payload));
        Assert.Equal(1, frames.Count(static frame => frame.Header.Type == VatpFrameType.End));
        AssertTransfer(frames, streamId: 1, built);
    }

    [Fact]
    public async Task ArticleExactlyOneFrame_HasFinAndNoExtraData()
    {
        var built = BuildArticle("<vatp-c@seg.test>", "exact-one\r\n");
        await using var harness = await Harness.StartAsync(built, maxFramePayload: (uint)built.Record.ArtSize);
        var frames = await harness.OpenAndReadAsync(built.Record.ArtId, streamId: 1);

        var data = Assert.Single(frames, static frame => frame.Header.Type == VatpFrameType.Data);
        Assert.Equal(built.Record.ArtSize, data.Payload.Length);
        Assert.True(data.Header.HasFin);
        Assert.Equal(1, frames.Count(static frame => frame.Header.Type == VatpFrameType.End));
    }

    [Fact]
    public async Task ArticleOnFrameBoundary_DoesNotEmitEmptyData()
    {
        var built = BuildEvenArticle("<vatp-d@seg.test>");
        var frame = (uint)(built.Record.ArtSize / 2);
        await using var harness = await Harness.StartAsync(built, maxFramePayload: frame);
        var frames = await harness.OpenAndReadAsync(built.Record.ArtId, streamId: 1);

        var data = frames.Where(static frame => frame.Header.Type == VatpFrameType.Data).ToArray();
        Assert.Equal(2, data.Length);
        Assert.All(data, item => Assert.Equal(frame, (uint)item.Payload.Length));
        Assert.False(data[0].Header.HasFin);
        Assert.True(data[1].Header.HasFin);
        Assert.Equal(1, frames.Count(static frame => frame.Header.Type == VatpFrameType.End));
    }

    [Fact]
    public async Task ArticleWithinInitialCredit_CompletesWithoutWindow()
    {
        var built = BuildArticle("<vatp-credit-a@seg.test>", "small\r\n");
        var limits = new ArticleTransferLimits
        {
            InitialStreamWindowBytes = built.Record.ArtSize,
            MaxStreamCreditBytes = 1024 * 1024,
        };
        await using var harness = await Harness.StartAsync(built, maxFramePayload: 64 * 1024, limits: limits);
        var frames = await harness.OpenAndReadAsync(built.Record.ArtId, streamId: 1);

        AssertTransfer(frames, streamId: 1, built);
        Assert.DoesNotContain(frames, static frame => frame.Header.Type == VatpFrameType.Window);
    }

    [Fact]
    public async Task ArticleBeyondInitialCredit_ResumesAfterWindow()
    {
        var built = BuildArticle("<vatp-credit-b@seg.test>", new string('y', 80));
        const int credit = 24;
        var limits = new ArticleTransferLimits
        {
            InitialStreamWindowBytes = credit,
            MaxStreamCreditBytes = 1024 * 1024,
        };
        await using var harness = await Harness.StartAsync(built, maxFramePayload: 64 * 1024, limits: limits);
        await harness.SendHelloAndOpenAsync(built.Record.ArtId, streamId: 1);
        var paused = await harness.ReadUntilAsync(frames =>
            DataBytes(frames, 1) == credit && !frames.Any(frame => frame.Header.Type == VatpFrameType.End));

        Assert.Equal(credit, DataBytes(paused, 1));
        Assert.DoesNotContain(paused, static frame => frame.Header.Type == VatpFrameType.End);

        await harness.SendWindowAsync(1, (uint)(built.Record.ArtSize - credit));
        var rest = await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.End && frame.Header.StreamId == 1));
        AssertTransfer([.. paused, .. rest], streamId: 1, built);
    }

    [Fact]
    public async Task MultipleWindows_AccumulateCredit()
    {
        var built = BuildArticle("<vatp-credit-c@seg.test>", new string('z', 90));
        const int initial = 20;
        var limits = new ArticleTransferLimits
        {
            InitialStreamWindowBytes = initial,
            MaxStreamCreditBytes = 1024 * 1024,
        };
        await using var harness = await Harness.StartAsync(built, maxFramePayload: 64, limits: limits);
        await harness.SendHelloAndOpenAsync(built.Record.ArtId, streamId: 1);
        var first = await harness.ReadUntilAsync(frames => DataBytes(frames, 1) == initial);
        Assert.Equal(initial, DataBytes(first, 1));

        await harness.SendWindowAsync(1, 15);
        var second = await harness.ReadUntilAsync(frames => DataBytes(frames, 1) == 15);
        Assert.Equal(initial + 15, DataBytes([.. first, .. second], 1));

        await harness.SendWindowAsync(1, (uint)(built.Record.ArtSize - (initial + 15)));
        var rest = await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.End && frame.Header.StreamId == 1));
        var frames = first.Concat(second).Concat(rest).ToArray();
        AssertTransfer(frames, streamId: 1, built);
        Assert.All(
            frames.Where(static frame => frame.Header.Type == VatpFrameType.Data),
            static frame => Assert.InRange(frame.Payload.Length, 1, 64));
    }

    [Fact]
    public async Task Data_NeverExceedsGrantedCredit()
    {
        var built = BuildArticle("<vatp-credit-d@seg.test>", new string('w', 70));
        const int credit = 32;
        var limits = new ArticleTransferLimits
        {
            InitialStreamWindowBytes = credit,
            MaxStreamCreditBytes = 1024 * 1024,
        };
        await using var harness = await Harness.StartAsync(built, maxFramePayload: 100, limits: limits);
        await harness.SendHelloAndOpenAsync(built.Record.ArtId, streamId: 1);
        var paused = await harness.ReadUntilAsync(frames => DataBytes(frames, 1) == credit);
        Assert.Equal(credit, DataBytes(paused, 1));
        Assert.All(paused.Where(static frame => frame.Header.Type == VatpFrameType.Data), frame =>
            Assert.True(frame.Payload.Length <= credit));

        await harness.SendWindowAsync(1, 10);
        var next = await harness.ReadUntilAsync(frames => DataBytes(frames, 1) == 10);
        Assert.Equal(credit + 10, DataBytes([.. paused, .. next], 1));
    }

    [Fact]
    public async Task CancelWhileWaitingForWindow_DoesNotSendEnd()
    {
        var large = BuildArticle("<vatp-cancel@seg.test>", new string('c', 400));
        var other = BuildArticle("<vatp-cancel-other@seg.test>", "ok\r\n");
        var limits = new ArticleTransferLimits
        {
            InitialStreamWindowBytes = other.Record.ArtSize,
            MaxStreamCreditBytes = 1024 * 1024,
        };
        Assert.True(large.Record.ArtSize > other.Record.ArtSize);
        await using var harness = await Harness.StartAsync([large, other], maxFramePayload: 64 * 1024, limits: limits);
        await harness.SendHelloAndOpenAsync(large.Record.ArtId, streamId: 1);
        _ = await harness.ReadUntilAsync(frames => DataBytes(frames, 1) == other.Record.ArtSize);
        await harness.SendCancelAsync(1);
        await harness.SendOpenAsync(other.Record.ArtId, streamId: 2);
        var frames = await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.End && frame.Header.StreamId == 2));

        Assert.DoesNotContain(frames, static frame => frame.Header.Type == VatpFrameType.End && frame.Header.StreamId == 1);
        AssertTransfer(frames, streamId: 2, other);
    }

    [Fact]
    public async Task TwoOpensOnOneConnection_BothComplete()
    {
        var first = BuildArticle("<vatp-two-a@seg.test>", "aaa\r\n");
        var second = BuildArticle("<vatp-two-b@seg.test>", "bbb\r\n");
        await using var harness = await Harness.StartAsync([first, second], maxFramePayload: 64 * 1024);
        await harness.SendOpenAsync(first.Record.ArtId, streamId: 1);
        await harness.SendOpenAsync(second.Record.ArtId, streamId: 2);
        var frames = await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.End && frame.Header.StreamId == 1)
            && frames.Any(frame => frame.Header.Type == VatpFrameType.End && frame.Header.StreamId == 2));

        AssertTransfer(frames, 1, first);
        AssertTransfer(frames, 2, second);
    }

    [Fact]
    public async Task SameArticle_CanBeOpenedTwice()
    {
        var built = BuildArticle("<vatp-twice@seg.test>", "again\r\n");
        await using var harness = await Harness.StartAsync(built, maxFramePayload: 64 * 1024);
        var first = await harness.OpenAndReadAsync(built.Record.ArtId, streamId: 1);
        AssertTransfer(first, 1, built);
        await harness.SendOpenAsync(built.Record.ArtId, streamId: 2);
        var second = await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.End && frame.Header.StreamId == 2));
        AssertTransfer(second, 2, built);
    }

    [Fact]
    public async Task MalformedOpen_FailsDecodeAndConnectionStaysUsable()
    {
        var built = BuildArticle("<vatp-malformed@seg.test>", "ok\r\n");
        await using var harness = await Harness.StartAsync(built, maxFramePayload: 64 * 1024);
        var bad = new byte[VatpProtocol.HeaderLengthBytes + VatpProtocol.OpenPayloadLength];
        VatpFrameHeader.Create(VatpFrameType.Open, 1, VatpProtocol.OpenPayloadLength).WriteTo(bad);
        await harness.Transport.ClientWriteAsync(bad);
        var failed = await harness.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.InvalidOpen, DecodeFail(failed.Last(static frame => frame.Header.Type == VatpFrameType.Fail)));

        await harness.SendOpenAsync(built.Record.ArtId, streamId: 2);
        var frames = await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.End && frame.Header.StreamId == 2));
        AssertTransfer(frames, 2, built);
    }

    [Fact]
    public async Task OpenBeforeHello_IsInvalidStateTransition()
    {
        var built = BuildArticle("<vatp-order@seg.test>", "ok\r\n");
        var transport = new LoopbackVatpTransport();
        var boundary = new FixedOpenBoundary(built);
        await using var session = new StorageVatpSession(
            transport,
            boundary,
            ListenerOptions(),
            NullLogger.Instance);
        using var cts = new CancellationTokenSource(Safety);
        var run = session.RunAsync(cts.Token);
        await transport.ClientWriteAsync(OpenFrame(built.Record.ArtId, streamId: 1));
        var frames = await transport.ReadUntilAsync(
            static frames => frames.Any(frame => frame.Header.Type == VatpFrameType.Fail),
            cts.Token);
        Assert.Equal(VatpErrorCode.InvalidStateTransition, DecodeFail(Assert.Single(frames)));
        transport.ClientClose();
        await run.WaitAsync(Safety);
    }

    private static int DataBytes(IReadOnlyList<DecodedFrame> frames, uint streamId) =>
        frames.Where(frame => frame.Header.Type == VatpFrameType.Data && frame.Header.StreamId == streamId)
            .Sum(static frame => frame.Payload.Length);

    private static VatpErrorCode DecodeFail(DecodedFrame frame)
    {
        Assert.True(VatpControlPayload.TryDecodeFail(frame.Payload, out var error, out _));
        return error;
    }

    private static void AssertTransfer(IReadOnlyList<DecodedFrame> frames, uint streamId, BuiltArticle built)
    {
        var stream = frames.Where(frame => frame.Header.StreamId == streamId).ToArray();
        var meta = Assert.Single(stream, static frame => frame.Header.Type == VatpFrameType.Meta);
        Assert.Equal(VatpProtocol.MetaPayloadLength, meta.Payload.Length);
        Assert.True(VatpMetaCodec.TryDecode(meta.Payload, out var decoded, out var metaError), metaError.ToString());
        Assert.Equal(built.Record.ArtHash, decoded.ArtHash);
        Assert.Equal(built.Record.ArtSize, decoded.ArtSize);
        Assert.Equal(built.Record.ArtLines, decoded.ArtLines);

        var data = stream.Where(static frame => frame.Header.Type == VatpFrameType.Data).ToArray();
        Assert.NotEmpty(data);
        Assert.All(data.Take(data.Length - 1), static frame => Assert.False(frame.Header.HasFin));
        Assert.True(data[^1].Header.HasFin);
        var body = data.SelectMany(static frame => frame.Payload).ToArray();
        Assert.Equal(built.Record.ArtData.ToArray(), body);
        Assert.Equal(1, stream.Count(static frame => frame.Header.Type == VatpFrameType.End));
        Assert.Empty(stream.Last(static frame => frame.Header.Type == VatpFrameType.End).Payload);

        var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(body, in decoded, built.Record.ArtId);
        Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
        Assert.Equal(built.Record.ArtId, created.Record.ArtId);
        Assert.Equal(built.Record.ArtHash, created.Record.ArtHash);
        Assert.Equal(built.Record.ArtSize, created.Record.ArtSize);
    }

    private static BuiltArticle BuildEvenArticle(string messageId)
    {
        var built = BuildArticle(messageId, new string('b', 64));
        if (built.Record.ArtSize % 2 == 0)
        {
            return built;
        }

        return BuildArticle(messageId, new string('b', 65));
    }

    private static BuiltArticle BuildArticle(string messageId, string body)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: vatp\r\n");
        _ = builder.Append('\r').Append('\n').Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
        return new BuiltArticle(created.Record, created.SelectedDateHeaderName);
    }

    private static byte[] OpenFrame(ArticleId articleId, uint streamId)
    {
        var id = new byte[ArticleId.Length];
        articleId.CopyTo(id);
        return VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeOpen(streamId, Guid.NewGuid(), id));
    }

    private static StorageServerListenerRuntimeOptions ListenerOptions() =>
        new(262144, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 8);

    private readonly record struct BuiltArticle(ArticleRecord Record, NntpArticleHeaderName SelectedDateHeaderName);

    private readonly record struct DecodedFrame(VatpFrameHeader Header, byte[] Payload);

    private sealed class FixedOpenBoundary : IStorageArticleOpenBoundary
    {
        private readonly Dictionary<ArticleId, BuiltArticle> _articles;

        public FixedOpenBoundary(params BuiltArticle[] articles)
        {
            _articles = articles.ToDictionary(static article => article.Record.ArtId);
        }

        public StorageArticleOpenResult TryOpen(Guid requestId, in ArticleId articleId)
        {
            _ = requestId;
            return _articles.TryGetValue(articleId, out var article)
                ? StorageArticleOpenResult.Opened(article.Record, article.SelectedDateHeaderName)
                : StorageArticleOpenResult.Rejected("missing");
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _sessionCts = new();
        private readonly Task _run;
        private readonly StorageVatpSession _session;

        private Harness(LoopbackVatpTransport transport, StorageVatpSession session, uint maxFramePayload)
        {
            Transport = transport;
            _session = session;
            MaxFramePayload = maxFramePayload;
            _run = session.RunAsync(_sessionCts.Token);
        }

        public LoopbackVatpTransport Transport { get; }

        public uint MaxFramePayload { get; }

        public static async Task<Harness> StartAsync(
            BuiltArticle article,
            uint maxFramePayload,
            ArticleTransferLimits? limits = null) =>
            await StartAsync([article], maxFramePayload, limits);

        public static async Task<Harness> StartAsync(
            IReadOnlyList<BuiltArticle> articles,
            uint maxFramePayload,
            ArticleTransferLimits? limits = null)
        {
            var transport = new LoopbackVatpTransport();
            var session = new StorageVatpSession(
                transport,
                new FixedOpenBoundary([.. articles]),
                ListenerOptions(),
                NullLogger.Instance,
                limits);
            var harness = new Harness(transport, session, maxFramePayload);
            await harness.SendHelloAsync();
            return harness;
        }

        public async Task<IReadOnlyList<DecodedFrame>> OpenAndReadAsync(ArticleId articleId, uint streamId)
        {
            await SendOpenAsync(articleId, streamId);
            return await ReadUntilAsync(frames =>
                frames.Any(frame => frame.Header.Type == VatpFrameType.End && frame.Header.StreamId == streamId));
        }

        public async Task SendHelloAsync() =>
            await Transport.ClientWriteAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeHello(MaxFramePayload)));

        public async Task SendHelloAndOpenAsync(ArticleId articleId, uint streamId)
        {
            await SendOpenAsync(articleId, streamId);
        }

        public async Task SendOpenAsync(ArticleId articleId, uint streamId) =>
            await Transport.ClientWriteAsync(OpenFrame(articleId, streamId));

        public async Task SendWindowAsync(uint streamId, uint credit) =>
            await Transport.ClientWriteAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeWindow(streamId, credit)));

        public async Task SendCancelAsync(uint streamId) =>
            await Transport.ClientWriteAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeCancel(streamId)));

        public async Task<IReadOnlyList<DecodedFrame>> ReadUntilAsync(Func<IReadOnlyList<DecodedFrame>, bool> done)
        {
            using var timeout = new CancellationTokenSource(Safety);
            return await Transport.ReadUntilAsync(done, timeout.Token);
        }

        public async ValueTask DisposeAsync()
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

            await _session.DisposeAsync();
            _sessionCts.Dispose();
        }
    }

    private sealed class LoopbackVatpTransport : IStorageVatpTransport
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>();
        private readonly List<byte> _pendingOut = [];
        private readonly TaskCompletionSource _releaseData = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockedData;
        private byte[]? _current;
        private int _offset;

        public bool BlockFirstDataHeader { get; set; }

        public TaskCompletionSource FirstDataHeader { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseData() => _releaseData.TrySetResult();

        public async ValueTask ClientWriteAsync(byte[] frame) =>
            await _inbound.Writer.WriteAsync(frame);

        public void ClientClose() => _inbound.Writer.TryComplete();

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            while (_current is null || _offset >= _current.Length)
            {
                if (!await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                if (!_inbound.Reader.TryRead(out _current))
                {
                    continue;
                }

                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            return count;
        }

        public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            var copy = buffer.ToArray();
            if (BlockFirstDataHeader
                && copy.Length == VatpProtocol.HeaderLengthBytes
                && copy[1] == (byte)VatpFrameType.Data
                && Interlocked.Exchange(ref _blockedData, 1) == 0)
            {
                FirstDataHeader.TrySetResult();
                await _releaseData.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await _outbound.Writer.WriteAsync(copy, cancellationToken).ConfigureAwait(false);
            return copy.Length;
        }

        public async Task<IReadOnlyList<DecodedFrame>> ReadUntilAsync(
            Func<IReadOnlyList<DecodedFrame>, bool> done,
            CancellationToken cancellationToken)
        {
            var frames = new List<DecodedFrame>();
            while (!done(frames))
            {
                var chunk = await _outbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                _pendingOut.AddRange(chunk);
                Drain(frames);
            }

            return frames;
        }

        public ValueTask DisposeAsync()
        {
            _inbound.Writer.TryComplete();
            _outbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private static byte[] CopyPayload(ReadOnlySequence<byte> payload)
        {
            if (payload.IsSingleSegment)
            {
                return payload.FirstSpan.ToArray();
            }

            var bytes = new byte[payload.Length];
            var offset = 0;
            foreach (var segment in payload)
            {
                segment.Span.CopyTo(bytes.AsSpan(offset));
                offset += segment.Length;
            }

            return bytes;
        }

        private void Drain(List<DecodedFrame> frames)
        {
            while (_pendingOut.Count > 0)
            {
                var bytes = _pendingOut.ToArray();
                var parsed = VatpFrameParser.ParseOneFrame(bytes, VatpProtocol.DefaultMaxFramePayload);
                if (parsed.Status == VatpFrameParseStatus.Incomplete)
                {
                    return;
                }

                if (parsed.Status != VatpFrameParseStatus.Success || parsed.Frame is not { } frame)
                {
                    throw new InvalidOperationException($"Unexpected server frame status {parsed.Status} {parsed.Error}.");
                }

                var payload = CopyPayload(frame.Payload);
                frames.Add(new DecodedFrame(frame.Header, payload));
                _pendingOut.RemoveRange(0, checked((int)parsed.ConsumedBytes));
            }
        }
    }
}
