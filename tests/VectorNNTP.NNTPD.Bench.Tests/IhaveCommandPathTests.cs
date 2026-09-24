using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.History;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session;

namespace VectorNNTP.NNTPD.Bench.Tests;

public sealed class IhaveCommandPathTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(15);

    private static readonly byte[] StuffedArticle = "Subject: stuffed\r\n\r\n..leading-dot\r\n.\r\n"u8.ToArray();

    [Fact]
    public async Task Accepts_Count235_AndByteAccounting()
    {
        await using var server = new FakeIHaveServer(FakeIHaveBehavior.AcceptAll);
        var articles = IhavePreparedArticles.FromWireArticles(StuffedArticle);
        var worker = CreateWorker(server.Port, articles, TimeSpan.FromSeconds(0.4));
        await RunWorkerAsync(worker);

        Assert.True(worker.Accepted235 > 0);
        Assert.Equal(worker.Sent, worker.Accepted235);
        Assert.Equal(0, worker.Rejected435);
        Assert.Equal(0, worker.ProtocolErrors);
        Assert.Equal(0, worker.ConnectionErrors);
        Assert.Equal(1, worker.MaxOutstanding);
        Assert.True(server.Replies235 >= worker.Sent);
        Assert.True(server.Replies235 <= worker.Sent + 1);
        Assert.Equal(worker.Sent * (worker.WireCommandBytes + StuffedArticle.Length), worker.BytesSent);
        Assert.Equal(worker.Sent * StuffedArticle.Length, worker.ArticleBytesSent);
    }

    [Fact]
    public async Task Sequence_IsIhaveThen335ThenArticleThen235()
    {
        await using var server = new FakeIHaveServer(FakeIHaveBehavior.AcceptAll);
        var articles = IhavePreparedArticles.FromWireArticles(StuffedArticle);
        var worker = CreateWorker(server.Port, articles, TimeSpan.FromSeconds(0.3));
        await RunWorkerAsync(worker);

        Assert.True(worker.Accepted235 > 0);
        Assert.True(server.Replies235 >= worker.Accepted235);
        Assert.True(server.Replies235 <= worker.Accepted235 + 1);
        Assert.Equal(server.Replies235, server.ArticlesReceived);
        Assert.True(server.Replies335 >= server.Replies235);
        Assert.True(server.IhaveCommands >= server.Replies335);
        Assert.StartsWith("<i", server.LastMessageId, StringComparison.Ordinal);
        Assert.Contains("-00-", server.LastMessageId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transmits_RawDotStuffedArticle_IncludingTerminator()
    {
        await using var server = new FakeIHaveServer(FakeIHaveBehavior.AcceptAll);
        var articles = IhavePreparedArticles.FromWireArticles(StuffedArticle);
        var worker = CreateWorker(server.Port, articles, TimeSpan.FromSeconds(0.25));
        await RunWorkerAsync(worker);

        Assert.NotNull(server.LastArticle);
        Assert.Equal(StuffedArticle, server.LastArticle);
        Assert.True(server.LastArticle.AsSpan().EndsWith("\r\n.\r\n"u8));
        Assert.Contains("..leading-dot"u8.ToArray(), server.LastArticle);
    }

    [Fact]
    public async Task Unexpected435_DoesNotCountAsSuccess()
    {
        await using var server = new FakeIHaveServer(FakeIHaveBehavior.RejectNotWanted);
        var articles = IhavePreparedArticles.FromWireArticles(StuffedArticle);
        var worker = CreateWorker(server.Port, articles, TimeSpan.FromSeconds(0.3));
        await RunWorkerAsync(worker, allowFault: true);

        Assert.Equal(1, worker.Sent);
        Assert.Equal(0, worker.Accepted235);
        Assert.Equal(1, worker.Rejected435);
        Assert.Equal(0, worker.ArticleBytesSent);
        Assert.Equal(0, server.ArticlesReceived);
        Assert.NotNull(worker.Fault);
    }

    [Fact]
    public async Task DuplicateMessageId_DoesNotCountAsSuccess()
    {
        await using var server = new FakeIHaveServer(FakeIHaveBehavior.RejectDuplicatesAs435);
        var articles = IhavePreparedArticles.FromWireArticles(StuffedArticle);
        var worker = new IhaveConnection(
            id: 0,
            host: "127.0.0.1",
            port: server.Port,
            articles: articles,
            duration: TimeSpan.FromSeconds(0.4),
            warmup: TimeSpan.Zero,
            uniqueMessageIds: false);
        await RunWorkerAsync(worker, allowFault: true);

        Assert.Equal(1, worker.Accepted235);
        Assert.Equal(1, worker.Rejected435);
        Assert.Equal(2, worker.Sent);
        Assert.Equal(1, server.ArticlesReceived);
        Assert.Equal(1, server.Replies435);
        Assert.NotNull(worker.Fault);
    }

    [Fact]
    public async Task ProductionPath_ExercisesHistoryDbPeek()
    {
        var redis = new CheckDelayedRedis();
        await using var host = await IhaveProductionHost.StartAsync(redis);
        var articles = IhavePreparedArticles.FromWireArticles(StuffedArticle);
        var worker = CreateWorker(host.Port, articles, TimeSpan.FromSeconds(0.4));
        await RunWorkerAsync(worker);

        Assert.True(worker.Accepted235 > 0);
        Assert.Equal(worker.Sent, worker.Accepted235);
        Assert.True(redis.ExistsCount >= worker.Accepted235);
        Assert.Equal(0, worker.Rejected435);
    }

    [Fact]
    public async Task ProductionPath_DuplicateRememberedId_FailsClearly()
    {
        var redis = new CheckDelayedRedis();
        await using var host = await IhaveProductionHost.StartAsync(redis);
        var articles = IhavePreparedArticles.FromWireArticles(StuffedArticle);
        var worker = new IhaveConnection(
            id: 0,
            host: "127.0.0.1",
            port: host.Port,
            articles: articles,
            duration: TimeSpan.FromSeconds(0.4),
            warmup: TimeSpan.Zero,
            uniqueMessageIds: false);
        await RunWorkerAsync(worker, allowFault: true);

        Assert.Equal(1, worker.Accepted235);
        Assert.Equal(1, worker.Rejected435);
        Assert.True(redis.ExistsCount >= 1);
        Assert.NotNull(worker.Fault);
    }

    private static IhaveConnection CreateWorker(int port, IhavePreparedArticles articles, TimeSpan duration) =>
        new(
            id: 0,
            host: "127.0.0.1",
            port: port,
            articles: articles,
            duration: duration,
            warmup: TimeSpan.Zero);

    private static async Task RunWorkerAsync(IhaveConnection worker, bool allowFault = false)
    {
        using var cts = new CancellationTokenSource(Safety);
        await worker.RunAsync(cts.Token);
        await worker.DisposeAsync();
        if (!allowFault && worker.Fault is not null and not OperationCanceledException)
        {
            Assert.Fail(worker.Fault.ToString());
        }
    }
}

internal sealed class IhaveProductionHost : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ArticleIngestionQueue _queue;
    private readonly HistoryDb _history;
    private readonly Task _accept;
    private readonly Task _drain;

    private IhaveProductionHost(TcpListener listener, ArticleIngestionQueue queue, HistoryDb history)
    {
        _listener = listener;
        _queue = queue;
        _history = history;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _accept = AcceptAsync();
        _drain = DrainAsync();
    }

    public int Port { get; }

    public static Task<IhaveProductionHost> StartAsync(CheckDelayedRedis redis)
    {
        var history = new HistoryDb(
            redis,
            Options.Create(new NntpdOptions { HistoryTime = TimeSpan.FromHours(2) }),
            NullLogger<HistoryDb>.Instance);
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
        {
            QueueCapacity = 64,
            MaxArticleBytes = 64 * 1024,
        });
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new IhaveProductionHost(listener, queue, history));
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var socket = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
                _ = ServeAsync(socket);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    private async Task ServeAsync(Socket socket)
    {
        try
        {
            var remote = (IPEndPoint)(socket.RemoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0));
            await using var connection = NntpConnection.StartPlain(
                socket,
                ConnectionClientIdentity.Direct(remote),
                NullLogger.Instance);
            var session = new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: _queue,
                historyDb: _history);
            session.SetAuthorization(NntpAuthorization.TrustedTransitPeer);
            await session.RunAsync().ConfigureAwait(false);
        }
        catch (Exception) when (!_cts.IsCancellationRequested)
        {
            // client closed
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                _ = await _queue.DequeueAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _queue.Complete();
        try
        {
            await _accept.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        try
        {
            await _drain.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        _cts.Dispose();
    }
}
