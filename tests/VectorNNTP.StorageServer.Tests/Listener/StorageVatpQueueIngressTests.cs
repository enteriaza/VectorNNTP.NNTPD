using System.Buffers;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Ingress;
using VectorNNTP.StorageServer.Listener;

namespace VectorNNTP.StorageServer.Tests.Listener;

public sealed class StorageVatpQueueIngressTests
{
    private const string CacheFqdn = "cache01.usenet.ninja";
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Store_enqueues_the_original_article_without_this_servers_path_hop()
    {
        var inbound = Build("<store-path@seg.test>", "body-line\r\n");
        var queue = new AcceptedArticleQueue();
        await using var harness = await Harness.StartAsync(queue);
        var frames = await harness.StoreAndReadAsync(inbound);
        Assert.Equal(VatpStoreOutcome.Accepted, Outcome(frames));

        var delivery = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(delivery.HasArticle);
        Assert.Equal(inbound.Record.ArtId, delivery.Article.ArtId);
        Assert.Equal(inbound.Record.ArtHash, delivery.Article.ArtHash);
        Assert.Equal(inbound.Record.ArtSize, delivery.Article.ArtSize);
        Assert.True(delivery.Article.ArtData.Span.SequenceEqual(inbound.Record.ArtData.Span));
        var queued = Encoding.ASCII.GetString(delivery.Article.ArtData.Span);
        Assert.Equal(0, CountHop(queued, CacheFqdn));
        Assert.Contains("Path: ", queued, StringComparison.Ordinal);
        Assert.Contains("body-line\r\n", queued, StringComparison.Ordinal);
        Assert.Contains("news.usenet.ninja", queued, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_of_bytes_that_already_contain_this_hop_prepends_it_again()
    {
        var inbound = Build("<store-again-hop@seg.test>", "kept-body\r\n", CacheFqdn);
        var inboundText = Encoding.ASCII.GetString(inbound.Record.ArtData.Span);
        Assert.Equal(1, CountHop(inboundText, CacheFqdn));

        var queue = new AcceptedArticleQueue();
        await using var harness = await Harness.StartAsync(queue);
        Assert.Equal(VatpStoreOutcome.Accepted, Outcome(await harness.StoreAndReadAsync(inbound)));

        var delivery = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(delivery.HasArticle);
        var queued = Encoding.ASCII.GetString(delivery.Article.ArtData.Span);
        Assert.True(delivery.Article.ArtData.Span.SequenceEqual(inbound.Record.ArtData.Span));
        Assert.Equal(1, CountHop(queued, CacheFqdn));
        Assert.Equal(Body(inboundText), Body(queued));
        Assert.Contains("news.usenet.ninja", queued, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Second_store_of_the_same_bytes_enqueues_another_unmodified_copy()
    {
        var inbound = Build("<store-again@seg.test>", "body\r\n");
        var queue = new AcceptedArticleQueue();
        await using var harness = await Harness.StartAsync(queue);
        Assert.Equal(VatpStoreOutcome.Accepted, Outcome(await harness.StoreAndReadAsync(inbound)));
        Assert.Equal(VatpStoreOutcome.Accepted, Outcome(await harness.StoreAndReadAsync(inbound, streamId: 2)));

        var first = await queue.DequeueAsync(CancellationToken.None);
        var second = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(first.HasArticle && second.HasArticle);
        Assert.True(first.Article.ArtData.Span.SequenceEqual(inbound.Record.ArtData.Span));
        Assert.True(second.Article.ArtData.Span.SequenceEqual(inbound.Record.ArtData.Span));
        Assert.Equal(0, CountHop(Encoding.ASCII.GetString(first.Article.ArtData.Span), CacheFqdn));
        Assert.Equal(0, CountHop(Encoding.ASCII.GetString(second.Article.ArtData.Span), CacheFqdn));
    }

    [Fact]
    public async Task Hash_id_and_size_mismatches_do_not_enqueue()
    {
        var real = Build("<store-real@seg.test>", "body\r\n");
        var other = Build("<store-other@seg.test>", "body\r\n");
        var queue = new AcceptedArticleQueue();

        await using var idMismatch = await Harness.StartAsync(queue);
        await idMismatch.SendRawAsync(StoreFrame(other.Record.ArtId, 1));
        await idMismatch.SendArticleBodyAsync(real, 1);
        Assert.Equal(VatpErrorCode.ArticleIdMismatch, FailCode(await idMismatch.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.Fail))));

        await using var hashMismatch = await Harness.StartAsync(queue);
        await hashMismatch.SendStoreAsync(real, corruptLastDataByte: true);
        Assert.Equal(VatpErrorCode.ArtHashMismatch, FailCode(await hashMismatch.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.Fail))));

        await using var sizeMismatch = await Harness.StartAsync(queue);
        await sizeMismatch.SendRawAsync(StoreFrame(real.Record.ArtId, 1));
        await sizeMismatch.SendRawAsync(MetaFrame(real, 1));
        var shortPayload = real.Record.ArtData.Slice(0, Math.Max(1, real.Record.ArtSize / 2));
        await sizeMismatch.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(
            VatpFrameEncoder.EncodeData(1, shortPayload, fin: true)));
        await sizeMismatch.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(1)));
        Assert.Equal(VatpErrorCode.ArtSizeMismatch, FailCode(await sizeMismatch.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.Fail))));

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await queue.DequeueAsync(timeout.Token));
    }

    [Fact]
    public async Task Oversized_meta_is_rejected_before_enqueue()
    {
        var built = Build("<store-big@seg.test>", "body\r\n");
        var queue = new AcceptedArticleQueue();
        await using var harness = await Harness.StartAsync(queue);
        await harness.SendRawAsync(StoreFrame(built.Record.ArtId, 1));
        var meta = MetaFrame(built, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
            meta.AsSpan(VatpProtocol.HeaderLengthBytes + 12, 4),
            ArticleResourceLimits.MaxArticleBytes + 1);
        await harness.SendRawAsync(meta);
        Assert.Equal(VatpErrorCode.ArticleTooLarge, FailCode(await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.Fail))));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await queue.DequeueAsync(timeout.Token));
    }

    [Fact]
    public async Task Completed_queue_rejects_the_store_without_dropping_a_queued_article()
    {
        var built = Build("<store-closed@seg.test>", "body\r\n");
        var queue = new AcceptedArticleQueue();
        queue.Complete();
        await using var harness = await Harness.StartAsync(queue);
        await harness.SendStoreAsync(built);
        Assert.Equal(VatpErrorCode.IncompleteTransfer, FailCode(await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.Fail))));
        var empty = await queue.DequeueAsync(CancellationToken.None);
        Assert.False(empty.HasArticle);
    }

    [Fact]
    public async Task Two_sessions_enqueue_distinct_articles()
    {
        var queue = new AcceptedArticleQueue();
        var first = Build("<store-a@seg.test>", "alpha\r\n");
        var second = Build("<store-b@seg.test>", "beta\r\n");
        await using var left = await Harness.StartAsync(queue);
        await using var right = await Harness.StartAsync(queue);
        var leftTask = left.StoreAndReadAsync(first);
        var rightTask = right.StoreAndReadAsync(second, streamId: 3);
        var leftFrames = await leftTask;
        var rightFrames = await rightTask;
        Assert.Equal(VatpStoreOutcome.Accepted, Outcome(leftFrames));
        Assert.Equal(VatpStoreOutcome.Accepted, Outcome(rightFrames));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 2; i++)
        {
            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.HasArticle);
            seen.Add(Encoding.ASCII.GetString(delivery.Article.ArtData.Span));
        }

        Assert.Contains(seen, static text => text.Contains("alpha\r\n", StringComparison.Ordinal));
        Assert.Contains(seen, static text => text.Contains("beta\r\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Windows_cover_every_payload_byte_and_precede_result()
    {
        var body = new StringBuilder(300_000);
        for (var line = 0; line < 3_000; line++)
        {
            body.Append('x', 98).Append("\r\n");
        }

        var built = Build("<store-window@seg.test>", body.ToString());
        var queue = new AcceptedArticleQueue();
        await using var harness = await Harness.StartAsync(queue);
        await harness.SendRawAsync(StoreFrame(built.Record.ArtId, 1));
        await harness.SendRawAsync(MetaFrame(built, 1));

        var art = built.Record.ArtData;
        var offset = 0;
        while (offset < art.Length)
        {
            var length = Math.Min(64 * 1024, art.Length - offset);
            var fin = offset + length == art.Length;
            await harness.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(
                VatpFrameEncoder.EncodeData(1, art.Slice(offset, length), fin)));
            offset += length;
        }

        await harness.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(1)));
        var frames = await harness.ReadUntilAsync(static received =>
            received.Any(frame => frame.Header.Type == VatpFrameType.Result));

        long credit = 0;
        var resultSeen = false;
        foreach (var frame in frames)
        {
            if (frame.Header.Type == VatpFrameType.Window)
            {
                Assert.False(resultSeen);
                Assert.Equal(1u, frame.Header.StreamId);
                Assert.True(VatpControlPayload.TryDecodeWindow(frame.Payload, out var add));
                credit += add;
            }

            if (frame.Header.Type == VatpFrameType.Result)
            {
                resultSeen = true;
                Assert.Equal(VatpStoreOutcome.Accepted, frame.Payload[0]);
            }
        }

        Assert.Equal(built.Record.ArtSize, credit);
        Assert.Equal(1, queue.EnqueuedCount);
    }

    [Fact]
    public void Default_receive_stream_starts_at_the_production_window()
    {
        var built = Build("<store-default-window@seg.test>", "body\r\n");
        var stream = new ArticleTransferReceiveStream(1, Guid.NewGuid(), built.Record.ArtId);
        Assert.Equal(1024L * 1024, stream.ReceiveCredit);
        Assert.Equal(1024 * 1024, ArticleTransferLimits.Default.InitialStreamWindowBytes);
    }

    [Fact]
    public async Task Explicit_initial_window_reaches_the_receive_stream_and_refunds_each_chunk()
    {
        const int initial = 1024 * 1024;
        var limits = new ArticleTransferLimits { InitialStreamWindowBytes = initial };
        var built = Build("<store-wide-window@seg.test>", "body-line\r\n");
        var stream = new ArticleTransferReceiveStream(1, Guid.NewGuid(), built.Record.ArtId, limits);
        Assert.Equal((long)initial, stream.ReceiveCredit);

        var queue = new AcceptedArticleQueue();
        await using var harness = await Harness.StartAsync(queue, limits);
        var frames = await harness.StoreAndReadAsync(built);
        Assert.Equal(VatpStoreOutcome.Accepted, Outcome(frames));
        long refunded = 0;
        foreach (var frame in frames)
        {
            if (frame.Header.Type != VatpFrameType.Window)
            {
                continue;
            }

            Assert.True(VatpControlPayload.TryDecodeWindow(frame.Payload, out var add));
            Assert.True(add <= 64 * 1024);
            refunded += add;
        }

        Assert.Equal(built.Record.ArtSize, refunded);
        Assert.Equal(1, queue.EnqueuedCount);
    }

    [Fact]
    public async Task Initial_window_smaller_than_a_chunk_is_a_flow_control_failure()
    {
        var limits = new ArticleTransferLimits { InitialStreamWindowBytes = 16 };
        var built = Build("<store-tight-window@seg.test>", "body-line\r\n");
        Assert.True(built.Record.ArtSize > limits.InitialStreamWindowBytes);
        var queue = new AcceptedArticleQueue();
        await using var harness = await Harness.StartAsync(queue, limits);
        await harness.SendStoreAsync(built);
        Assert.Equal(VatpErrorCode.FlowControlViolation, FailCode(await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.Fail))));
        Assert.Equal(0, queue.EnqueuedCount);
    }

    [Fact]
    public void Initial_window_above_the_max_credit_is_rejected()
    {
        var built = Build("<store-over-window@seg.test>", "body\r\n");
        var limits = new ArticleTransferLimits
        {
            InitialStreamWindowBytes = (int)ArticleTransferLimits.Default.MaxStreamCreditBytes + 1,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ArticleTransferReceiveStream(1, Guid.NewGuid(), built.Record.ArtId, limits));
    }

    [Fact]
    public async Task Split_reads_enqueue_a_large_article()
    {
        var body = new StringBuilder(768_000);
        for (var line = 0; line < 7_680; line++)
        {
            body.Append('y', 98).Append("\r\n");
        }

        var built = Build("<store-split@seg.test>", body.ToString());
        var framed = EncodeStore(built, 1);

        await AssertChunkedEnqueueAsync(built, framed, chunkSize: 7);
        await AssertChunkedEnqueueAsync(built, framed, chunkSize: 16 * 1024);
        await AssertChunkedEnqueueAsync(built, framed, chunkSize: framed.Length);
        await AssertExactFrameReadsAsync(built);
    }

    [Fact]
    public async Task Interleaved_streams_split_across_reads_both_enqueue()
    {
        var first = Build("<store-left@seg.test>", "left-body\r\n");
        var second = Build("<store-right@seg.test>", "right-body\r\n");
        var framed = EncodeStore(first, 1).Concat(EncodeStore(second, 2)).ToArray();
        var queue = new AcceptedArticleQueue();
        await using var harness = await Harness.StartAsync(queue);
        await harness.Transport.ClientWriteChunkedAsync(framed, 5);
        var frames = await harness.ReadUntilAsync(static received =>
            received.Count(static frame => frame.Header.Type == VatpFrameType.Result) == 2);

        Assert.Equal(2, frames.Count(static frame => frame.Header.Type == VatpFrameType.Result && frame.Payload[0] == VatpStoreOutcome.Accepted));
        Assert.Equal(2, queue.EnqueuedCount);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 2; i++)
        {
            var delivery = await queue.DequeueAsync(CancellationToken.None);
            Assert.True(delivery.HasArticle);
            seen.Add(Encoding.ASCII.GetString(delivery.Article.ArtData.Span));
        }

        Assert.Contains(seen, static text => text.Contains("left-body\r\n", StringComparison.Ordinal));
        Assert.Contains(seen, static text => text.Contains("right-body\r\n", StringComparison.Ordinal));
    }

    private static async Task AssertChunkedEnqueueAsync(Built built, byte[] framed, int chunkSize)
    {
        var queue = new AcceptedArticleQueue();
        await using var harness = await Harness.StartAsync(queue);
        await harness.Transport.ClientWriteChunkedAsync(framed, chunkSize);
        var frames = await harness.ReadUntilAsync(static received =>
            received.Any(frame => frame.Header.Type == VatpFrameType.Result));
        Assert.Equal(VatpStoreOutcome.Accepted, Outcome(frames));
        var delivery = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(delivery.HasArticle);
        Assert.True(delivery.Article.ArtData.Span.SequenceEqual(built.Record.ArtData.Span));
        Assert.Equal(1, queue.EnqueuedCount);
    }

    private static async Task AssertExactFrameReadsAsync(Built built)
    {
        var queue = new AcceptedArticleQueue();
        await using var harness = await Harness.StartAsync(queue);
        foreach (var frame in EnumerateStoreFrames(built, 1))
        {
            await harness.SendRawAsync(frame);
        }

        var frames = await harness.ReadUntilAsync(static received =>
            received.Any(frame => frame.Header.Type == VatpFrameType.Result));
        Assert.Equal(VatpStoreOutcome.Accepted, Outcome(frames));
        var delivery = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(delivery.HasArticle);
        Assert.True(delivery.Article.ArtData.Span.SequenceEqual(built.Record.ArtData.Span));
    }

    private static byte[] EncodeStore(Built built, uint streamId)
    {
        var parts = EnumerateStoreFrames(built, streamId);
        var length = 0;
        foreach (var part in parts)
        {
            length += part.Length;
        }

        var framed = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(framed.AsSpan(offset));
            offset += part.Length;
        }

        return framed;
    }

    private static List<byte[]> EnumerateStoreFrames(Built built, uint streamId)
    {
        var parts = new List<byte[]>
        {
            StoreFrame(built.Record.ArtId, streamId),
            MetaFrame(built, streamId),
        };
        var art = built.Record.ArtData;
        var offset = 0;
        while (offset < art.Length)
        {
            var length = Math.Min(64 * 1024, art.Length - offset);
            var fin = offset + length == art.Length;
            parts.Add(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeData(streamId, art.Slice(offset, length), fin)));
            offset += length;
        }

        parts.Add(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(streamId)));
        return parts;
    }

    private static int CountHop(string article, string hop) =>
        article.Split(hop, StringSplitOptions.None).Length - 1;

    private static string Body(string article)
    {
        var split = article.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(split >= 0);
        return article[(split + 4)..];
    }

    private static byte Outcome(IReadOnlyList<Decoded> frames) =>
        frames.Last(static frame => frame.Header.Type == VatpFrameType.Result).Payload[0];

    private static VatpErrorCode FailCode(IReadOnlyList<Decoded> frames)
    {
        var payload = frames.Last(static frame => frame.Header.Type == VatpFrameType.Fail).Payload;
        Assert.True(VatpControlPayload.TryDecodeFail(payload, out var error, out _));
        return error;
    }

    private static Built Build(string messageId, string body, string ingressFqdn = "nntpd01.usenet.ninja")
    {
        var parser = new NntpArticleParser(ingressFqdn);
        var text = new StringBuilder()
            .Append("Path: peer.example\r\n")
            .Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n")
            .Append("Message-ID: ").Append(messageId).Append("\r\n")
            .Append("Newsgroups: alt.test\r\n")
            .Append("From: user@example.test\r\n")
            .Append("Subject: vatp\r\n")
            .Append("\r\n")
            .Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(text.ToString()));
        Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
        return new Built(created.Record, created.SelectedDateHeaderName);
    }

    private static byte[] StoreFrame(ArticleId articleId, uint streamId)
    {
        var id = new byte[ArticleId.Length];
        articleId.CopyTo(id);
        return VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeStore(streamId, id));
    }

    private static byte[] MetaFrame(Built built, uint streamId)
    {
        var meta = ArticleCanonicalTransferMeta.FromRecord(built.Record, built.SelectedDateHeaderName);
        return VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeMeta(streamId, VatpMetaCodec.Encode(in meta)));
    }

    private readonly record struct Built(ArticleRecord Record, NntpArticleHeaderName SelectedDateHeaderName);

    private readonly record struct Decoded(VatpFrameHeader Header, byte[] Payload);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;

        private Harness(PipeTransport transport, StorageVatpSession session)
        {
            Transport = transport;
            _run = session.RunAsync(_cts.Token);
        }

        public PipeTransport Transport { get; }

        public static async Task<Harness> StartAsync(IAcceptedArticleQueue queue, ArticleTransferLimits? limits = null)
        {
            var transport = new PipeTransport();
            var session = new StorageVatpSession(
                transport,
                NullStorageArticleOpenBoundary.Instance,
                new StorageServerListenerRuntimeOptions(262144, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 8),
                NullLogger.Instance,
                limits,
                acceptedArticles: queue,
                storeAdmission: new StoreAssemblyAdmission(8));
            var harness = new Harness(transport, session);
            await harness.Transport.ClientWriteAsync(VatpFrameEncoder.ToSingleBuffer(
                VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload)));
            _ = await harness.ReadUntilAsync(static frames => frames.Any(frame => frame.Header.Type == VatpFrameType.Hello));
            return harness;
        }

        public async Task SendStoreAsync(Built built, uint streamId = 1, bool corruptLastDataByte = false)
        {
            await SendRawAsync(StoreFrame(built.Record.ArtId, streamId));
            await SendArticleBodyAsync(built, streamId, corruptLastDataByte);
        }

        public async Task SendArticleBodyAsync(Built built, uint streamId, bool corruptLastDataByte = false)
        {
            await SendRawAsync(MetaFrame(built, streamId));
            var payload = built.Record.ArtData.ToArray();
            if (corruptLastDataByte)
            {
                payload[^1] ^= 0xFF;
            }

            await SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeData(streamId, payload, fin: true)));
            await SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(streamId)));
        }

        public Task SendRawAsync(byte[] frame) => Transport.ClientWriteAsync(frame);

        public async Task<IReadOnlyList<Decoded>> StoreAndReadAsync(Built built, uint streamId = 1)
        {
            await SendRawAsync(StoreFrame(built.Record.ArtId, streamId));
            await SendArticleBodyAsync(built, streamId);
            return await ReadUntilAsync(static frames => frames.Any(frame => frame.Header.Type == VatpFrameType.Result));
        }

        public async Task<IReadOnlyList<Decoded>> ReadUntilAsync(Func<IReadOnlyList<Decoded>, bool> done)
        {
            using var timeout = new CancellationTokenSource(Safety);
            var buffer = new List<byte>();
            var frames = new List<Decoded>();
            while (!done(frames))
            {
                var chunk = await Transport.ReadServerAsync(timeout.Token);
                buffer.AddRange(chunk);
                var offset = 0;
                while (offset < buffer.Count)
                {
                    var parsed = VatpFrameParser.ParseOneFrame(buffer.ToArray(), VatpProtocol.DefaultMaxFramePayload);
                    if (parsed.Status == VatpFrameParseStatus.Incomplete)
                    {
                        break;
                    }

                    Assert.Equal(VatpFrameParseStatus.Success, parsed.Status);
                    var frame = parsed.Frame!.Value;
                    var payload = new byte[frame.Payload.Length];
                    frame.Payload.CopyTo(payload);
                    frames.Add(new Decoded(frame.Header, payload));
                    offset += checked((int)parsed.ConsumedBytes);
                    buffer.RemoveRange(0, checked((int)parsed.ConsumedBytes));
                }
            }

            return frames;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _run;
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
            await Transport.DisposeAsync();
        }
    }

    private sealed class PipeTransport : IStorageVatpTransport
    {
        private readonly Channel<byte[]> _toServer = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _toClient = Channel.CreateUnbounded<byte[]>();
        private byte[] _pending = [];
        private int _pendingOffset;

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_pendingOffset >= _pending.Length)
            {
                _pending = await _toServer.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                _pendingOffset = 0;
            }

            var count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
            _pending.AsSpan(_pendingOffset, count).CopyTo(buffer.Span);
            _pendingOffset += count;
            return count;
        }

        public async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            await _toClient.Writer.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
            return buffer.Length;
        }

        public Task ClientWriteAsync(byte[] frame) => _toServer.Writer.WriteAsync(frame).AsTask();

        public async Task ClientWriteChunkedAsync(byte[] frame, int chunkSize)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
            var offset = 0;
            while (offset < frame.Length)
            {
                var count = Math.Min(chunkSize, frame.Length - offset);
                var slice = new byte[count];
                frame.AsSpan(offset, count).CopyTo(slice);
                await _toServer.Writer.WriteAsync(slice).ConfigureAwait(false);
                offset += count;
            }
        }

        public Task<byte[]> ReadServerAsync(CancellationToken cancellationToken) =>
            _toClient.Reader.ReadAsync(cancellationToken).AsTask();

        public ValueTask DisposeAsync()
        {
            _toServer.Writer.TryComplete();
            _toClient.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
