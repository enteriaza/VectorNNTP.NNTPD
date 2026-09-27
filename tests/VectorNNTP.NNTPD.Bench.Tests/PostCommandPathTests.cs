namespace VectorNNTP.NNTPD.Bench.Tests;

public sealed class PostCommandPathTests
{
    [Fact]
    public async Task Accepts_Count240_AndUniqueArticleMessageIds()
    {
        await using var server = new FakePostServer(FakePostBehavior.AcceptAll);
        var article = TakeThisArticlePayload.Build(256);
        var worker = new PostConnection(
            id: 0,
            host: "127.0.0.1",
            port: server.Port,
            articleTemplate: article,
            authUser: "poster",
            authPassword: "secret",
            duration: TimeSpan.FromSeconds(0.4),
            warmup: TimeSpan.Zero);
        await worker.RunAsync(CancellationToken.None);

        Assert.True(worker.Accepted240 > 0);
        Assert.Equal(worker.Sent, worker.Accepted240);
        Assert.Equal(0, worker.Rejected441);
        Assert.Equal(0, worker.ProtocolErrors);
        Assert.Equal(0, worker.ConnectionErrors);
        Assert.Equal(1, worker.MaxOutstanding);
        Assert.True(server.Replies240 >= worker.Sent);
        Assert.StartsWith("<p", server.LastMessageId, StringComparison.Ordinal);
        Assert.Contains("-00-", server.LastMessageId, StringComparison.Ordinal);
        await worker.DisposeAsync();
    }

    [Fact]
    public async Task Reject441_DoesNotCountAsSuccess()
    {
        await using var server = new FakePostServer(FakePostBehavior.RejectAll);
        var article = TakeThisArticlePayload.Build(256);
        var worker = new PostConnection(
            id: 0,
            host: "127.0.0.1",
            port: server.Port,
            articleTemplate: article,
            authUser: "poster",
            authPassword: "secret",
            duration: TimeSpan.FromSeconds(0.3),
            warmup: TimeSpan.Zero);
        await worker.RunAsync(CancellationToken.None);

        Assert.True(worker.Rejected441 > 0);
        Assert.Equal(0, worker.Accepted240);
        Assert.True(worker.Sent >= worker.Rejected441);
        await worker.DisposeAsync();
    }
}
