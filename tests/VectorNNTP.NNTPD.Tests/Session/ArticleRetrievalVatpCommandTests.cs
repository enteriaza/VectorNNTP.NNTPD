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
using VectorNNTP.NNTPD.Storage;
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

    [Fact]
    public async Task ArticleWork_Success_DoesNotCallStorageLookup()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.Success, prepared.Record.ArtId, CacheUri);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()));
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.FromSuccess(prepared.Record, prepared.RequestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        var wire = Encoding.ASCII.GetString(await duplex.ReadMultilineAsync());
        Assert.StartsWith($"220 0 {MessageId}\r\n", wire, StringComparison.Ordinal);
        Assert.Equal(0, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(CacheUri, vatp.Uri);
    }

    [Theory]
    [InlineData("ARTICLE", ArticleWorkOutcome.ArticleNotFound)]
    [InlineData("HEAD", ArticleWorkOutcome.ArticleNotFound)]
    [InlineData("BODY", ArticleWorkOutcome.ArticleNotFound)]
    [InlineData("STAT", ArticleWorkOutcome.ArticleNotFound)]
    [InlineData("ARTICLE", ArticleWorkOutcome.InvalidArticle)]
    public async Task ArticleWorkMiss_StorageHit_ServesAndAdmits(string verb, ArticleWorkOutcome outcome)
    {
        var prepared = PrepareArticle(MessageId, body: "hello\r\n");
        var requestId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var uri = StorageCacheUri(prepared.Record.ArtId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var queue = new RecordingIngestionQueue();
        var rpc = new StubArticleWorkRpcClient(outcome, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId, uri));
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.FromSuccess(prepared.Record, requestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(
            articleWorkRpc: rpc,
            vatp: vatp,
            ingestion: queue,
            storageLookup: lookup);

        await DispatchLineAsync(duplex, session, $"{verb} {MessageId}");

        switch (verb)
        {
            case "ARTICLE":
                Assert.StartsWith($"220 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
                break;
            case "HEAD":
                Assert.StartsWith($"221 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
                break;
            case "BODY":
                Assert.StartsWith($"222 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
                break;
            default:
                Assert.Equal($"223 0 {MessageId}", await duplex.ReadClientLineAsync());
                break;
        }

        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(prepared.Record.ArtId, lookup.LastArticleId);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(uri, vatp.Uri);
        Assert.Equal(requestId, vatp.RequestId);
        Assert.Equal(prepared.Record.ArtId, vatp.ArticleId);
        Assert.Equal(InboundArticleProducer.BackFiller, Assert.Single(queue.Admitted).Producer);
    }

    [Fact]
    public async Task ArticleWorkMiss_StorageSilence_Returns430_WithoutVatp()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageSilence(prepared.Record.ArtId));
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
    }

    [Theory]
    [InlineData("Storage article lookup was canceled.")]
    [InlineData("Lookup could not be registered.")]
    public async Task ArticleWorkMiss_StorageUnavailable_Returns400_WithoutVatp(string error)
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageNotFound(prepared.Record.ArtId, error));
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
    }

    [Fact]
    public async Task ArticleWorkMiss_StorageThrows_Returns400_WithoutVatpOrRetry()
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new ThrowingStorageLookup();
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
    }

    [Fact]
    public async Task ArticleWorkThrows_DoesNotCallStorageLookup()
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new ThrowingArticleWorkRpcClient();
        var lookup = new ThrowingStorageLookup();
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(0, lookup.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
    }

    [Fact]
    public async Task Cancellation_DuringStorageLookup_PropagatesWithoutReply()
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookup = new GatedStorageLookup(gate.Task);
        var vatp = new RecordingVatpArticleClient(VatpFetchResult.ConnectionFailure("unused"));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        using var cts = new CancellationTokenSource();
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
        await lookup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        gate.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.True(lookup.SawCancellation);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(0, vatp.FetchCount);
        Assert.Null(await duplex.TryReadClientLineAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task StorageHit_OpenRejected_Returns400_WithoutRetry()
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId));
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.RemoteFailure("open", VatpErrorCode.OpenRejected, requestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(requestId, vatp.RequestId);
    }

    [Fact]
    public async Task StorageHit_IdentityMismatch_Returns430_WithoutRetry()
    {
        var prepared = PrepareArticle(MessageId);
        var other = PrepareArticle("<other@example.test>");
        var requestId = Guid.NewGuid();
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId));
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.FromSuccess(other.Record, requestId, other.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
    }

    [Fact]
    public async Task StorageHit_IncompleteTransfer_Returns400_WithoutRetry()
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId));
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.IncompleteOrMalformed(
                "truncated",
                VatpErrorCode.IncompleteTransfer,
                requestId,
                prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
    }

    [Fact]
    public async Task StorageHit_CancellationDuringVatp_PropagatesWithoutReply()
    {
        var prepared = PrepareArticle(MessageId);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vatp = new GatedVatpArticleClient(gate.Task);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        using var cts = new CancellationTokenSource();
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
        await vatp.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        gate.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.True(vatp.SawCancellation);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, rpc.LookupCount);
        Assert.Null(await duplex.TryReadClientLineAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task TwoSessions_SameMessageId_LookupIndependently()
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        await using var firstDuplex = await ArticleDuplex.CreateAsync();
        await using var secondDuplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId));
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.FromSuccess(prepared.Record, requestId, prepared.Record.ArtId));
        var first = firstDuplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        var second = secondDuplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);

        await DispatchLineAsync(firstDuplex, first, $"ARTICLE {MessageId}");
        await DispatchLineAsync(secondDuplex, second, $"ARTICLE {MessageId}");

        Assert.StartsWith(
            $"220 0 {MessageId}\r\n",
            Encoding.ASCII.GetString(await firstDuplex.ReadMultilineAsync()),
            StringComparison.Ordinal);
        Assert.StartsWith(
            $"220 0 {MessageId}\r\n",
            Encoding.ASCII.GetString(await secondDuplex.ReadMultilineAsync()),
            StringComparison.Ordinal);
        Assert.Equal(2, rpc.LookupCount);
        Assert.Equal(2, lookup.LookupCount);
        Assert.Equal(2, vatp.FetchCount);
    }

    private static string StorageCacheUri(ArticleId articleId) =>
        $"cache://cache01.usenet.ninja:563/{articleId.ToLowerHexString()}";

    private static StorageArticleLookupResult StorageFound(ArticleId articleId, Guid requestId, string? uri = null) =>
        new(
            StorageArticleLookupOutcome.Found,
            requestId,
            articleId,
            1,
            "cache01.usenet.ninja",
            uri ?? StorageCacheUri(articleId),
            Error: null);

    private static StorageArticleLookupResult StorageSilence(ArticleId articleId) =>
        StorageNotFound(articleId, "Storage article lookup timed out with no positive response.");

    private static StorageArticleLookupResult StorageNotFound(ArticleId articleId, string error) =>
        new(
            StorageArticleLookupOutcome.NotFound,
            Guid.NewGuid(),
            articleId,
            null,
            null,
            null,
            error);

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

    private sealed class RecordingVatpArticleClient(VatpFetchResult result) : IVatpArticleClient
    {
        public int FetchCount { get; private set; }

        public string? Uri { get; private set; }

        public Guid RequestId { get; private set; }

        public ArticleId ArticleId { get; private set; }

        public Task<VatpFetchResult> FetchArticleAsync(
            string cacheUri,
            Guid requestId,
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            FetchCount++;
            Uri = cacheUri;
            RequestId = requestId;
            ArticleId = articleId;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingArticleWorkRpcClient : IArticleWorkRpcClient
    {
        public int LookupCount { get; private set; }

        public Task<ArticleWorkRpcResult> LookupByMessageIdAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken)
        {
            LookupCount++;
            throw new InvalidOperationException("article-work unavailable");
        }
    }

    private sealed class StubStorageLookup(StorageArticleLookupResult result) : IStorageArticleLookupClient
    {
        public int LookupCount { get; private set; }

        public ArticleId LastArticleId { get; private set; }

        public Task<StorageArticleLookupResult> LookupAsync(ArticleId articleId, CancellationToken cancellationToken)
        {
            LookupCount++;
            LastArticleId = articleId;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingStorageLookup : IStorageArticleLookupClient
    {
        public int LookupCount { get; private set; }

        public Task<StorageArticleLookupResult> LookupAsync(ArticleId articleId, CancellationToken cancellationToken)
        {
            LookupCount++;
            throw new InvalidOperationException("storage lookup unavailable");
        }
    }

    private sealed class GatedStorageLookup(Task release) : IStorageArticleLookupClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LookupCount { get; private set; }

        public bool SawCancellation { get; private set; }

        public async Task<StorageArticleLookupResult> LookupAsync(ArticleId articleId, CancellationToken cancellationToken)
        {
            LookupCount++;
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

            return StorageArticleLookupResult.NotFound(Guid.NewGuid(), articleId, "unexpected");
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
            IHistoryDb? historyDb = null,
            IStorageArticleLookupClient? storageLookup = null)
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
                vatpArticleClient: vatp,
                storageArticleLookup: storageLookup);
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

        public async Task<string?> TryReadClientLineAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
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
