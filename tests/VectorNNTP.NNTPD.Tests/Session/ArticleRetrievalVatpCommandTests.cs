using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Messaging.Cache;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.History;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.RabbitMq;
using VectorNNTP.NNTPD.RabbitMq.ArticleWork;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Storage;
using VectorNNTP.NNTPD.Session.CommandProcessor;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Tests.RabbitMq;
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

    [Fact]
    public async Task StorageHit_OneCandidate_StartsFetchWithoutWaitingForAlternate()
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        var alternates = new ManualAlternates();
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId) with { Alternates = alternates });
        var vatp = new RecordingVatpArticleClient(
            VatpFetchResult.FromSuccess(prepared.Record, requestId, prepared.Record.ArtId));
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.StartsWith($"220 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(requestId, vatp.RequestId);
        Assert.Equal(0, alternates.WaitCount);
    }

    [Theory]
    [InlineData("ARTICLE", "220")]
    [InlineData("HEAD", "221")]
    [InlineData("BODY", "222")]
    [InlineData("STAT", "223")]
    public async Task StorageFailover_PreDataFailure_UsesSecondCandidate(string verb, string code)
    {
        var prepared = PrepareArticle(MessageId, body: "hello\r\n");
        var requestId = Guid.NewGuid();
        var firstUri = StorageCacheUri(prepared.Record.ArtId);
        var secondUri = StorageHostUri(prepared.Record.ArtId, "cache02.usenet.ninja", 564);
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(2, "cache02.usenet.ninja", secondUri));
        await using var duplex = await ArticleDuplex.CreateAsync();
        var queue = new RecordingIngestionQueue();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId, firstUri) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
        [
            VatpFetchResult.ConnectionFailure("down", requestId, prepared.Record.ArtId, 0),
            VatpFetchResult.FromSuccess(prepared.Record, requestId, prepared.Record.ArtId),
        ]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, ingestion: queue, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"{verb} {MessageId}");
        if (verb == "STAT")
        {
            Assert.Equal($"{code} 0 {MessageId}", await duplex.ReadClientLineAsync());
        }
        else
        {
            Assert.StartsWith($"{code} 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
        }

        Assert.Equal(1, rpc.LookupCount);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(2, vatp.FetchCount);
        Assert.Equal([firstUri, secondUri], vatp.Uris);
        Assert.All(vatp.RequestIds, id => Assert.Equal(requestId, id));
        Assert.Equal(InboundArticleProducer.BackFiller, Assert.Single(queue.Admitted).Producer);
    }

    [Fact]
    public async Task StorageFailover_OpenRejectedBeforeData_UsesSecondCandidate()
    {
        await AssertSecondCandidateUsedAsync(
            VatpFetchResult.RemoteFailure("open", VatpErrorCode.OpenRejected, Guid.Empty, default, 0));
    }

    [Fact]
    public async Task StorageFailover_ProtocolFailureBeforeData_UsesSecondCandidate()
    {
        await AssertSecondCandidateUsedAsync(VatpFetchResult.ProtocolFailure("local", Guid.Empty, null, 0));
    }

    [Fact]
    public async Task StorageFailover_ConnectionFailureAfterData_DoesNotUseSecondCandidate()
    {
        await AssertNoSecondCandidateAsync(
            VatpFetchResult.ConnectionFailure("reset", Guid.Empty, default, 1024),
            "400 Service temporarily unavailable");
    }

    [Fact]
    public async Task StorageFailover_RemoteFailureAfterData_DoesNotUseSecondCandidate()
    {
        await AssertNoSecondCandidateAsync(
            VatpFetchResult.RemoteFailure("fail", VatpErrorCode.OpenRejected, Guid.Empty, default, 64),
            "400 Service temporarily unavailable");
    }

    [Fact]
    public async Task StorageFailover_IncompleteBeforeData_DoesNotUseSecondCandidate()
    {
        await AssertNoSecondCandidateAsync(
            VatpFetchResult.IncompleteOrMalformed("meta", VatpErrorCode.InvalidMeta, Guid.Empty, default, 0),
            "400 Service temporarily unavailable");
    }

    [Fact]
    public async Task StorageFailover_IncompleteAfterData_DoesNotUseSecondCandidate()
    {
        await AssertNoSecondCandidateAsync(
            VatpFetchResult.IncompleteOrMalformed("truncated", VatpErrorCode.ArtSizeMismatch, Guid.Empty, default, 32),
            "400 Service temporarily unavailable");
    }

    [Fact]
    public async Task StorageFailover_IdentityMismatch_Returns430_WithoutSecondCandidate()
    {
        var prepared = PrepareArticle(MessageId);
        var other = PrepareArticle("<other@example.test>");
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(2, "cache02.usenet.ninja", StorageHostUri(prepared.Record.ArtId, "cache02.usenet.ninja", 564)));
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [VatpFetchResult.FromSuccess(other.Record, Guid.NewGuid(), other.Record.ArtId)]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(0, alternates.WaitCount);
    }

    [Fact]
    public async Task StorageFailover_EligibleWithoutAlternate_PreservesFirstFailure()
    {
        var prepared = PrepareArticle(MessageId);
        var alternates = new ManualAlternates();
        alternates.Publish(null);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [VatpFetchResult.ConnectionFailure("down", Guid.Empty, default, 0)]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(1, alternates.WaitCount);
    }

    [Fact]
    public async Task StorageFailover_SecondCandidateFails_DoesNotAttemptThird()
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(
            2,
            "cache02.usenet.ninja",
            StorageHostUri(prepared.Record.ArtId, "cache02.usenet.ninja", 564)));
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
        [
            VatpFetchResult.ConnectionFailure("down", requestId, prepared.Record.ArtId, 0),
            VatpFetchResult.RemoteFailure("open", VatpErrorCode.OpenRejected, requestId, prepared.Record.ArtId, 0),
        ]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(2, vatp.FetchCount);
        Assert.Equal(requestId, vatp.RequestIds[0]);
        Assert.Equal(requestId, vatp.RequestIds[1]);
    }

    [Fact]
    public async Task StorageFailover_AlternateArrivesDuringFirstAttempt()
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        var secondUri = StorageHostUri(prepared.Record.ArtId, "cache02.usenet.ninja", 564);
        var alternates = new ManualAlternates();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [
                VatpFetchResult.ConnectionFailure("down", requestId, prepared.Record.ArtId, 0),
                VatpFetchResult.FromSuccess(prepared.Record, requestId, prepared.Record.ArtId),
            ],
            entered,
            release.Task);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, vatp.FetchCount);
        alternates.Publish(new StorageArticleCandidate(2, "cache02.usenet.ninja", secondUri));
        release.TrySetResult();
        await dispatch;
        Assert.StartsWith($"220 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(2, vatp.FetchCount);
        Assert.Equal(secondUri, vatp.Uris[1]);
    }

    [Fact]
    public async Task StorageFailover_CancellationDuringFirstAttempt_DoesNotUseSecond()
    {
        var prepared = PrepareArticle(MessageId);
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(
            2,
            "cache02.usenet.ninja",
            StorageHostUri(prepared.Record.ArtId, "cache02.usenet.ninja", 564)));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [VatpFetchResult.ConnectionFailure("down", Guid.Empty, default, 0)],
            entered,
            release.Task);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        using var cts = new CancellationTokenSource();
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(0, alternates.WaitCount);
        Assert.Null(await duplex.TryReadClientLineAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task StorageFailover_CancellationWhileWaitingForAlternate_Propagates()
    {
        var prepared = PrepareArticle(MessageId);
        var alternates = new ManualAlternates();
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [VatpFetchResult.ConnectionFailure("down", Guid.Empty, default, 0)]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        using var cts = new CancellationTokenSource();
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
        await alternates.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Null(await duplex.TryReadClientLineAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task StorageFailover_CancellationBeforeAlternateDecision_DoesNotFetchSecond()
    {
        var prepared = PrepareArticle(MessageId);
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(
            2,
            "cache02.usenet.ninja",
            StorageHostUri(prepared.Record.ArtId, "cache02.usenet.ninja", 564)));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [
                VatpFetchResult.ConnectionFailure("down", Guid.Empty, default, 0),
                VatpFetchResult.FromSuccess(prepared.Record, Guid.NewGuid(), prepared.Record.ArtId),
            ],
            entered,
            release.Task,
            returnDespiteCancellation: true);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        using var cts = new CancellationTokenSource();
        var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(0, alternates.WaitCount);
        Assert.Null(await duplex.TryReadClientLineAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task StorageFailover_CancellationClosesOpenLookupWindow_DoesNotBecome400()
    {
        var factory = new FakeRabbitMqConnectionFactory();
        await using var rabbit = CreateStorageLookupRabbit(factory);
        var time = new FakeTimeProvider();
        var service = new StorageArticleLookupService(
            rabbit,
            Options.Create(CreateStorageLookupOptions()),
            NullLogger<StorageArticleLookupService>.Instance,
            time);
        var window = new CancelClosedLookupWindow(service);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ReleaseProductionWait = release.Task;
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var vatp = new SequenceVatpArticleClient(
            [VatpFetchResult.ConnectionFailure("down", Guid.Empty, default, 0)]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: window);
        await rabbit.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        try
        {
            var dispatch = DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}", cts.Token);
            var publication = await WaitForStoragePublicationAsync(factory);
            Assert.True(StorageArticleLookupWireProtocol.TryParseRequestV1(publication.Body, out var request, out _));
            var consume = factory.LastConnection!.RpcChannels.First(static channel => channel.ConsumedQueue is not null);
            await consume.DeliverAsync(
                publication.CorrelationId,
                StorageArticleLookupWireProtocol.SerializeResponseV1(
                    CreateStorageLookupResponse(request!, 1, "cache01.usenet.ninja")));

            await window.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, service.OutstandingCorrelations);
            Assert.Equal(1, vatp.FetchCount);
            await cts.CancelAsync();
            await window.WindowClosed!.WaitAsync(TimeSpan.FromSeconds(2));
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
            Assert.Equal(1, vatp.FetchCount);
            Assert.Null(await duplex.TryReadClientLineAsync(TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StorageFailover_ConcurrentCommands_DoNotShareCandidates()
    {
        var prepared = PrepareArticle(MessageId);
        var requestA = Guid.NewGuid();
        var requestB = Guid.NewGuid();
        var uriA1 = StorageHostUri(prepared.Record.ArtId, "cache01.usenet.ninja", 563);
        var uriA2 = StorageHostUri(prepared.Record.ArtId, "cache02.usenet.ninja", 564);
        var uriB1 = StorageHostUri(prepared.Record.ArtId, "cache09.usenet.ninja", 569);
        var uriB2 = StorageHostUri(prepared.Record.ArtId, "cache03.usenet.ninja", 565);
        var alternatesA = new ManualAlternates();
        var alternatesB = new ManualAlternates();
        alternatesA.Publish(new StorageArticleCandidate(2, "cache02.usenet.ninja", uriA2));
        alternatesB.Publish(new StorageArticleCandidate(3, "cache03.usenet.ninja", uriB2));
        await using var firstDuplex = await ArticleDuplex.CreateAsync();
        await using var secondDuplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new PairedStorageLookup(
            StorageFound(prepared.Record.ArtId, requestA, uriA1) with { Alternates = alternatesA },
            StorageFound(prepared.Record.ArtId, requestB, uriB1) with { Alternates = alternatesB });
        var vatp = new SequenceVatpArticleClient(
        [
            VatpFetchResult.ConnectionFailure("a", requestA, prepared.Record.ArtId, 0),
            VatpFetchResult.FromSuccess(prepared.Record, requestA, prepared.Record.ArtId),
            VatpFetchResult.ProtocolFailure("b", requestB, prepared.Record.ArtId, 0),
            VatpFetchResult.FromSuccess(prepared.Record, requestB, prepared.Record.ArtId),
        ]);
        var first = firstDuplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        var second = secondDuplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(firstDuplex, first, $"ARTICLE {MessageId}");
        await DispatchLineAsync(secondDuplex, second, $"ARTICLE {MessageId}");
        Assert.StartsWith($"220 0 {MessageId}\r\n", Encoding.ASCII.GetString(await firstDuplex.ReadMultilineAsync()), StringComparison.Ordinal);
        Assert.StartsWith($"220 0 {MessageId}\r\n", Encoding.ASCII.GetString(await secondDuplex.ReadMultilineAsync()), StringComparison.Ordinal);
        Assert.Equal(2, lookup.LookupCount);
        Assert.Equal([uriA1, uriA2, uriB1, uriB2], vatp.Uris);
        Assert.Equal([requestA, requestA, requestB, requestB], vatp.RequestIds);
    }

    private static async Task AssertSecondCandidateUsedAsync(VatpFetchResult firstFailure)
    {
        var prepared = PrepareArticle(MessageId);
        var requestId = Guid.NewGuid();
        var secondUri = StorageHostUri(prepared.Record.ArtId, "cache02.usenet.ninja", 564);
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(2, "cache02.usenet.ninja", secondUri));
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, requestId) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient(
            [firstFailure, VatpFetchResult.FromSuccess(prepared.Record, requestId, prepared.Record.ArtId)]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.StartsWith($"220 0 {MessageId}\r\n", Encoding.ASCII.GetString(await duplex.ReadMultilineAsync()), StringComparison.Ordinal);
        Assert.Equal(1, lookup.LookupCount);
        Assert.Equal(2, vatp.FetchCount);
        Assert.Equal(secondUri, vatp.Uris[1]);
        Assert.All(vatp.RequestIds, id => Assert.Equal(requestId, id));
    }

    private static async Task AssertNoSecondCandidateAsync(VatpFetchResult firstFailure, string expectedLine)
    {
        var prepared = PrepareArticle(MessageId);
        var alternates = new ManualAlternates();
        alternates.Publish(new StorageArticleCandidate(
            2,
            "cache02.usenet.ninja",
            StorageHostUri(prepared.Record.ArtId, "cache02.usenet.ninja", 564)));
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new StubArticleWorkRpcClient(ArticleWorkOutcome.ArticleNotFound, articleId: null, uri: null);
        var lookup = new StubStorageLookup(StorageFound(prepared.Record.ArtId, Guid.NewGuid()) with { Alternates = alternates });
        var vatp = new SequenceVatpArticleClient([firstFailure]);
        var session = duplex.CreateSession(articleWorkRpc: rpc, vatp: vatp, storageLookup: lookup);
        await DispatchLineAsync(duplex, session, $"ARTICLE {MessageId}");
        Assert.Equal(expectedLine, await duplex.ReadClientLineAsync());
        Assert.Equal(1, vatp.FetchCount);
        Assert.Equal(0, alternates.WaitCount);
    }

    private static string StorageHostUri(ArticleId articleId, string host, int port) =>
        $"cache://{host}:{port}/{articleId.ToLowerHexString()}";

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

    private sealed class ManualAlternates : IStorageArticleAlternateSource
    {
        private readonly TaskCompletionSource<StorageArticleCandidate?> _next =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WaitCount { get; private set; }

        public Task WhenClosed => Task.CompletedTask;

        public void Publish(StorageArticleCandidate? candidate) => _next.TrySetResult(candidate);

        public async ValueTask<StorageArticleCandidate?> WaitForAlternateAsync(CancellationToken cancellationToken)
        {
            WaitCount++;
            Entered.TrySetResult();
            return await _next.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class SequenceVatpArticleClient : IVatpArticleClient
    {
        private readonly Queue<VatpFetchResult> _results;
        private readonly TaskCompletionSource? _firstEntered;
        private readonly Task? _firstRelease;

        private readonly bool _returnDespiteCancellation;

        public SequenceVatpArticleClient(
            IEnumerable<VatpFetchResult> results,
            TaskCompletionSource? firstEntered = null,
            Task? firstRelease = null,
            bool returnDespiteCancellation = false)
        {
            _results = new Queue<VatpFetchResult>(results);
            _firstEntered = firstEntered;
            _firstRelease = firstRelease;
            _returnDespiteCancellation = returnDespiteCancellation;
        }

        public List<string> Uris { get; } = [];

        public List<Guid> RequestIds { get; } = [];

        public int FetchCount => Uris.Count;

        public async Task<VatpFetchResult> FetchArticleAsync(
            string cacheUri,
            Guid requestId,
            ArticleId articleId,
            CancellationToken cancellationToken)
        {
            Uris.Add(cacheUri);
            RequestIds.Add(requestId);
            if (Uris.Count == 1 && _firstEntered is not null)
            {
                _firstEntered.TrySetResult();
                if (_firstRelease is not null)
                {
                    if (_returnDespiteCancellation)
                    {
                        await _firstRelease.ConfigureAwait(false);
                    }
                    else
                    {
                        await _firstRelease.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (!_returnDespiteCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return _results.Dequeue();
        }
    }

    /// <summary>
    /// Production lookup whose alternate wait is held until the test has cancelled.
    /// The inner wait then observes the window that cancellation already closed and
    /// returns null instead of throwing, which is the MapFetch-to-400 race.
    /// </summary>
    private sealed class CancelClosedLookupWindow(StorageArticleLookupService service) : IStorageArticleLookupClient
    {
        private WindowClosedAlternate? _alternate;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? ReleaseProductionWait { get; set; }

        public Task? WindowClosed => _alternate?.WhenClosed;

        public async Task<StorageArticleLookupResult> LookupAsync(ArticleId articleId, CancellationToken cancellationToken)
        {
            var result = await service.LookupAsync(articleId, cancellationToken).ConfigureAwait(false);
            if (result.Alternates is null)
            {
                return result;
            }

            _alternate = new WindowClosedAlternate(result.Alternates, Entered, ReleaseProductionWait!);
            return result with { Alternates = _alternate };
        }

        private sealed class WindowClosedAlternate(
            IStorageArticleAlternateSource inner,
            TaskCompletionSource entered,
            Task release) : IStorageArticleAlternateSource
        {
            public Task WhenClosed => inner.WhenClosed;

            public async ValueTask<StorageArticleCandidate?> WaitForAlternateAsync(CancellationToken cancellationToken)
            {
                entered.TrySetResult();
                await release.ConfigureAwait(false);
                return await inner.WaitForAlternateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static RabbitMqService CreateStorageLookupRabbit(FakeRabbitMqConnectionFactory factory)
    {
        var options = RabbitMqOptionsTests.CreateValid();
        options.PoolReconnectBaseDelayMs = 50;
        options.PoolReconnectMaxDelayMs = 50;
        return new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTP.NNTPD:nntpd01.usenet.ninja"),
            NullLogger<RabbitMqService>.Instance);
    }

    private static NntpdOptions CreateStorageLookupOptions()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = 1;
        return options;
    }

    private static StorageArticleLookupResponse CreateStorageLookupResponse(
        StorageArticleLookupRequest request,
        int serverId,
        string fqdn) =>
        new(
            1,
            request.RequestId,
            serverId,
            fqdn,
            request.ArticleId,
            StorageArticleLookupWireProtocol.BuildCacheUri(fqdn, 1191, request.ArticleId));

    private static async Task<FakeRabbitMqRpcPublication> WaitForStoragePublicationAsync(FakeRabbitMqConnectionFactory factory)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var connection = factory.LastConnection;
            if (connection is not null)
            {
                var publication = connection.RpcChannels
                    .SelectMany(static channel => channel.Publications)
                    .FirstOrDefault(static candidate => candidate.Exchange == CacheFleetTopology.RequestsExchangeName);
                if (publication is not null)
                {
                    return publication;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Expected a cache.requests publication.");
    }

    private sealed class PairedStorageLookup(StorageArticleLookupResult first, StorageArticleLookupResult second)
        : IStorageArticleLookupClient
    {
        private readonly Queue<StorageArticleLookupResult> _results = new([first, second]);

        public int LookupCount { get; private set; }

        public Task<StorageArticleLookupResult> LookupAsync(ArticleId articleId, CancellationToken cancellationToken)
        {
            LookupCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_results.Dequeue());
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
