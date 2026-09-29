using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.History;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.RabbitMq.ArticleWork;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.CommandProcessor;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.TestDoubles;
using VectorNNTP.NNTPD.Transport.Vatp;

namespace VectorNNTP.NNTPD.Tests.Session;

/// <summary>
/// Message-id ARTICLE/HEAD/BODY/STAT through ArticleWork → VATP → CanonicalV1 ArticleRecord.
/// </summary>
public sealed class ArticleRetrievalVatpCommandTests
{
    private static readonly NntpAuthorization Reader = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: false,
        streamingPermitted: false);

    private const string MessageId = "<vatp-article@example.test>";
    private const string CacheUri = "cache://backfiller.test:119/dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14";

    [Theory]
    [InlineData("ARTICLE")]
    [InlineData("HEAD")]
    [InlineData("BODY")]
    [InlineData("STAT")]
    public async Task MessageId_Success_ServesFromVatp(string verb)
    {
        var prepared = PrepareArticle(MessageId, body: "hello\r\n.dot\r\n");
        await using var duplex = await ArticleDuplex.CreateAsync();
        var queue = new RecordingIngestionQueue();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, CacheUri);
        var vatp = new StubVatpArticleClient(VatpFetchResult.FromSuccess(prepared.Record, prepared.RequestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, ingestion: queue);

        await DispatchLineAsync(duplex, session, $"{verb} {MessageId}");

        switch (verb)
        {
            case "ARTICLE":
                {
                    var wire = Encoding.ASCII.GetString(await duplex.ReadMultilineAsync());
                    Assert.StartsWith($"220 0 {MessageId}\r\n", wire, StringComparison.Ordinal);
                    Assert.Contains("Message-ID: " + MessageId, wire, StringComparison.Ordinal);
                    Assert.Contains("hello\r\n", wire, StringComparison.Ordinal);
                    Assert.Contains("..dot\r\n", wire, StringComparison.Ordinal); // restuffed
                    Assert.EndsWith(".\r\n", wire, StringComparison.Ordinal);
                    break;
                }
            case "HEAD":
                {
                    var wire = Encoding.ASCII.GetString(await duplex.ReadMultilineAsync());
                    Assert.StartsWith($"221 0 {MessageId}\r\n", wire, StringComparison.Ordinal);
                    Assert.Contains("Message-ID: " + MessageId, wire, StringComparison.Ordinal);
                    Assert.DoesNotContain("hello", wire, StringComparison.Ordinal);
                    Assert.EndsWith(".\r\n", wire, StringComparison.Ordinal);
                    break;
                }
            case "BODY":
                {
                    var wire = Encoding.ASCII.GetString(await duplex.ReadMultilineAsync());
                    Assert.StartsWith($"222 0 {MessageId}\r\n", wire, StringComparison.Ordinal);
                    Assert.DoesNotContain("Message-ID:", wire, StringComparison.Ordinal);
                    Assert.Contains("hello\r\n", wire, StringComparison.Ordinal);
                    Assert.Contains("..dot\r\n", wire, StringComparison.Ordinal);
                    Assert.EndsWith(".\r\n", wire, StringComparison.Ordinal);
                    break;
                }
            case "STAT":
                Assert.Equal($"223 0 {MessageId}", await duplex.ReadClientLineAsync());
                break;
        }

        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(InboundArticleProducer.BackFiller, Assert.Single(queue.Admitted).Producer);
        Assert.True(queue.Admitted[0].Payload.Equals(prepared.Record.ArtData));
    }

    [Fact]
    public async Task Article_Head_Body_Share_SingleFetchPerCommand()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, CacheUri);
        var vatp = new StubVatpArticleClient(VatpFetchResult.FromSuccess(prepared.Record, prepared.RequestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp);

        await DispatchLineAsync(duplex, session, $"HEAD {MessageId}");
        _ = await duplex.ReadMultilineAsync();
        Assert.Equal(1, vatp.FetchCount);

        await DispatchLineAsync(duplex, session, $"BODY {MessageId}");
        _ = await duplex.ReadMultilineAsync();
        Assert.Equal(2, vatp.FetchCount);
        Assert.Equal(2, rpc.LookupCount);
    }

    [Fact]
    public async Task ArticleWork_NotFound_Returns430_WithoutVatp()
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var vatp = new StubVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        Assert.Equal(0, vatp.FetchCount);
    }

    [Fact]
    public async Task Vatp_ConnectionFailure_Returns400()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, CacheUri);
        var vatp = new StubVatpArticleClient(VatpFetchResult.ConnectionFailure("down"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        await DispatchLineAsync(duplex, session, "DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vatp_RemoteFailure_Returns400()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, CacheUri);
        var vatp = new StubVatpArticleClient(VatpFetchResult.RemoteFailure("open", VatpErrorCode.OpenRejected, prepared.RequestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp);
        await DispatchLineAsync(duplex, session, $"BODY {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task Vatp_Incomplete_NeverServed_Returns400()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, CacheUri);
        var vatp = new StubVatpArticleClient(
            VatpFetchResult.IncompleteOrMalformed("truncated", VatpErrorCode.IncompleteTransfer, prepared.RequestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task ArticleId_Mismatch_Returns430()
    {
        var prepared = PrepareArticle(MessageId);
        var other = PrepareArticle("<other@example.test>");
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, CacheUri);
        // VATP returns a valid record whose ArtId/MID do not match the OPEN identity.
        var vatp = new StubVatpArticleClient(VatpFetchResult.FromSuccess(other.Record, prepared.RequestId, other.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task IngestQueueFull_StillServesArticle()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var queue = new RecordingIngestionQueue { NextResult = ArticleEnqueueResult.Full };
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, CacheUri);
        var vatp = new StubVatpArticleClient(VatpFetchResult.FromSuccess(prepared.Record, prepared.RequestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, ingestion: queue);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        var wire = Encoding.ASCII.GetString(await duplex.ReadMultilineAsync());
        Assert.StartsWith($"220 0 {MessageId}\r\n", wire, StringComparison.Ordinal);
        Assert.Empty(queue.Admitted);
        Assert.Equal(ArticleEnqueueResult.Full, queue.LastResult);
    }

    [Fact]
    public async Task SuccessfulAdmit_RemembersHistoryOnce()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var history = new RecordingHistoryDb();
        var queue = new RecordingIngestionQueue();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, CacheUri);
        var vatp = new StubVatpArticleClient(VatpFetchResult.FromSuccess(prepared.Record, prepared.RequestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, ingestion: queue, historyDb: history);
        await DispatchLineAsync(duplex, session, $"STAT {MessageId}");
        Assert.Equal($"223 0 {MessageId}", await duplex.ReadClientLineAsync());
        Assert.Equal(MessageId, Encoding.ASCII.GetString(Assert.Single(history.Remembered).Span));
    }

    [Fact]
    public async Task Cancellation_DuringVatp_Propagates()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, CacheUri);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vatp = new GatedVatpArticleClient(gate.Task);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp);
        using var cts = new CancellationTokenSource();
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
        await vatp.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        gate.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.True(vatp.SawCancellation);
    }

    private static Prepared PrepareArticle(string messageId, string body = "body\r\n")
    {
        var parser = new NntpArticleParser("nntpd.test");
        var created = ArticleRecordFactory.TryCreate(
            parser,
            Encoding.ASCII.GetBytes(CanonicalArticleText.Destuffed(messageId, body)));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return new Prepared(Guid.NewGuid(), created.Record);
    }

    private static async Task DispatchLineAsync(
        ArticleDuplex duplex,
        NntpSession session,
        string command,
        CancellationToken cancellationToken = default)
    {
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, command, cancellationToken);
    }

    private readonly record struct Prepared(Guid RequestId, ArticleRecord Record);

    private sealed class StubArticleWorkRpcClient(
        ArticleWorkOutcome outcome,
        ArticleId? articleId,
        string? uri) : IArticleWorkRpcClient
    {
        public int LookupCount { get; private set; }

        public Task<ArticleWorkRpcResult> LookupByMessageIdAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken)
        {
            LookupCount++;
            var mid = Encoding.ASCII.GetString(messageId.Span);
            return Task.FromResult(new ArticleWorkRpcResult(
                outcome,
                Guid.NewGuid(),
                mid,
                Backbone: outcome == ArticleWorkOutcome.Success ? "Storage" : null,
                Uri: uri,
                ArticleId: articleId,
                Error: outcome == ArticleWorkOutcome.Success ? null : "missing",
                SourceExchange: outcome == ArticleWorkOutcome.Success ? "cache.requests" : null));
        }
    }

    private sealed class StubVatpArticleClient(VatpFetchResult result) : IVatpArticleClient
    {
        public int FetchCount { get; private set; }

        public Task<VatpFetchResult> FetchArticleAsync(
            string cacheUri,
            Guid requestId,
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            FetchCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class GatedVatpArticleClient(Task release) : IVatpArticleClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SawCancellation { get; private set; }

        public async Task<VatpFetchResult> FetchArticleAsync(
            string cacheUri,
            Guid requestId,
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await release.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }

            return VatpFetchResult.ConnectionFailure("unexpected");
        }
    }

    private sealed class RecordingIngestionQueue : IArticleIngestionQueue
    {
        public List<InboundArticle> Admitted { get; } = [];

        public ArticleEnqueueResult NextResult { get; set; } = ArticleEnqueueResult.Accepted;

        public ArticleEnqueueResult LastResult { get; private set; }

        public long MemoryLimitBytes => 64 * 1024 * 1024;

        public long QueuedBytes => 0;

        public long PeakQueuedBytes => 0;

        public int MaxArticleBytes => 4 * 1024 * 1024;

        public int Count => Admitted.Count;

        public int PeakCount => Admitted.Count;

        public bool IsAccepting => true;

        public ArticleEnqueueResult TryAdmit(InboundArticle article)
        {
            LastResult = NextResult;
            if (NextResult == ArticleEnqueueResult.Accepted)
            {
                Admitted.Add(article);
            }

            return NextResult;
        }

        public bool TryEnqueue(InboundArticle article) => TryAdmit(article) == ArticleEnqueueResult.Accepted;

        public ValueTask<ArticleEnqueueResult> EnqueueAsync(
            InboundArticle article,
            CancellationToken cancellationToken) =>
            new(TryAdmit(article));

        public bool TryProbeCapacity() => true;

        public void Complete()
        {
        }

        public ValueTask<InboundArticle?> DequeueAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<InboundArticle?>(null);
    }

    private sealed class RecordingHistoryDb : IHistoryDb
    {
        public List<ReadOnlyMemory<byte>> Remembered { get; } = [];

        public ValueTask<HistoryLookupResult> LookupAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            new(HistoryLookupResult.Unseen);

        public ValueTask<HistoryLookupResult> PeekAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken = default) =>
            new(HistoryLookupResult.Unseen);

        public void Remember(ReadOnlyMemory<byte> messageId) => Remembered.Add(messageId.ToArray());

        public bool ContainsLocal(in HistoryDigest digest) => false;
    }

    private sealed class ArticleDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<ArticleDuplex> CreateAsync() => Task.FromResult(new ArticleDuplex());

        public NntpSession CreateSession(
            IArticleWorkRpcClient? articleWorkRpc = null,
            IVatpArticleClient? vatp = null,
            IArticleIngestionQueue? ingestion = null,
            IHistoryDb? historyDb = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            var session = new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: ingestion,
                historyDb: historyDb,
                articleWorkRpc: articleWorkRpc,
                vatpArticleClient: vatp);
            session.SetAuthorization(Reader);
            return session;
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async Task<byte[]> ReadMultilineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            while (true)
            {
                var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
                Assert.NotNull(line);
                var ascii = Encoding.ASCII.GetBytes(line + "\r\n");
                ascii.CopyTo(buffer.GetSpan(ascii.Length));
                buffer.Advance(ascii.Length);
                if (line == ".")
                {
                    return buffer.WrittenSpan.ToArray();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }
    }

    private sealed class PipeNntpConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();

        public PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
        {
            Input = input;
            Output = output;
            ClientIdentity = identity;
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }

        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

        public System.Net.EndPoint? LocalEndPoint => null;

        public ConnectionClientIdentity ClientIdentity { get; }

        public bool IsTls => false;

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public bool IsCompressed => false;

        public CancellationToken ConnectionClosed => _cts.Token;

        public bool IsCompleted => _cts.IsCancellationRequested;

        public long OutboundIdleVersion => 0;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CompleteAsync(Exception? exception = null)
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }

        public Task UpgradeToTlsAsync(
            VectorNNTP.NNTPD.Networking.Certificates.ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
