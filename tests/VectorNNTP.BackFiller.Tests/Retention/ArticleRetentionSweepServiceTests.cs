using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Retention;

public sealed class ArticleRetentionSweepServiceTests
{
    [Fact]
    public async Task Sweep_runs_immediately_then_stops_on_cancellation_and_closes_admission()
    {
        var authority = new RecordingRetentionAuthority();
        var sweep = new ArticleRetentionSweepService(authority, NullLogger<ArticleRetentionSweepService>.Instance);
        using var cts = new CancellationTokenSource();
        await sweep.StartAsync(cts.Token);
        await authority.Swept.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sweep.StopAsync(CancellationToken.None);
        Assert.True(authority.ShutdownStarted);
        Assert.True(authority.SweepCount >= 1);
    }

    [Fact]
    public async Task Sweep_interval_is_the_configured_retention_interval()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 64, sweep: TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), authority.SweepInterval);
        var sweep = new ArticleRetentionSweepService(authority, NullLogger<ArticleRetentionSweepService>.Instance);
        Assert.Equal(TimeSpan.FromSeconds(7), ((IArticleRetentionAuthority)authority).SweepInterval);
        await sweep.StopAsync(CancellationToken.None);
    }

    private sealed class RecordingRetentionAuthority : IArticleRetentionAuthority
    {
        public TaskCompletionSource Swept { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SweepCount { get; private set; }

        public bool ShutdownStarted { get; private set; }

        public TimeSpan SweepInterval => TimeSpan.FromHours(1);

        public long RetainedPayloadBytes => 0;

        public int RetainedCount => 0;

        public ArticleRetentionResult Retain(string messageId, byte[] payload) =>
            new(ArticleRetentionKind.ShuttingDown, null, null, 0, 0);

        public ArticleLookupResult TryGetByMessageId(string messageId) => ArticleLookupResult.Missing();

        public ArticleLookupResult TryGetByMd5(string md5Hex) => ArticleLookupResult.Missing();

        public long SweepExpired()
        {
            SweepCount++;
            Swept.TrySetResult();
            return 0;
        }

        public void BeginShutdown() => ShutdownStarted = true;
    }
}
