using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Nntp;

public sealed class NntpDateKeepAliveTests
{
    [Fact]
    public async Task Provider_and_session_construction_receive_the_mysql_keepalive()
    {
        var factory = new ScriptedNntpTransportFactory();
        factory.Enqueue(NntpSessionPoolTests.CreateDateAwareServer());
        await using var pool = NntpSessionPoolTests.CreatePool(factory, max: 1, keepAliveSeconds: 30);

        await using var lease = await pool.AcquireAsync(CancellationToken.None);

        Assert.Equal((byte)30, factory.ConnectAttempts[0].KeepAliveSeconds);
        Assert.Equal((byte)30, lease.Session.KeepAliveSeconds);
    }

    [Fact]
    public async Task Date_is_issued_after_the_configured_idle_interval_and_not_before()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        var dateStarted = NewSource();
        var factory = new ScriptedNntpTransportFactory();
        var server = NntpSessionPoolTests.CreateDateAwareServer(dateStarted: dateStarted);
        factory.Enqueue(server);
        await using var pool = NntpSessionPoolTests.CreatePool(
            factory,
            max: 1,
            min: 1,
            keepAliveSeconds: 2,
            time: time);
        await pool.WarmupAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(dateStarted.Task.IsCompleted);
        Assert.DoesNotContain(server.Commands, static command => command.Equals("DATE", StringComparison.OrdinalIgnoreCase));

        time.Advance(TimeSpan.FromSeconds(1));
        await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(server.Commands, static command => command.Equals("DATE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Healthy_date_keeps_the_idle_session_reusable()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        var dateStarted = NewSource();
        var factory = new ScriptedNntpTransportFactory();
        factory.Enqueue(NntpSessionPoolTests.CreateDateAwareServer(dateStarted: dateStarted));
        await using var pool = NntpSessionPoolTests.CreatePool(
            factory,
            max: 1,
            min: 1,
            keepAliveSeconds: 3,
            time: time);
        await pool.WarmupAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(3));
        await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await using var lease = await pool.AcquireAsync(CancellationToken.None);
        using var result = await lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
        Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
        Assert.True(result.SessionReusable);
        Assert.True(lease.Session.IsReusable);
        Assert.Single(factory.ConnectAttempts);
    }

    [Fact]
    public async Task Date_protocol_failure_retires_the_session()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        var dateStarted = NewSource();
        var factory = new ScriptedNntpTransportFactory();
        factory.Enqueue(NntpSessionPoolTests.CreateDateAwareServer("400 no\r\n", dateStarted));
        factory.Enqueue(NntpSessionPoolTests.CreateDateAwareServer());
        await using var pool = NntpSessionPoolTests.CreatePool(
            factory,
            max: 1,
            min: 1,
            keepAliveSeconds: 2,
            time: time);
        await pool.WarmupAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(2));
        await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => pool.LiveSessionCount == 0);

        await using var replacement = await pool.AcquireAsync(CancellationToken.None);
        Assert.Equal(2, factory.ConnectAttempts.Count);
        Assert.True(replacement.Session.IsReusable);
    }

    [Fact]
    public async Task Date_timeout_marks_the_session_unhealthy()
    {
        var dateStarted = NewSource();
        var factory = new ScriptedNntpTransportFactory();
        factory.Enqueue(
            NntpSessionPoolTests.CreateDateAwareServer(
                dateStarted: dateStarted,
                blockDate: NewSource()));
        var session = new NntpProviderSession(
            new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1, 1),
            NntpSessionOptions.Default with
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                CommandTimeout = TimeSpan.FromMilliseconds(80),
                ReceiveTimeout = TimeSpan.FromSeconds(2),
            },
            NullLogger.Instance);
        Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

        var date = session.SendDateKeepAliveAsync(CancellationToken.None);
        await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(await date.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(session.IsReusable);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Date_eof_retires_the_session()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        var dateStarted = NewSource();
        var factory = new ScriptedNntpTransportFactory();
        var server = NntpSessionPoolTests.CreateDateAwareServer(dateStarted: dateStarted);
        server.CompleteAfterDateWithoutResponse = true;
        factory.Enqueue(server);
        factory.Enqueue(NntpSessionPoolTests.CreateDateAwareServer());
        await using var pool = NntpSessionPoolTests.CreatePool(
            factory,
            max: 1,
            min: 1,
            keepAliveSeconds: 1,
            time: time);
        await pool.WarmupAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(1));
        await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => pool.LiveSessionCount == 0);

        await using var replacement = await pool.AcquireAsync(CancellationToken.None);
        Assert.Equal(2, factory.ConnectAttempts.Count);
    }

    [Fact]
    public async Task Article_on_a_leased_session_does_not_run_date()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        var articleStarted = NewSource();
        var blockArticle = NewSource();
        var factory = new ScriptedNntpTransportFactory();
        var server = NntpSessionPoolTests.CreateDateAwareServer(
            articleStarted: articleStarted,
            blockArticle: blockArticle);
        factory.Enqueue(server);
        await using var pool = NntpSessionPoolTests.CreatePool(
            factory,
            max: 1,
            keepAliveSeconds: 1,
            time: time);

        await using var lease = await pool.AcquireAsync(CancellationToken.None);
        var download = lease.Session.DownloadArticleAsync("<a@b>", CancellationToken.None);
        await articleStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await lease.Session.SendDateKeepAliveAsync(CancellationToken.None));
        Assert.DoesNotContain(server.Commands, static command => command.Equals("DATE", StringComparison.OrdinalIgnoreCase));

        blockArticle.TrySetResult();
        using var result = await download.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
        Assert.DoesNotContain(server.Commands, static command => command.Equals("DATE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Shutdown_cancels_a_pending_keepalive_without_leaking_the_session()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        var dateStarted = NewSource();
        var factory = new ScriptedNntpTransportFactory();
        factory.Enqueue(NntpSessionPoolTests.CreateDateAwareServer(dateStarted: dateStarted));
        var pool = NntpSessionPoolTests.CreatePool(
            factory,
            max: 1,
            min: 1,
            keepAliveSeconds: 30,
            time: time);
        await pool.WarmupAsync(CancellationToken.None);

        await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(dateStarted.Task.IsCompleted);
        Assert.Equal(0, pool.LiveSessionCount);
        Assert.Equal(0, pool.ActiveLeaseCount);
    }

    [Fact]
    public async Task Shutdown_interrupts_an_in_flight_date()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        var dateStarted = NewSource();
        var factory = new ScriptedNntpTransportFactory();
        var server = NntpSessionPoolTests.CreateDateAwareServer(
            dateStarted: dateStarted,
            blockDate: NewSource());
        factory.Enqueue(server);
        var pool = NntpSessionPoolTests.CreatePool(
            factory,
            max: 1,
            min: 1,
            keepAliveSeconds: 1,
            time: time,
            commandTimeout: TimeSpan.FromSeconds(2));
        await pool.WarmupAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(1));
        await dateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, pool.LiveSessionCount);
        Assert.Equal(0, pool.ActiveLeaseCount);
    }

    [Fact]
    public async Task Replacement_applies_new_keepalive_to_new_sessions_and_leaves_leased_sessions_intact()
    {
        var catalog = new ProviderConfigurationCatalog();
        var transport = new ScriptedNntpTransportFactory();
        NntpSessionPoolTests.EnqueueReadyServers(transport, count: 2);
        await using var registry = new NntpProviderRegistry(
            catalog,
            transport,
            NntpSessionOptions.Default with
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                CommandTimeout = TimeSpan.FromSeconds(2),
                ReceiveTimeout = TimeSpan.FromSeconds(2),
            },
            TimeSpan.FromSeconds(2),
            NullLogger<NntpProviderRegistry>.Instance);
        var original = new BackFillerProviderDefinition(
            "Giganews",
            "news.example.test",
            563,
            true,
            "nntp-user",
            "p",
            0,
            4,
            KeepAliveSeconds: 5);
        await registry.ApplySnapshotAsync([original], CancellationToken.None);
        Assert.True(registry.TryGetPool("Giganews", out var oldPool));
        var lease = await oldPool.AcquireAsync(CancellationToken.None);
        Assert.Equal((byte)5, lease.Session.KeepAliveSeconds);

        var replacement = original with { KeepAliveSeconds = 10 };
        var applying = registry.ApplySnapshotAsync([replacement], CancellationToken.None);
        Assert.Equal(NntpSessionState.Ready, lease.Session.State);
        Assert.Equal((byte)5, lease.Session.KeepAliveSeconds);
        Assert.True(registry.TryGetPool("Giganews", out var newPool));
        Assert.NotSame(oldPool, newPool);
        Assert.Equal((byte)10, newPool.Provider.KeepAliveSeconds);

        await using var created = await newPool.AcquireAsync(CancellationToken.None);
        Assert.Equal((byte)10, created.Session.KeepAliveSeconds);
        await created.DisposeAsync();

        await lease.DisposeAsync();
        await applying;
        Assert.True(registry.TryGetPool("Giganews", out var after));
        Assert.Same(newPool, after);
        Assert.Equal((byte)10, after.Provider.KeepAliveSeconds);
    }

    [Fact]
    public async Task Zero_keepalive_does_not_issue_date()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        var factory = new ScriptedNntpTransportFactory();
        var server = NntpSessionPoolTests.CreateDateAwareServer();
        factory.Enqueue(server);
        await using var pool = NntpSessionPoolTests.CreatePool(
            factory,
            max: 1,
            min: 1,
            keepAliveSeconds: 0,
            time: time);
        await pool.WarmupAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(server.Commands, static command => command.Equals("DATE", StringComparison.OrdinalIgnoreCase));
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
