using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.NNTPD.Transport.Vatp;

namespace VectorNNTP.NNTPD.Tests.Transport.Vatp;

public sealed class VatpArticleClientTests
{
    [Fact]
    public async Task Coalesced_multi_frame_transfer_succeeds()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.CoalesceOutboundFrames;
        var prepared = CreateArticle("<coalesce-multi@example.test>", body: BuildLargeBody(96 * 1024));
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);

        var result = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.Equal(VatpFetchKind.Success, result.Kind);
        Assert.Equal(prepared.Record.ArtId, result.Record.ArtId);
        Assert.Equal(prepared.Record.ArtSize, result.Record.ArtSize);
        Assert.Equal(prepared.Record.ArtHash, result.Record.ArtHash);
    }

    [Fact]
    public async Task Coalesced_240KiB_article_matches_live_failure_scale()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.CoalesceOutboundFrames;
        var prepared = CreateArticle("<coalesce-240k@example.test>", body: BuildLargeBody(240 * 1024));
        Assert.True(prepared.Record.ArtSize > VatpProtocol.DefaultMaxFramePayload);
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);

        var result = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.Equal(VatpFetchKind.Success, result.Kind);
        Assert.Equal(prepared.Record.ArtId, result.Record.ArtId);
        Assert.Equal(prepared.Record.ArtSize, result.Record.ArtSize);
        Assert.Equal(prepared.Record.ArtHash, result.Record.ArtHash);
        Assert.True(result.Record.ArtData.ToArray().AsSpan()
            .SequenceEqual(prepared.Record.ArtData.ToArray()));
    }

    [Fact]
    public async Task Tiny_writes_reconstruct_partial_frames_across_reads()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.TinyOutboundWrites;
        var prepared = CreateArticle("<tiny-writes@example.test>", body: BuildLargeBody(8 * 1024));
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);

        var result = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.Equal(VatpFetchKind.Success, result.Kind);
        Assert.Equal(prepared.Record.ArtId, result.Record.ArtId);
        Assert.Equal(prepared.Record.ArtSize, result.Record.ArtSize);
    }

    [Fact]
    public async Task Max_sized_single_data_frame_assembles_across_32KiB_reads()
    {
        // Live root cause: assembling one DefaultMaxFramePayload DATA across multiple
        // 32 KiB socket reads must not trip the old one-frame accumulation guard.
        await using var server = VatpLoopbackTestServer.Start();
        var bodyBytes = (int)VatpProtocol.DefaultMaxFramePayload - 256;
        var prepared = CreateArticle("<max-frame@example.test>", body: BuildLargeBody(bodyBytes));
        Assert.True(prepared.Record.ArtSize > 32 * 1024);
        Assert.True(prepared.Record.ArtSize <= VatpProtocol.DefaultMaxFramePayload);
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);

        var result = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.Equal(VatpFetchKind.Success, result.Kind);
        Assert.Equal(prepared.Record.ArtSize, result.Record.ArtSize);
        Assert.Equal(prepared.Record.ArtHash, result.Record.ArtHash);
    }

    [Fact]
    public async Task Oversized_data_frame_still_rejected()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.OversizedDataFrame;
        var prepared = CreateArticle("<oversized-frame@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);

        var result = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
        Assert.NotEqual(VatpFetchKind.Success, result.Kind);
        Assert.Equal(default, result.Record);
    }

    [Fact]
    public async Task Hello_and_single_transfer_success()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var prepared = CreateArticle("<vatp-success@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);
        var result = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.Success, result.Kind);
        Assert.Equal(prepared.Record.ArtId, result.Record.ArtId);
    }

    [Fact]
    public void Fin_without_end_does_not_complete_on_receive_stream()
    {
        var prepared = CreateArticle("<fin-only@example.test>");
        var stream = new ArticleTransferReceiveStream(1, prepared.RequestId, prepared.Record.ArtId);
        var record = prepared.Record;
        var meta = ArticleCanonicalTransferMeta.FromRecord(in record, prepared.SelectedDateHeaderName);
        var metaBytes = VatpMetaCodec.Encode(in meta);
        Assert.True(stream.TryAcceptMeta(metaBytes).Success);
        var artData = prepared.Record.ArtData.ToArray();
        Assert.True(stream.TryAcceptData(artData, fin: true).Success);
        Assert.Equal(ArticleTransferPhase.AwaitingEnd, stream.Phase);
        Assert.False(stream.HasConsumableRecord);
        Assert.False(stream.TryTakeRecord(out _));
    }

    [Fact]
    public async Task Wrong_article_id_open_yields_remote_failure()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var prepared = CreateArticle("<registered@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var wrongId = ArticleId.FromMessageId("<other@example.test>"u8);
        var client = CreateClient(server);
        var result = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            wrongId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.RemoteTransferFailure, result.Kind);
        Assert.Equal(VatpErrorCode.OpenRejected, result.ErrorCode);
    }

    [Fact]
    public async Task Eight_concurrent_streams_share_one_connection()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var articles = new PreparedArticle[8];
        for (var i = 0; i < articles.Length; i++)
        {
            articles[i] = CreateArticle($"<mux{i}@example.test>");
            server.Register(articles[i].Record, articles[i].SelectedDateHeaderName);
        }

        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);
        var tasks = articles.Select(a =>
            client.FetchArticleAsync(server.CreateCacheUri(), a.RequestId, a.Record.ArtId, CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(VatpFetchKind.Success, r.Kind));
        Assert.Equal(1, pool.ConnectionCountForTest(server.Host, server.Port));
    }

    [Fact]
    public async Task Cancel_one_stream_returns_cancelled()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.FinWithoutEnd;
        var prepared = CreateArticle("<cancel-me@example.test>", body: BuildLargeBody(512 * 1024));
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var result = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            cts.Token);
        Assert.True(
            result.Kind is VatpFetchKind.Cancelled or VatpFetchKind.ConnectionFailure or VatpFetchKind.IncompleteOrMalformedArticle,
            result.Kind.ToString());
    }

    [Fact]
    public async Task Connection_loss_mid_data_fails_fetch()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.DisconnectMidData;
        var prepared = CreateArticle("<drop@example.test>", body: BuildLargeBody(256 * 1024));
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);
        var result = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
    }

    [Fact]
    public async Task Pool_removes_dead_connection()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var prepared = CreateArticle("<dead-conn@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);
        server.Mode = VatpLoopbackTestServer.TransferMode.DisconnectMidData;
        _ = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        await WaitUntilAsync(() => pool.AliveConnectionCount(server.Host, server.Port) == 0, TimeSpan.FromSeconds(3));
        server.Mode = VatpLoopbackTestServer.TransferMode.Complete;
        var retry = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.Success, retry.Kind);
    }

    [Fact]
    public async Task Tls_wrong_target_host_fails_without_test_callback()
    {
        await using var server = VatpLoopbackTestServer.Start();
        var prepared = CreateArticle("<tls-host@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var pool = new VatpConnectionPool(NullLogger<VatpConnectionPool>.Instance)
        {
            TestTcpConnectHost = IPAddress.Loopback.ToString(),
        };
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);
        var uri = $"cache://wrong.host.test:{server.Port}/30edc94157aa16fe644a45a1f1ffe160";
        var result = await client.FetchArticleAsync(uri, prepared.RequestId, prepared.Record.ArtId, CancellationToken.None);
        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
    }

    [Fact]
    public async Task Fin_without_end_over_tls_does_not_succeed()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.FinWithoutEnd;
        var prepared = CreateArticle("<fin-no-end@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var client = CreateClient(server);
        var result = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
    }

    [Fact]
    public async Task ConnectionLoss_MidMeta_FailsTransferCleanly()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.DisconnectMidMeta;
        var prepared = CreateArticle("<mid-meta@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);

        var result = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.Equal(VatpFetchKind.ConnectionFailure, result.Kind);
        Assert.NotEqual(VatpFetchKind.Success, result.Kind);
        Assert.Equal(default, result.Record);
        await WaitUntilAsync(() => pool.AliveConnectionCount(server.Host, server.Port) == 0, TimeSpan.FromSeconds(3));

        server.Mode = VatpLoopbackTestServer.TransferMode.Complete;
        var retry = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.Success, retry.Kind);
        Assert.Equal(prepared.Record.ArtId, retry.Record.ArtId);
    }

    [Fact]
    public async Task ConnectionLoss_AfterFinBeforeEnd_FailsTransferCleanly()
    {
        await using var server = VatpLoopbackTestServer.Start();
        server.Mode = VatpLoopbackTestServer.TransferMode.DisconnectAfterFinBeforeEnd;
        var prepared = CreateArticle("<fin-before-end@example.test>");
        server.Register(prepared.Record, prepared.SelectedDateHeaderName);
        var pool = CreatePool(server);
        var client = new VatpArticleClient(pool, NullLogger<VatpArticleClient>.Instance);

        var result = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);

        Assert.True(
            result.Kind is VatpFetchKind.ConnectionFailure or VatpFetchKind.IncompleteOrMalformedArticle,
            result.Kind.ToString());
        Assert.NotEqual(VatpFetchKind.Success, result.Kind);
        Assert.Equal(default, result.Record);
        await WaitUntilAsync(() => pool.AliveConnectionCount(server.Host, server.Port) == 0, TimeSpan.FromSeconds(3));

        server.Mode = VatpLoopbackTestServer.TransferMode.Complete;
        var retry = await client.FetchArticleAsync(
            server.CreateCacheUri(),
            prepared.RequestId,
            prepared.Record.ArtId,
            CancellationToken.None);
        Assert.Equal(VatpFetchKind.Success, retry.Kind);
        Assert.Equal(prepared.Record.ArtId, retry.Record.ArtId);
    }

    private static VatpArticleClient CreateClient(VatpLoopbackTestServer server) =>
        new(CreatePool(server), NullLogger<VatpArticleClient>.Instance);

    private static VatpConnectionPool CreatePool(VatpLoopbackTestServer server)
    {
        var pool = new VatpConnectionPool(
            NullLogger<VatpConnectionPool>.Instance,
            new VatpClientOptions
            {
                IoTimeout = TimeSpan.FromSeconds(2),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                TlsHandshakeTimeout = TimeSpan.FromSeconds(5),
            })
        {
            ServerCertificateValidationCallback = static (_, _, _, _) => true,
            TestTcpConnectHost = IPAddress.Loopback.ToString(),
        };
        return pool;
    }

    private static PreparedArticle CreateArticle(string messageId, string body = "body\r\n")
    {
        var parser = new NntpArticleParser("backfiller.test");
        var destuffed = BuildDestuffed(messageId, body);
        var created = ArticleRecordFactory.TryCreate(parser, destuffed);
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return new PreparedArticle(Guid.NewGuid(), created.Record, created.SelectedDateHeaderName);
    }

    private const string BodyLine = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\r\n";

    private static string BuildLargeBody(int minimumBytes)
    {
        var builder = new System.Text.StringBuilder(minimumBytes + BodyLine.Length);
        while (builder.Length < minimumBytes)
        {
            builder.Append(BodyLine);
        }

        return builder.ToString();
    }

    private static byte[] BuildDestuffed(string messageId, string body) =>
        System.Text.Encoding.ASCII.GetBytes(
            "Path: peer.example\r\n"
            + "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
            + "Message-ID: " + messageId + "\r\n"
            + "Newsgroups: alt.test\r\n"
            + "From: user@example.test\r\n"
            + "Subject: s\r\n"
            + "\r\n"
            + body);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("Timed out waiting for condition.");
    }

    private readonly record struct PreparedArticle(
        Guid RequestId,
        ArticleRecord Record,
        NntpArticleHeaderName SelectedDateHeaderName);
}

internal static class VatpConnectionPoolTestExtensions
{
    public static int ConnectionCountForTest(this VatpConnectionPool pool, string host, int port) =>
        pool.GetEndpointConnectionCount(host, port);

    public static int AliveConnectionCount(this VatpConnectionPool pool, string host, int port) =>
        pool.GetEndpointAliveConnectionCount(host, port);
}
