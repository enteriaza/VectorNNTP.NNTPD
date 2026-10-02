using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Listener;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.Memory;

namespace VectorNNTP.StorageServer.Tests.Listener;

public sealed class StorageVatpStoreTests
{
    private const string CacheFqdn = "cache01.usenet.ninja";
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Store_ResultAccepted_OnlyAfterAcceptReturns()
    {
        var built = Build("<store-accept@seg.test>", "body\r\n");
        var gate = new GateEngine(CreateMemory());
        await using var harness = await Harness.StartAsync(gate);
        await harness.SendStoreAsync(built);
        await gate.Entered.Task.WaitAsync(Safety);
        Assert.DoesNotContain(harness.DrainAvailable(), static frame => frame.Header.Type == VatpFrameType.Result);
        gate.Release.TrySetResult();
        var frames = await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.Result));
        var result = Assert.Single(frames, static frame => frame.Header.Type == VatpFrameType.Result);
        Assert.Equal((byte)ArticleAcceptOutcome.Accepted, result.Payload[0]);
        Assert.Equal(1, gate.AcceptCalls);
        await WaitPresentAsync(gate.Inner, built.Record.ArtId);
    }

    [Fact]
    public async Task Store_DuplicateIsSuccess_AndConflictDoesNotOverwrite()
    {
        var first = Build("<store-id@seg.test>", "one\r\n");
        var conflict = Build("<store-id@seg.test>", "two\r\n");
        var engine = CreateMemory();
        await using var harness = await Harness.StartAsync(engine);
        var accepted = await harness.StoreAndReadAsync(first);
        Assert.Equal((byte)ArticleAcceptOutcome.Accepted, Outcome(accepted));
        var duplicate = await harness.StoreAndReadAsync(first, streamId: 2);
        Assert.Equal((byte)ArticleAcceptOutcome.Duplicate, Outcome(duplicate));
        var conflicted = await harness.StoreAndReadAsync(conflict, streamId: 3);
        Assert.Equal((byte)ArticleAcceptOutcome.Conflict, Outcome(conflicted));
        var read = await WaitPresentAsync(engine, first.Record.ArtId);
        var stored = Stored(first);
        Assert.Equal(first.Record.ArtId, read.Metadata.ArtId);
        Assert.Equal(stored.ArtHash, read.Metadata.ArtHash);
        Assert.True(read.ArtData.Span.SequenceEqual(stored.ArtData.Span));
        Assert.False(read.ArtData.Span.SequenceEqual(conflict.Record.ArtData.Span));
    }

    [Fact]
    public async Task Store_TerminalOutcomes_AreResultBytes()
    {
        var built = Build("<store-terminal@seg.test>", "body\r\n");
        foreach (var outcome in new[]
                 {
                     ArticleAcceptOutcome.RejectedCapacity,
                     ArticleAcceptOutcome.RejectedPressure,
                     ArticleAcceptOutcome.RejectedInvalid,
                 })
        {
            var engine = new ScriptEngine(outcome);
            await using var harness = await Harness.StartAsync(engine);
            var frames = await harness.StoreAndReadAsync(built);
            Assert.Equal((byte)outcome, Outcome(frames));
            Assert.DoesNotContain(frames, static frame => frame.Header.Type == VatpFrameType.Fail);
            Assert.Equal(1, engine.AcceptCalls);
            Assert.False(engine.AppendedJournal);
        }
    }

    [Fact]
    public async Task Store_Mismatches_DoNotCallAccept()
    {
        var real = Build("<store-real@seg.test>", "body\r\n");
        var other = Build("<store-other@seg.test>", "body\r\n");
        var engine = new ScriptEngine(ArticleAcceptOutcome.Accepted);

        await using var idMismatch = await Harness.StartAsync(engine);
        await idMismatch.SendRawAsync(StoreFrame(other.Record.ArtId, 1));
        await idMismatch.SendArticleBodyAsync(real, 1);
        var idFrames = await idMismatch.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.ArticleIdMismatch, FailCode(idFrames));
        Assert.Equal(0, engine.AcceptCalls);

        engine.AcceptCalls = 0;
        await using var hashMismatch = await Harness.StartAsync(engine);
        await hashMismatch.SendStoreAsync(real, corruptLastDataByte: true);
        var hashFrames = await hashMismatch.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.ArtHashMismatch, FailCode(hashFrames));
        Assert.Equal(0, engine.AcceptCalls);

        engine.AcceptCalls = 0;
        await using var sizeMismatch = await Harness.StartAsync(engine);
        await sizeMismatch.SendRawAsync(StoreFrame(real.Record.ArtId, 1));
        await sizeMismatch.SendRawAsync(MetaFrame(real, 1));
        var shortPayload = real.Record.ArtData.Slice(0, Math.Max(1, real.Record.ArtSize / 2));
        await sizeMismatch.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(
            VatpFrameEncoder.EncodeData(1, shortPayload, fin: true)));
        await sizeMismatch.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(1)));
        var sizeFrames = await sizeMismatch.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.ArtSizeMismatch, FailCode(sizeFrames));
        Assert.Equal(0, engine.AcceptCalls);
    }

    [Fact]
    public async Task Store_AdmissionCap_RejectsBeforeAccept()
    {
        var built = Build("<store-cap@seg.test>", "body\r\n");
        var engine = new ScriptEngine(ArticleAcceptOutcome.Accepted);
        await using var harness = await Harness.StartAsync(engine, admission: new StoreAssemblyAdmission(1));
        await harness.SendRawAsync(StoreFrame(built.Record.ArtId, 1));
        await harness.SendRawAsync(StoreFrame(built.Record.ArtId, 2));
        var frames = await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.Fail && frame.Header.StreamId == 2));
        Assert.Equal(VatpErrorCode.StreamTableError, FailCode(frames));
        Assert.Equal(0, engine.AcceptCalls);
    }

    [Fact]
    public async Task Store_CancelBeforeAccept_DoesNotCallAccept()
    {
        var built = Build("<store-cancel@seg.test>", "body\r\n");
        var engine = new ScriptEngine(ArticleAcceptOutcome.Accepted);
        await using var harness = await Harness.StartAsync(engine);
        await harness.SendRawAsync(StoreFrame(built.Record.ArtId, 1));
        await harness.SendRawAsync(MetaFrame(built, 1));
        await harness.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(
            VatpFrameEncoder.EncodeData(1, built.Record.ArtData, fin: true)));
        await harness.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeCancel(1)));
        var frames = await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.Fail));
        Assert.Equal(VatpErrorCode.Cancelled, FailCode(frames));
        Assert.Equal(0, engine.AcceptCalls);
    }

    [Fact]
    public async Task Store_CancelAfterAccepted_DoesNotEvict()
    {
        var built = Build("<store-keep@seg.test>", "body\r\n");
        var engine = CreateMemory();
        await using var harness = await Harness.StartAsync(engine);
        var frames = await harness.StoreAndReadAsync(built);
        Assert.Equal((byte)ArticleAcceptOutcome.Accepted, Outcome(frames));
        await harness.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeCancel(1)));
        var still = await WaitPresentAsync(engine, built.Record.ArtId);
        Assert.Equal(Stored(built).ArtHash, still.Metadata.ArtHash);
    }

    [Fact]
    public async Task Store_WindowPacing_FinOnlyOnFinalData()
    {
        var built = Build("<store-window@seg.test>", new string('w', 80));
        var engine = CreateMemory();
        var limits = new ArticleTransferLimits
        {
            InitialStreamWindowBytes = 24,
            MaxStreamCreditBytes = 1024 * 1024,
        };
        await using var harness = await Harness.StartAsync(engine, limits);
        await harness.SendRawAsync(StoreFrame(built.Record.ArtId, 1));
        await harness.SendRawAsync(MetaFrame(built, 1));
        var art = built.Record.ArtData;
        var offset = 0;
        const int credit = 24;
        while (offset < art.Length)
        {
            var take = Math.Min(credit, art.Length - offset);
            var fin = offset + take == art.Length;
            await harness.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(
                VatpFrameEncoder.EncodeData(1, art.Slice(offset, take), fin)));
            offset += take;
            if (!fin)
            {
                var window = await harness.ReadUntilAsync(static frames =>
                    frames.Any(frame => frame.Header.Type == VatpFrameType.Window));
                Assert.Equal((uint)take, BinaryPrimitives.ReadUInt32BigEndian(
                    window.Last(static frame => frame.Header.Type == VatpFrameType.Window).Payload));
            }
        }

        await harness.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(1)));
        var done = await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.Result));
        Assert.Equal((byte)ArticleAcceptOutcome.Accepted, Outcome(done));
    }

    [Fact]
    public async Task Open_StillServesPresentArticle_OnStoreSession()
    {
        var built = Build("<store-open@seg.test>", "served\r\n");
        var engine = CreateMemory();
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(built.Record, CancellationToken.None)).Outcome);
        await using var harness = await Harness.StartAsync(engine, open: new FixedOpen([built]));
        var id = new byte[ArticleId.Length];
        built.Record.ArtId.CopyTo(id);
        await harness.SendRawAsync(VatpFrameEncoder.ToSingleBuffer(
            VatpFrameEncoder.EncodeOpen(4, Guid.NewGuid(), id)));
        var frames = await harness.ReadUntilAsync(static frames =>
            frames.Any(frame => frame.Header.Type == VatpFrameType.End && frame.Header.StreamId == 4));
        Assert.Contains(frames, static frame => frame.Header.Type == VatpFrameType.Meta);
        Assert.Contains(frames, static frame => frame.Header.Type == VatpFrameType.Data && frame.Header.HasFin);
    }

    [Fact]
    public async Task Store_DropAfterJournalFlush_RecoversAccept()
    {
        var built = Build("<store-crash@seg.test>", "durable\r\n");
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options, NullLogger.Instance);
        var transport = new Loopback { FailOnResult = true };
        var session = new StorageVatpSession(
            transport,
            new FixedOpen([]),
            ListenerOptions(),
            NullLogger.Instance,
            placementEngine: engine,
            storeAdmission: new StoreAssemblyAdmission(1),
            storePathIdentity: CacheFqdn);
        using var cts = new CancellationTokenSource();
        var run = session.RunAsync(cts.Token);
        await transport.ClientWriteAsync(VatpFrameEncoder.ToSingleBuffer(
            VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload)));
        await transport.ClientWriteAsync(StoreFrame(built.Record.ArtId, 1));
        await transport.ClientWriteAsync(MetaFrame(built, 1));
        await transport.ClientWriteAsync(VatpFrameEncoder.ToSingleBuffer(
            VatpFrameEncoder.EncodeData(1, built.Record.ArtData, fin: true)));
        await transport.ClientWriteAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(1)));
        await Assert.ThrowsAnyAsync<IOException>(async () => await run.WaitAsync(Safety));
        await engine.DisposeAsync();
        await using var recovered = FileArticleStorageEngine.Open(dir.Options, NullLogger.Instance);
        await recovered.RecoverAsync(CancellationToken.None);
        Assert.True(recovered.TryRead(built.Record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(Stored(built).ArtData.Span));
        cts.Cancel();
        await session.DisposeAsync();
    }

    private static async Task<ArticleReadResult> WaitPresentAsync(IArticleStorageEngine engine, ArticleId articleId)
    {
        using var timeout = new CancellationTokenSource(Safety);
        while (!engine.TryRead(articleId, out _))
        {
            await Task.Delay(1, timeout.Token);
        }

        Assert.True(engine.TryRead(articleId, out var read));
        return read;
    }

    private static byte Outcome(IReadOnlyList<Decoded> frames) =>
        frames.Last(static frame => frame.Header.Type == VatpFrameType.Result).Payload[0];

    private static VatpErrorCode FailCode(IReadOnlyList<Decoded> frames)
    {
        var payload = frames.Last(static frame => frame.Header.Type == VatpFrameType.Fail).Payload;
        Assert.True(VatpControlPayload.TryDecodeFail(payload, out var error, out _));
        return error;
    }

    private static MemoryArticleStorageEngine CreateMemory() =>
        new(new ArticleStorageRuntimeOptions("control", "segments", 1024 * 1024, 8 * 1024 * 1024, 1024 * 1024));

    private static Built Build(string messageId, string body)
    {
        var parser = new NntpArticleParser("nntpd01.usenet.ninja");
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

    private static ArticleRecord Stored(Built inbound)
    {
        var created = ArticleRecordFactory.TryCreate(new NntpArticleParser(CacheFqdn), inbound.Record.ArtData);
        Assert.True(created.IsAccepted, created.MaterializeFailure.ToString());
        Assert.Equal(inbound.Record.ArtId, created.Record.ArtId);
        return created.Record;
    }

    [Fact]
    public void StoreRewrite_WhenCacheHopAlreadyPresent_PrependsAgain()
    {
        var inbound = Build("<store-path-again@seg.test>", "body\r\n");
        var once = Stored(inbound);
        var again = ArticleRecordFactory.TryCreate(new NntpArticleParser(CacheFqdn), once.ArtData);
        Assert.True(again.IsAccepted, again.MaterializeFailure.ToString());
        Assert.Equal(once.ArtId, again.Record.ArtId);
        Assert.NotEqual(once.ArtHash, again.Record.ArtHash);
        var path = Encoding.ASCII.GetString(again.Record.Path);
        Assert.StartsWith("cache01.usenet.ninja!cache01.usenet.ninja!", path, StringComparison.Ordinal);
        Assert.Equal(2, CountHop(path, "cache01.usenet.ninja"));
        Assert.Equal(1, CountHop(path, "news.usenet.ninja"));
    }

    [Fact]
    public async Task Store_PrependsCacheHop_AndRetryIsDuplicateWithoutASecondHop()
    {
        var inbound = Build("<store-path@seg.test>", "body\r\n");
        var engine = CreateMemory();
        await using var harness = await Harness.StartAsync(engine);
        var accepted = await harness.StoreAndReadAsync(inbound);
        Assert.Equal((byte)ArticleAcceptOutcome.Accepted, Outcome(accepted));

        var stored = Stored(inbound);
        var read = await WaitPresentAsync(engine, inbound.Record.ArtId);
        Assert.Equal(inbound.Record.ArtId, read.Metadata.ArtId);
        Assert.NotEqual(inbound.Record.ArtHash, read.Metadata.ArtHash);
        Assert.Equal(stored.ArtHash, read.Metadata.ArtHash);
        Assert.True(read.ArtData.Span.SequenceEqual(stored.ArtData.Span));
        var path = Encoding.ASCII.GetString(stored.Path);
        Assert.StartsWith("cache01.usenet.ninja!", path, StringComparison.Ordinal);
        Assert.Contains("!nntpd01.usenet.ninja!", path, StringComparison.Ordinal);
        Assert.Equal(1, CountHop(path, "cache01.usenet.ninja"));
        Assert.Contains("news.usenet.ninja", path, StringComparison.Ordinal);

        var duplicate = await harness.StoreAndReadAsync(inbound, streamId: 2);
        Assert.Equal((byte)ArticleAcceptOutcome.Duplicate, Outcome(duplicate));
        var again = await WaitPresentAsync(engine, inbound.Record.ArtId);
        Assert.True(again.ArtData.Span.SequenceEqual(stored.ArtData.Span));
        Assert.Equal(1, CountHop(Encoding.ASCII.GetString(stored.Path), "cache01.usenet.ninja"));

        var served = ArticleRecordFactory.TryCreate(
            new NntpArticleParser(ArticlePathCanonicalizer.OrganizationalTrackerHost),
            again.ArtData,
            ArticlePathMode.Normalize);
        Assert.True(served.IsAccepted);
        Assert.True(served.Record.ArtData.Span.SequenceEqual(again.ArtData.Span));
        Assert.Equal(stored.ArtHash, served.Record.ArtHash);
    }

    private static int CountHop(string path, string hop)
    {
        var count = 0;
        foreach (var component in path.Split('!'))
        {
            if (string.Equals(component, hop, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
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

    private static StorageServerListenerRuntimeOptions ListenerOptions() =>
        new(262144, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 8);

    private readonly record struct Built(ArticleRecord Record, NntpArticleHeaderName SelectedDateHeaderName);

    private readonly record struct Decoded(VatpFrameHeader Header, byte[] Payload);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;
        private readonly StorageVatpSession _session;

        private Harness(Loopback transport, StorageVatpSession session)
        {
            Transport = transport;
            _session = session;
            _run = session.RunAsync(_cts.Token);
        }

        public Loopback Transport { get; }

        public static async Task<Harness> StartAsync(
            IArticleStorageEngine engine,
            ArticleTransferLimits? limits = null,
            StoreAssemblyAdmission? admission = null,
            IStorageArticleOpenBoundary? open = null)
        {
            var transport = new Loopback();
            var session = new StorageVatpSession(
                transport,
                open ?? new FixedOpen([]),
                ListenerOptions(),
                NullLogger.Instance,
                limits,
                engine,
                admission ?? new StoreAssemblyAdmission(4),
                CacheFqdn);
            var harness = new Harness(transport, session);
            await harness.SendHelloAsync();
            return harness;
        }

        public Task SendHelloAsync() =>
            Transport.ClientWriteAsync(VatpFrameEncoder.ToSingleBuffer(
                VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload))).AsTask();

        public async Task SendStoreAsync(Built built, uint streamId = 1, bool corruptLastDataByte = false)
        {
            await SendRawAsync(StoreFrame(built.Record.ArtId, streamId));
            await SendArticleBodyAsync(built, streamId, corruptLastDataByte);
        }

        public async Task SendArticleBodyAsync(Built built, uint streamId, bool corruptLastDataByte = false)
        {
            await SendRawAsync(MetaFrame(built, streamId));
            var payload = built.Record.ArtData.ToArray();
            if (corruptLastDataByte && payload.Length > 0)
            {
                payload[^1] ^= 0xFF;
            }

            await SendRawAsync(VatpFrameEncoder.ToSingleBuffer(
                VatpFrameEncoder.EncodeData(streamId, payload, fin: true)));
            await SendRawAsync(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(streamId)));
        }

        public Task SendRawAsync(byte[] frame) => Transport.ClientWriteAsync(frame).AsTask();

        public IReadOnlyList<Decoded> DrainAvailable() => Transport.DrainAvailable();

        public async Task<IReadOnlyList<Decoded>> ReadUntilAsync(Func<IReadOnlyList<Decoded>, bool> done)
        {
            using var timeout = new CancellationTokenSource(Safety);
            return await Transport.ReadUntilAsync(done, timeout.Token);
        }

        public async Task<IReadOnlyList<Decoded>> StoreAndReadAsync(Built built, uint streamId = 1)
        {
            await SendStoreAsync(built, streamId);
            return await ReadUntilAsync(frames =>
                frames.Any(frame => frame.Header.Type == VatpFrameType.Result && frame.Header.StreamId == streamId));
        }

        public async ValueTask DisposeAsync()
        {
            Transport.ClientClose();
            _cts.Cancel();
            try
            {
                await _run.WaitAsync(Safety);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }

            await _session.DisposeAsync();
            _cts.Dispose();
        }
    }

    private sealed class Loopback : IStorageVatpTransport
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>();
        private readonly List<byte> _pending = [];
        private readonly List<Decoded> _parsed = [];
        private byte[]? _current;
        private int _offset;

        public bool FailOnResult { get; set; }

        public ValueTask ClientWriteAsync(byte[] frame) => new(_inbound.Writer.WriteAsync(frame).AsTask());

        public void ClientClose() => _inbound.Writer.TryComplete();

        public IReadOnlyList<Decoded> DrainAvailable()
        {
            while (_outbound.Reader.TryRead(out var chunk))
            {
                _pending.AddRange(chunk);
                Drain();
            }

            var copy = _parsed.ToArray();
            _parsed.Clear();
            return copy;
        }

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
            if (FailOnResult
                && buffer.Length == VatpProtocol.HeaderLengthBytes
                && buffer.Span[1] == (byte)VatpFrameType.Result)
            {
                throw new IOException("result-dropped");
            }

            await _outbound.Writer.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
            return buffer.Length;
        }

        public async Task<IReadOnlyList<Decoded>> ReadUntilAsync(
            Func<IReadOnlyList<Decoded>, bool> done,
            CancellationToken cancellationToken)
        {
            while (!done(_parsed))
            {
                var chunk = await _outbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                _pending.AddRange(chunk);
                Drain();
            }

            var copy = _parsed.ToArray();
            _parsed.Clear();
            return copy;
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

        private void Drain()
        {
            while (_pending.Count > 0)
            {
                var parsed = VatpFrameParser.ParseOneFrame(_pending.ToArray(), VatpProtocol.DefaultMaxFramePayload);
                if (parsed.Status == VatpFrameParseStatus.Incomplete)
                {
                    return;
                }

                if (parsed.Status != VatpFrameParseStatus.Success || parsed.Frame is not { } frame)
                {
                    throw new InvalidOperationException(parsed.Error.ToString());
                }

                var payload = CopyPayload(frame.Payload);
                _parsed.Add(new Decoded(frame.Header, payload));
                _pending.RemoveRange(0, checked((int)parsed.ConsumedBytes));
            }
        }
    }

    private sealed class FixedOpen(Built[] articles) : IStorageArticleOpenBoundary
    {
        public StorageArticleOpenResult TryOpen(Guid requestId, in ArticleId articleId)
        {
            foreach (var article in articles)
            {
                if (article.Record.ArtId.Equals(articleId))
                {
                    return new StorageArticleOpenResult(true, null, article.Record, article.SelectedDateHeaderName);
                }
            }

            return StorageArticleOpenResult.Rejected("missing");
        }
    }

    private sealed class ScriptEngine(ArticleAcceptOutcome outcome) : IArticleStorageEngine
    {
        public int AcceptCalls { get; set; }

        public bool AppendedJournal { get; private set; }

        public Task<ArticleAcceptResult> AcceptAsync(ArticleRecord record, CancellationToken cancellationToken)
        {
            AcceptCalls++;
            return Task.FromResult(outcome switch
            {
                ArticleAcceptOutcome.Accepted => ArticleAcceptResult.Accepted(record.ArtId, 1),
                ArticleAcceptOutcome.Duplicate => ArticleAcceptResult.Duplicate(record.ArtId),
                ArticleAcceptOutcome.Conflict => ArticleAcceptResult.Conflict(record.ArtId),
                ArticleAcceptOutcome.RejectedPressure => ArticleAcceptResult.RejectedPressure(record.ArtId),
                ArticleAcceptOutcome.RejectedCapacity => ArticleAcceptResult.RejectedCapacity(record.ArtId),
                _ => ArticleAcceptResult.RejectedInvalid(record.ArtId, "invalid"),
            });
        }

        public bool TryRead(ArticleId artId, out ArticleReadResult result)
        {
            result = default;
            return false;
        }

        public bool TryEvict(ArticleId artId) => false;

        public bool TryInvalidate(ArticleId artId) => false;

        public StorageWritePressure GetWritePressure() => StorageWritePressure.Normal;
    }

    private sealed class GateEngine(IArticleStorageEngine inner) : IArticleStorageEngine
    {
        public IArticleStorageEngine Inner { get; } = inner;

        public int AcceptCalls { get; private set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ArticleAcceptResult> AcceptAsync(ArticleRecord record, CancellationToken cancellationToken)
        {
            AcceptCalls++;
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return await Inner.AcceptAsync(record, cancellationToken).ConfigureAwait(false);
        }

        public bool TryRead(ArticleId artId, out ArticleReadResult result) => Inner.TryRead(artId, out result);

        public bool TryEvict(ArticleId artId) => Inner.TryEvict(artId);

        public bool TryInvalidate(ArticleId artId) => Inner.TryInvalidate(artId);

        public StorageWritePressure GetWritePressure() => Inner.GetWritePressure();
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-store-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            return new TempStorageDir(
                root,
                new ArticleStorageRuntimeOptions(
                    control,
                    cache,
                    ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    ArticleStorageOptions.DefaultJournalHardLimitBytes,
                    ArticleStorageOptions.DefaultSegmentTargetSizeBytes));
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
