namespace VectorNNTP.NNTPD.Bench.Tests;

public sealed class TakeThisTimingTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task TimingSamples_AreCollected_OnlyWhenEnabled()
    {
        await using var server = new FakeTakeThisServer(FakeTakeThisBehavior.AcceptAll);
        var article = TakeThisArticlePayload.Build(256);
        var timed = new TakeThisConnection(
            id: 0,
            host: "127.0.0.1",
            port: server.Port,
            article: article,
            pipelineDepth: 4,
            duration: TimeSpan.FromSeconds(0.35),
            warmup: TimeSpan.Zero,
            collectTiming: true);
        using (var cts = new CancellationTokenSource(Safety))
        {
            await timed.RunAsync(cts.Token);
        }

        await timed.DisposeAsync();
        Assert.Null(timed.Fault);
        Assert.True(timed.Accepted239 > 0);
        Assert.NotNull(timed.TimingSamples);
        Assert.True(timed.TimingSamples!.Length > 0);
        Assert.All(timed.TimingSamples, static s =>
        {
            Assert.True(s.TransactionUs >= s.CommandAndArticleSendUs);
            Assert.True(s.TransactionUs > 0);
            Assert.True(s.SendCalls >= 1);
            Assert.True(s.TotalSendBytes > 0);
            Assert.Equal(1, s.ActiveSendsAtStart);
        });
        Assert.Equal(1, timed.MaxActiveSends);

        await using var durationServer = new FakeTakeThisServer(FakeTakeThisBehavior.AcceptAll);
        var duration = new TakeThisConnection(
            id: 0,
            host: "127.0.0.1",
            port: durationServer.Port,
            article: article,
            pipelineDepth: 4,
            duration: TimeSpan.FromSeconds(0.2),
            warmup: TimeSpan.Zero);
        using (var cts = new CancellationTokenSource(Safety))
        {
            await duration.RunAsync(cts.Token);
        }

        await duration.DisposeAsync();
        Assert.Null(duration.Fault);
        Assert.True(duration.Accepted239 > 0);
        Assert.Null(duration.TimingSamples);
    }
}
