namespace VectorNNTP.NNTPD.Bench.Tests;

public sealed class IhaveTimingTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(15);
    private static readonly byte[] Article = "Subject: t\r\n\r\nbody\r\n.\r\n"u8.ToArray();

    [Fact]
    public async Task TimingSamples_CollectExactCount_AndLeaveDefaultPathUntimed()
    {
        await using var server = new FakeIHaveServer(FakeIHaveBehavior.AcceptAll);
        var articles = IhavePreparedArticles.FromWireArticles(Article);
        var timed = new IhaveConnection(
            id: 0,
            host: "127.0.0.1",
            port: server.Port,
            articles: articles,
            duration: TimeSpan.FromSeconds(30),
            warmup: TimeSpan.Zero,
            timingSamples: 25);
        using (var cts = new CancellationTokenSource(Safety))
        {
            await timed.RunAsync(cts.Token);
        }

        await timed.DisposeAsync();
        Assert.Null(timed.Fault);
        Assert.Equal(25, timed.Accepted235);
        Assert.NotNull(timed.TimingSamples);
        Assert.Equal(25, timed.TimingSamples!.Length);
        Assert.All(timed.TimingSamples, static s =>
        {
            Assert.Equal(Article.Length, s.ArticleBytes);
            Assert.True(s.TransactionUs >= s.IhaveSentTo335Us + s.ArticleSendUs + s.ArticleSentTo235Us);
            Assert.True(s.TransactionUs > 0);
        });

        await using var durationServer = new FakeIHaveServer(FakeIHaveBehavior.AcceptAll);
        var duration = new IhaveConnection(
            id: 0,
            host: "127.0.0.1",
            port: durationServer.Port,
            articles: articles,
            duration: TimeSpan.FromSeconds(0.2),
            warmup: TimeSpan.Zero);
        using (var cts = new CancellationTokenSource(Safety))
        {
            await duration.RunAsync(cts.Token);
        }

        await duration.DisposeAsync();
        Assert.Null(duration.Fault);
        Assert.True(duration.Accepted235 > 0);
        Assert.Null(duration.TimingSamples);
    }

    [Fact]
    public void Percentile_IsMonotonic()
    {
        var values = new double[] { 10, 20, 30, 40, 50 };
        Assert.Equal(10, IhaveTimingStats.Percentile(values, 0));
        Assert.Equal(30, IhaveTimingStats.Percentile(values, 0.5));
        Assert.Equal(50, IhaveTimingStats.Percentile(values, 1));
        Assert.True(IhaveTimingStats.Percentile(values, 0.95) >= IhaveTimingStats.Percentile(values, 0.50));
    }
}
