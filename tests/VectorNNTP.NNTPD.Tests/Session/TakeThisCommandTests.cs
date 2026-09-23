using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Commands;
using VectorNNTP.NNTPD.Session.Framing;
using VectorNNTP.NNTPD.Tests.Networking.Transport;
using VectorNNTP.NNTPD.Tests.Transit;

namespace VectorNNTP.NNTPD.Tests.Session;

/// <summary>RFC 4644 TAKETHIS streaming / pipelining / ingestion contracts.</summary>
[Collection(nameof(TransportTestHostCollection))]
public sealed class TakeThisCommandTests
{
    private static NntpAuthorization TransitAuth { get; } = new(
        isAuthenticated: true,
        authorizedReader: false,
        authorizedTransit: true,
        postingPermitted: false,
        streamingPermitted: true);

    [Fact]
    public async Task TakeThis_ValidArticle_Returns239_AndEnqueues()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<article-one@example.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, "Subject: hi\r\n\r\nbody\r\n"));
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());

        var article = await queue.DequeueAsync(CancellationToken.None);
        Assert.NotNull(article);
        Assert.Equal(id, article!.MessageId);
        Assert.Equal("Subject: hi\r\n\r\nbody\r\n", Encoding.ASCII.GetString(article.Payload.Span));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task StreamTakeThis_PreservesDotStuffedWire_AndOmitsTerminator()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        Assert.Equal(NntpReceiveStrategy.StreamDataPlane, session.ReceiveStrategy);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<dot@example.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, "..foo\r\nbar\r\n"));
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());

        var article = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("..foo\r\nbar\r\n", Encoding.ASCII.GetString(article!.Payload.Span));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_PipelinedThree_ResponsesOrdered_AllQueued()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var ids = new[] { "<a@ex.com>", "<b@ex.com>", "<c@ex.com>" };
        var payload = new StringBuilder();
        foreach (var id in ids)
        {
            payload.Append(BuildTakeThis(id, $"Subject: {id}\r\n\r\nx\r\n"));
        }

        await duplex.WriteClientAsync(payload.ToString());

        foreach (var id in ids)
        {
            Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());
        }

        for (var i = 0; i < ids.Length; i++)
        {
            var article = await queue.DequeueAsync(CancellationToken.None);
            Assert.Equal(ids[i], article!.MessageId);
        }

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_TwoPipelinedInOneWrite_IdleFlushPeekLeavesSecondArticleReadable()
    {
        // Session idle-flush peeks Connection.Input after each TAKETHIS. That peek must
        // AdvanceTo(Start, Start). AdvanceTo(Start, End) marks leftover examined, so the
        // next STREAM ReadAsync waits even though the second TAKETHIS is already buffered.
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string first = "<peek1@ex.com>";
        const string second = "<peek2@ex.com>";
        await duplex.WriteClientAsync(
            BuildTakeThis(first, "Subject: 1\r\n\r\na\r\n") +
            BuildTakeThis(second, "Subject: 2\r\n\r\nb\r\n"));

        Assert.Equal($"239 {first}", await duplex.ReadClientLineAsync());
        Assert.Equal($"239 {second}", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TakeThis_SlowOutboundFlush_DoesNotBlockArticleReceive()
    {
        // Pause after ~1 byte so the first 239 flush blocks until the client reads.
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        await using var duplex = await TakeThisDuplex.CreateAsync(pauseWriterThreshold: 1);
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var ids = new[] { "<p1@ex.com>", "<p2@ex.com>", "<p3@ex.com>" };
        var payload = new StringBuilder();
        foreach (var id in ids)
        {
            payload.Append(BuildTakeThis(id, "Subject: x\r\n\r\ny\r\n"));
        }

        await duplex.WriteClientAsync(payload.ToString());

        // Articles must be accepted into the queue without the client reading any 239 yet.
        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (queue.Count < 3 && !waitCts.IsCancellationRequested)
        {
            await Task.Delay(5, waitCts.Token);
        }

        Assert.Equal(3, queue.Count);

        foreach (var id in ids)
        {
            Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());
        }

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_SlowSpoolWriter_DoesNotBlockEnqueue()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 8 });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new ConcurrentBag<string>();
        var persister = new GatedPersister(gate.Task, persisted);
        var writer = new IncomingSpoolWriterService(
            queue,
            persister,
            Options.Create(new NntpdOptions { ArticleIngestion = new ArticleIngestionOptions() }),
            NullLogger<IncomingSpoolWriterService>.Instance);

        await writer.StartAsync(CancellationToken.None);

        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var ids = new[] { "<s1@ex.com>", "<s2@ex.com>", "<s3@ex.com>" };
        foreach (var id in ids)
        {
            await duplex.WriteClientAsync(BuildTakeThis(id, "Subject: s\r\n\r\nz\r\n"));
            Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());
        }

        // All accepted before any disk write completes.
        Assert.Empty(persisted);
        gate.SetResult();

        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (persisted.Count < 3 && !drainCts.IsCancellationRequested)
        {
            await Task.Delay(5, drainCts.Token);
        }

        Assert.Equal(3, persisted.Count);
        queue.Complete();
        await writer.StopAsync(CancellationToken.None);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_QueueFull_AppliesBackpressureThenAccepts()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 1 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(BuildTakeThis("<q1@ex.com>", "Subject: 1\r\n\r\na\r\n"));
        Assert.Equal("239 <q1@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);

        // Second response must wait until capacity frees (enqueue backpressure).
        var secondResponse = duplex.ReadClientLineAsync();
        await duplex.WriteClientAsync(BuildTakeThis("<q2@ex.com>", "Subject: 2\r\n\r\nb\r\n"));
        await Task.Delay(80);
        Assert.False(secondResponse.IsCompleted);

        var first = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("<q1@ex.com>", first!.MessageId);

        Assert.Equal("239 <q2@ex.com>", await secondResponse.WaitAsync(TimeSpan.FromSeconds(5)));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_DisconnectMidArticle_DoesNotEnqueue()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync("TAKETHIS <partial@ex.com>\r\nSubject: x\r\n\r\nno-terminator");
        await duplex.CompleteClientInputAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task TakeThis_InvalidMessageId_Returns501_AfterConsumingArticle()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(BuildTakeThis("not-a-msgid", "Subject: x\r\n\r\ny\r\n"));
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_WithoutTransitAuth_Returns480()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("TAKETHIS <x@ex.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_QueueUnavailable_Returns400AndCloses()
    {
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(DisabledArticleIngestionQueue.Instance);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientAsync(BuildTakeThis("<tmp@ex.com>", "Subject: t\r\n\r\nz\r\n"));
        Assert.Equal("400 Service temporarily unavailable", await duplex.ReadClientLineAsync());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TakeThis_TooLarge_Returns439()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
        {
            QueueCapacity = 4,
            MaxArticleBytes = 16,
        });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<big@ex.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, "Subject: oversized-payload-here\r\n\r\nbody\r\n"));
        Assert.Equal($"439 {id}", await duplex.ReadClientLineAsync());
        Assert.Equal(0, queue.Count);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TakeThis_MixedAcceptReject_ResponseMessageIdsCorrelate()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions
        {
            QueueCapacity = 4,
            MaxArticleBytes = 40,
        });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var ok = "<ok@ex.com>";
        var big = "<big2@ex.com>";
        var payload = BuildTakeThis(ok, "Subject: ok\r\n\r\nx\r\n")
                      + BuildTakeThis(big, "Subject: this-is-definitely-too-large\r\n\r\nbody\r\n");
        await duplex.WriteClientAsync(payload);

        Assert.Equal($"239 {ok}", await duplex.ReadClientLineAsync());
        Assert.Equal($"439 {big}", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task DefaultUnspecified_UsesStreamDataPlaneRx()
    {
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(new ArticleIngestionQueue(new ArticleIngestionOptions()));
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);
        Assert.Equal(NntpReceiveStrategy.StreamDataPlane, session.ReceiveStrategy);
    }

    [Fact]
    public async Task ModeStream_KeepsStreamDataPlaneRx_WithoutSettingMode()
    {
        var source = System.Net.IPAddress.Parse("192.0.2.10");
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(
            new ArticleIngestionQueue(new ArticleIngestionOptions()),
            TransitTestPeers.ForAllowFrom(source),
            source);
        Assert.Equal(TransitTestPeers.DefaultPeerName, session.Authorization.TransitPeerName);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await duplex.ReadClientLineAsync());
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);
        Assert.Equal(NntpReceiveStrategy.StreamDataPlane, session.ReceiveStrategy);

        var id = "<stream-rx@ex.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, "Subject: s\r\n\r\nbody\r\n"));
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task ModeReader_UsesReaderCommandRx_AndDoesNotPreConsumeTakeThis()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE READER");
        Assert.StartsWith("201 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.Equal(NntpSessionMode.Reader, session.Mode);
        Assert.Equal(NntpReceiveStrategy.ReaderCommand, session.ReceiveStrategy);

        // Transit after MODE READER: TAKETHIS is dispatched as a command; handler reads the article.
        session.SetAuthorization(TransitAuth);
        Assert.Equal(NntpReceiveStrategy.ReaderCommand, session.ReceiveStrategy);

        const string id = "<reader-path@ex.com>";
        await duplex.WriteClientAsync(BuildTakeThis(id, "Subject: r\r\n\r\nreader\r\n"));
        Assert.Equal($"239 {id}", await duplex.ReadClientLineAsync());
        var article = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(id, article!.MessageId);
        Assert.Equal("Subject: r\r\n\r\nreader\r\n", Encoding.ASCII.GetString(article.Payload.Span));

        const string stuffedId = "<reader-destuff@ex.com>";
        await duplex.WriteClientAsync(BuildTakeThis(stuffedId, "..foo\r\nbar\r\n"));
        Assert.Equal($"239 {stuffedId}", await duplex.ReadClientLineAsync());
        var destuffed = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(".foo\r\nbar\r\n", Encoding.ASCII.GetString(destuffed!.Payload.Span));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task ModeReader_TakeThisWithoutTransit_DoesNotConsumeFollowingQuit()
    {
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(new ArticleIngestionQueue(new ArticleIngestionOptions()));
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("MODE READER");
        _ = await duplex.ReadClientLineAsync();
        Assert.Equal(NntpReceiveStrategy.ReaderCommand, session.ReceiveStrategy);

        await duplex.WriteClientLineAsync("TAKETHIS <x@ex.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        Assert.StartsWith("205 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await run;
    }

    [Fact]
    public async Task StreamRx_CheckThenTakeThis_CommandsInsideArticleRemainPayload()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        Assert.Equal(NntpReceiveStrategy.StreamDataPlane, session.ReceiveStrategy);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        var stored = "QUIT\r\nCHECK x\r\nTAKETHIS y\r\n";
        await duplex.WriteClientAsync(
            "CHECK <c@ex.com>\r\n" + BuildTakeThis("<in@body>", stored) + "QUIT\r\n");

        Assert.Equal("238 <c@ex.com> send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.Equal("239 <in@body>", await duplex.ReadClientLineAsync());
        var article = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(stored, Encoding.ASCII.GetString(article!.Payload.Span));
        Assert.StartsWith("205 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await run;
    }

    [Fact]
    public async Task ModeStream_Returns203_WithoutChangingMode()
    {
        var source = System.Net.IPAddress.Parse("192.0.2.10");
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(
            new ArticleIngestionQueue(new ArticleIngestionOptions()),
            TransitTestPeers.ForAllowFrom(source),
            source);
        Assert.Equal(TransitTestPeers.DefaultPeerName, session.Authorization.TransitPeerName);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);
        await duplex.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await duplex.ReadClientLineAsync());
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Capabilities_AdvertisesStreaming()
    {
        await using var duplex = await TakeThisDuplex.CreateAsync();
        var session = duplex.CreateSession(new ArticleIngestionQueue(new ArticleIngestionOptions()));
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CAPABILITIES");
        var lines = new List<string>();
        string line;
        do
        {
            line = await duplex.ReadClientLineAsync();
            lines.Add(line);
        }
        while (line != ".");

        Assert.Contains(lines, static l => l.Equals("STREAMING", StringComparison.Ordinal));

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task SpoolWriter_PersistsQueuedArticlesToIncomingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vectornntp-spool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = new ArticleIngestionOptions { IncomingDirectory = dir, QueueCapacity = 4 };
            var queue = new ArticleIngestionQueue(options);
            var persister = new IncomingSpoolFilePersister(
                Options.Create(new NntpdOptions { ArticleIngestion = options }),
                NullLogger<IncomingSpoolFilePersister>.Instance);
            var writer = new IncomingSpoolWriterService(
                queue,
                persister,
                Options.Create(new NntpdOptions { ArticleIngestion = options }),
                NullLogger<IncomingSpoolWriterService>.Instance);
            await writer.StartAsync(CancellationToken.None);

            var article = new InboundArticle(
                "<spool@ex.com>",
                Encoding.ASCII.GetBytes("Subject: spool\r\n\r\nok\r\n"),
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)),
                DateTimeOffset.UtcNow);
            Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(article, CancellationToken.None));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Directory.GetFiles(dir, "*.article").Length < 1 && !cts.IsCancellationRequested)
            {
                await Task.Delay(10, cts.Token);
            }

            var file = Assert.Single(Directory.GetFiles(dir, "*.article"));
            Assert.Equal("Subject: spool\r\n\r\nok\r\n", await File.ReadAllTextAsync(file));

            queue.Complete();
            await writer.StopAsync(CancellationToken.None);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }

    [Fact]
    public async Task SpoolWriter_ShutdownDrainsAlreadyQueuedArticles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vectornntp-drain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = new ArticleIngestionOptions { IncomingDirectory = dir, QueueCapacity = 8 };
            var queue = new ArticleIngestionQueue(options);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = new GatedPersister(gate.Task, []);
            // Use real file persister after gate for drain proof — compose:
            var filePersister = new IncomingSpoolFilePersister(
                Options.Create(new NntpdOptions { ArticleIngestion = options }),
                NullLogger<IncomingSpoolFilePersister>.Instance);
            var chained = new ChainedPersister(gate.Task, filePersister);

            var writer = new IncomingSpoolWriterService(
                queue,
                chained,
                Options.Create(new NntpdOptions { ArticleIngestion = options }),
                NullLogger<IncomingSpoolWriterService>.Instance);
            await writer.StartAsync(CancellationToken.None);

            for (var i = 0; i < 3; i++)
            {
                var article = new InboundArticle(
                    $"<drain{i}@ex.com>",
                    Encoding.ASCII.GetBytes($"Subject: {i}\r\n\r\n"),
                    ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)),
                    DateTimeOffset.UtcNow);
                Assert.True(queue.TryEnqueue(article));
            }

            var stop = writer.StopAsync(CancellationToken.None);
            await Task.Delay(30);
            Assert.False(stop.IsCompleted);
            gate.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(3, Directory.GetFiles(dir, "*.article").Length);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }

    [Fact]
    public async Task TakeThis_RealTcp_PipelinedWithoutWaitingForResponses()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 16 });
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        var session = new NntpSession(
            server,
            NullLogger<NntpSession>.Instance,
            articleIngestion: queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();

        _ = await ReadSocketLineAsync(client);

        var ids = new[] { "<tcp1@ex.com>", "<tcp2@ex.com>", "<tcp3@ex.com>" };
        var batch = new StringBuilder();
        foreach (var id in ids)
        {
            batch.Append(BuildTakeThis(id, $"Subject: {id}\r\n\r\nbody\r\n"));
        }

        var bytes = Encoding.ASCII.GetBytes(batch.ToString());
        await client.SendAsync(bytes);

        foreach (var id in ids)
        {
            Assert.Equal($"239 {id}", await ReadSocketLineAsync(client));
        }

        for (var i = 0; i < ids.Length; i++)
        {
            var article = await queue.DequeueAsync(CancellationToken.None);
            Assert.Equal(ids[i], article!.MessageId);
        }

        await client.SendAsync("QUIT\r\n"u8.ToArray());
        _ = await ReadSocketLineAsync(client);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MultilineReader_Unit_UnstuffsAndTerminates()
    {
        var pipe = new Pipe();
        var wire = Encoding.ASCII.GetBytes("..leading\r\nplain\r\n.\r\n");
        await pipe.Writer.WriteAsync(wire);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        var result = await NntpMultilineDataReader
            .ReadArticleAsync(pipe.Reader, 1024, CancellationToken.None);

        Assert.Equal(NntpMultilineReadStatus.Completed, result.Status);
        Assert.Equal(".leading\r\nplain\r\n", Encoding.ASCII.GetString(result.Payload.Span));
    }

    private static string BuildTakeThis(string messageId, string articleWithoutTerminator) =>
        $"TAKETHIS {messageId}\r\n{articleWithoutTerminator}.\r\n";

    private static async Task<string> ReadSocketLineAsync(Socket socket)
    {
        var buffer = new byte[1];
        var line = new StringBuilder();
        while (true)
        {
            var n = await socket.ReceiveAsync(buffer, SocketFlags.None);
            if (n == 0)
            {
                break;
            }

            line.Append((char)buffer[0]);
            if (line.Length >= 2 && line[^2] == '\r' && line[^1] == '\n')
            {
                return line.ToString(0, line.Length - 2);
            }
        }

        return line.ToString();
    }

    private sealed class GatedPersister(Task gate, ConcurrentBag<string> persisted) : IIncomingArticlePersister
    {
        private readonly Task _gate = gate;
        private readonly ConcurrentBag<string> _persisted = persisted;

        public async Task PersistAsync(InboundArticle article, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _persisted.Add(article.MessageId);
        }
    }

    private sealed class ChainedPersister(Task gate, IIncomingArticlePersister inner) : IIncomingArticlePersister
    {
        private readonly Task _gate = gate;
        private readonly IIncomingArticlePersister _inner = inner;

        public async Task PersistAsync(InboundArticle article, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _inner.PersistAsync(article, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TakeThisDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer;
        private readonly Pipe _serverToClient;

        private TakeThisDuplex(PipeOptions? outputOptions)
        {
            _clientToServer = new Pipe();
            _serverToClient = new Pipe(outputOptions ?? PipeOptions.Default);
        }

        public static Task<TakeThisDuplex> CreateAsync(long? pauseWriterThreshold = null)
        {
            PipeOptions? outputOptions = null;
            if (pauseWriterThreshold is { } pause)
            {
                outputOptions = new PipeOptions(
                    pauseWriterThreshold: pause,
                    resumeWriterThreshold: pause,
                    useSynchronizationContext: false);
            }

            return Task.FromResult(new TakeThisDuplex(outputOptions));
        }

        public NntpSession CreateSession(
            IArticleIngestionQueue queue,
            ITransitPeerAuthorization? transitPeers = null,
            System.Net.IPAddress? clientAddress = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(
                    new System.Net.IPEndPoint(clientAddress ?? System.Net.IPAddress.Loopback, 119)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                transitPeerAuthorization: transitPeers);
        }

        public async Task WriteClientLineAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task WriteClientAsync(string payload)
        {
            var bytes = Encoding.ASCII.GetBytes(payload);
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task CompleteClientInputAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }
    }

    private sealed class PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity) : INntpConnection
    {
        private readonly CancellationTokenSource _closed = new();
        private int _compressed;

        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
        public ConnectionClientIdentity ClientIdentity { get; } = identity;
        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public bool IsTls => false;
        public bool IsCompressed => Volatile.Read(ref _compressed) == 1;
        public bool IsCompleted => _closed.IsCancellationRequested;
        public long OutboundIdleVersion => 0;
        public CancellationToken ConnectionClosed => _closed.Token;

        public Task CompleteAsync(Exception? exception = null)
        {
            _closed.Cancel();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _closed.Dispose();
            return ValueTask.CompletedTask;
        }

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpgradeToTlsAsync(
            VectorNNTP.NNTPD.Networking.Certificates.ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _compressed, 1);
            return Task.CompletedTask;
        }

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipherSuite)
        {
            tlsVersion = string.Empty;
            cipherSuite = string.Empty;
            return false;
        }
    }
}
