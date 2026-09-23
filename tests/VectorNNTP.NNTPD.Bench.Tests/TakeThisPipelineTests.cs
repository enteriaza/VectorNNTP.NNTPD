namespace VectorNNTP.NNTPD.Bench.Tests;

public sealed class TakeThisPipelineTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Accepts_Count239()
    {
        await using var server = new FakeTakeThisServer(FakeTakeThisBehavior.AcceptAll);
        var worker = CreateWorker(server.Port, depth: 4, duration: TimeSpan.FromSeconds(0.4));
        await RunWorkerAsync(worker);

        Assert.True(worker.Sent > 0);
        Assert.Equal(worker.Sent, worker.Accepted239);
        Assert.Equal(0, worker.Rejected439);
        Assert.Equal(0, worker.ProtocolErrors);
        Assert.Equal(0, worker.ConnectionErrors);
        Assert.Equal(worker.Sent, server.Replies239);
    }

    [Fact]
    public async Task Rejects_Count439()
    {
        await using var server = new FakeTakeThisServer(FakeTakeThisBehavior.RejectAll);
        var worker = CreateWorker(server.Port, depth: 4, duration: TimeSpan.FromSeconds(0.4));
        await RunWorkerAsync(worker);

        Assert.True(worker.Sent > 0);
        Assert.Equal(0, worker.Accepted239);
        Assert.Equal(worker.Sent, worker.Rejected439);
        Assert.Equal(0, worker.ProtocolErrors);
        Assert.Equal(worker.Sent, server.Replies439);
    }

    [Fact]
    public async Task UnexpectedResponse_IsProtocolError()
    {
        await using var server = new FakeTakeThisServer(FakeTakeThisBehavior.ProtocolError);
        var worker = CreateWorker(server.Port, depth: 2, duration: TimeSpan.FromSeconds(0.3));
        await RunWorkerAsync(worker);

        Assert.True(worker.ProtocolErrors > 0);
        Assert.Equal(0, worker.Accepted239);
        Assert.Equal(0, worker.Rejected439);
    }

    [Fact]
    public async Task PipelineDepth_IsRespected()
    {
        const int depth = 3;
        await using var server = new FakeTakeThisServer(new FakeTakeThisBehavior
        {
            HoldUntil = 10_000,
            ReplyFor = FakeTakeThisBehavior.AcceptAll.ReplyFor,
        });

        var worker = CreateWorker(server.Port, depth, TimeSpan.FromSeconds(0.6));
        using (var cts = new CancellationTokenSource(Safety))
        {
            await worker.RunAsync(cts.Token);
        }

        await worker.DisposeAsync();

        Assert.Equal(depth, worker.Sent);
        Assert.True(worker.MaxOutstanding <= depth);
        Assert.Equal(depth, server.ArticlesReceived);
        Assert.True(server.MaxUnreadArticles <= depth);
        Assert.Equal(0, worker.Accepted239);
    }

    [Fact]
    public async Task TwoConnections_AreIndependent()
    {
        await using var server = new FakeTakeThisServer(FakeTakeThisBehavior.AcceptAll);
        var article = TakeThisArticlePayload.Build(256);
        var a = new TakeThisConnection(0, "127.0.0.1", server.Port, article, 8, TimeSpan.FromSeconds(0.4), TimeSpan.Zero);
        var b = new TakeThisConnection(1, "127.0.0.1", server.Port, article, 8, TimeSpan.FromSeconds(0.4), TimeSpan.Zero);

        using var cts = new CancellationTokenSource(Safety);
        await Task.WhenAll(a.RunAsync(cts.Token), b.RunAsync(cts.Token));
        await a.DisposeAsync();
        await b.DisposeAsync();

        Assert.True(a.Sent > 0);
        Assert.True(b.Sent > 0);
        Assert.Equal(a.Sent, a.Accepted239);
        Assert.Equal(b.Sent, b.Accepted239);
        Assert.Equal(a.Sent + b.Sent, server.ArticlesReceived);
    }

    private static TakeThisConnection CreateWorker(int port, int depth, TimeSpan duration) =>
        new(
            id: 0,
            host: "127.0.0.1",
            port: port,
            article: TakeThisArticlePayload.Build(256),
            pipelineDepth: depth,
            duration: duration,
            warmup: TimeSpan.Zero);

    private static async Task RunWorkerAsync(TakeThisConnection worker)
    {
        using var cts = new CancellationTokenSource(Safety);
        await worker.RunAsync(cts.Token);
        await worker.DisposeAsync();
        if (worker.Fault is not null and not OperationCanceledException)
        {
            Assert.Fail(worker.Fault.ToString());
        }
    }
}
