using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Messaging.Cache;
using VectorNNTP.Common.Messaging.RabbitMq;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Tests.Fixtures;
using VectorNNTP.StorageServer.Tests.TestDoubles;

namespace VectorNNTP.StorageServer.Tests.Storage;

public sealed class DurableIndexArticlePresenceTests
{
    [Fact]
    public async Task NotReady_ReturnsFalse()
    {
        using var dirs = TempDirs.Create();
        var service = CreateService(dirs);
        var presence = new DurableIndexArticlePresence(service);
        var id = ArticleId.FromMessageId("<absent@seg.test>"u8);

        Assert.False(await presence.HasArticleAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task PresentEvictedInvalidAndMissing_FollowIndexState()
    {
        using var dirs = TempDirs.Create();
        var service = CreateService(dirs);
        var presence = new DurableIndexArticlePresence(service);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var missing = ArticleId.FromMessageId("<missing@seg.test>"u8);
            Assert.False(await presence.HasArticleAsync(missing, CancellationToken.None));

            var present = CreateRecord("<present@seg.test>");
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await service.Engine.AcceptAsync(present, CancellationToken.None)).Outcome);
            await service.Engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(await presence.HasArticleAsync(present.ArtId, CancellationToken.None));

            Assert.True(service.Engine.TryEvict(present.ArtId));
            Assert.False(await presence.HasArticleAsync(present.ArtId, CancellationToken.None));

            var invalid = CreateRecord("<invalid@seg.test>");
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await service.Engine.AcceptAsync(invalid, CancellationToken.None)).Outcome);
            await service.Engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(service.Engine.TryInvalidate(invalid.ArtId));
            Assert.False(await presence.HasArticleAsync(invalid.ArtId, CancellationToken.None));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ConflictingAccept_LeavesOriginalPresent()
    {
        using var dirs = TempDirs.Create();
        var service = CreateService(dirs);
        var presence = new DurableIndexArticlePresence(service);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var first = CreateRecord("<conflict@seg.test>", "body-a\r\n");
            var second = CreateRecord("<conflict@seg.test>", "body-b\r\n");
            Assert.Equal(first.ArtId, second.ArtId);
            Assert.NotEqual(first.ArtHash, second.ArtHash);

            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await service.Engine.AcceptAsync(first, CancellationToken.None)).Outcome);
            await service.Engine.DrainPendingAsync(CancellationToken.None);
            Assert.Equal(
                ArticleAcceptOutcome.Conflict,
                (await service.Engine.AcceptAsync(second, CancellationToken.None)).Outcome);

            Assert.True(await presence.HasArticleAsync(first.ArtId, CancellationToken.None));
            Assert.True(service.Engine.Index.TryGet(first.ArtId, out var meta));
            Assert.Equal(ArticleStorageState.Present, meta.State);
            Assert.Equal(first.ArtHash, meta.ArtHash);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Relocation_StaysPresent()
    {
        using var dirs = TempDirs.Create();
        var service = CreateService(dirs);
        var presence = new DurableIndexArticlePresence(service);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var record = CreateRecord("<relocated@seg.test>");
            var engine = service.Engine;
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var before));
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.Segments.TryGetSegmentInfo(before.Location.SegmentId, out var closed));

            var compactionId = engine.Journal.AllocateCompactionId();
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engine.Journal.AppendCompactionBeginAsync(
                    new JournalCompactionBeginRecord(1, compactionId, before.Location.SegmentId, closed.Generation),
                    CancellationToken.None));
            var relocated = await engine.RelocateArticleAsync(
                compactionId,
                relocationId: 1,
                before.Location.SegmentId,
                closed.Generation,
                record.ArtId,
                CancellationToken.None);

            Assert.Equal(ArticleRelocationOutcome.Relocated, relocated.Outcome);
            Assert.True(engine.Index.TryGet(record.ArtId, out var after));
            Assert.Equal(ArticleStorageState.Present, after.State);
            Assert.NotEqual(before.Location, after.Location);
            Assert.True(await presence.HasArticleAsync(record.ArtId, CancellationToken.None));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AfterStop_ReturnsFalse()
    {
        using var dirs = TempDirs.Create();
        var service = CreateService(dirs);
        var presence = new DurableIndexArticlePresence(service);
        await service.StartAsync(CancellationToken.None);
        var record = CreateRecord("<stopped@seg.test>");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await service.Engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await service.Engine.DrainPendingAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.False(await presence.HasArticleAsync(record.ArtId, CancellationToken.None));
    }

    [Fact]
    public async Task CanceledToken_Throws()
    {
        using var dirs = TempDirs.Create();
        var service = CreateService(dirs);
        var presence = new DurableIndexArticlePresence(service);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            presence.HasArticleAsync(ArticleId.FromMessageId("<cancel@seg.test>"u8), cts.Token).AsTask());
    }

    [Fact]
    public async Task LookupConsumer_AnswersPresent_AndStaysSilentAfterEvict()
    {
        using var dirs = TempDirs.Create();
        var service = CreateService(dirs);
        await service.StartAsync(CancellationToken.None);
        var factory = new RecordingStorageServerRabbitMqConnectionFactory();
        var rabbit = CreateRabbitMq(factory);
        var consumer = new StorageArticleLookupConsumerService(
            rabbit,
            CreateRuntime(dirs),
            new DurableIndexArticlePresence(service),
            NullLogger<StorageArticleLookupConsumerService>.Instance);
        try
        {
            var record = CreateRecord("<lookup@seg.test>");
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await service.Engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await service.Engine.DrainPendingAsync(CancellationToken.None);

            await rabbit.StartAsync(CancellationToken.None);
            await consumer.StartAsync(CancellationToken.None);
            var channel = Assert.Single(factory.LastConnection!.ManualAckChannels);
            var publish = Assert.Single(factory.LastConnection.PublishChannels);

            var missing = new StorageArticleLookupRequest(
                1,
                Guid.NewGuid(),
                ArticleId.FromMessageId("<missing-lookup@seg.test>"u8));
            await channel.DeliverAsync(
                3,
                StorageArticleLookupWireProtocol.SerializeRequestV1(missing),
                "corr-missing",
                "nntpd.reply");
            Assert.Empty(publish.Publications);
            Assert.Contains(3UL, channel.Acks);

            var request = new StorageArticleLookupRequest(1, Guid.NewGuid(), record.ArtId);
            await channel.DeliverAsync(
                4,
                StorageArticleLookupWireProtocol.SerializeRequestV1(request),
                "corr-present",
                "nntpd.reply");

            var hit = Assert.Single(publish.Publications);
            Assert.True(StorageArticleLookupWireProtocol.TryParseResponseV1(hit.Body.Span, out var response, out _));
            Assert.NotNull(response);
            Assert.Equal(record.ArtId, response.ArticleId);
            Assert.Contains(record.ArtId.ToLowerHexString(), response.Uri, StringComparison.Ordinal);

            Assert.True(service.Engine.TryEvict(record.ArtId));
            await channel.DeliverAsync(
                5,
                StorageArticleLookupWireProtocol.SerializeRequestV1(request),
                "corr-evicted",
                "nntpd.reply");

            Assert.Single(publish.Publications);
            Assert.Contains(4UL, channel.Acks);
            Assert.Contains(5UL, channel.Acks);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
            await rabbit.DisposeAsync();
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static StorageEngineApplicationService CreateService(TempDirs dirs) =>
        new(
            CreateRuntime(dirs),
            Options.Create(CreateBindable(dirs)),
            NullLogger<StorageEngineApplicationService>.Instance);

    private static StorageServerRuntimeOptions CreateRuntime(TempDirs dirs)
    {
        var options = CreateBindable(dirs);
        return StorageServerRuntimeOptionsFactory.Create(options, StorageServerTestOptions.CreateValidAcme(options));
    }

    private static StorageServerOptions CreateBindable(TempDirs dirs)
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.CacheDir = dirs.CacheDir;
        options.Storage.ControlDir = dirs.ControlDir;
        return options;
    }

    private static RabbitMqService CreateRabbitMq(IRabbitMqConnectionFactory factory)
    {
        var options = new RabbitMqOptions
        {
            Hosts = ["127.0.0.1"],
            Port = 5672,
            VirtualHost = "/",
            EnableSsl = false,
            Username = "guest",
            Password = "guest",
            PoolReconnectBaseDelayMs = 50,
            PoolReconnectMaxDelayMs = 50,
        };
        return new RabbitMqService(
            factory,
            Options.Create(options),
            new DelegateRabbitMqConnectionNameProvider(() => "VectorNNTP.StorageServer:test"),
            NullLogger<RabbitMqService>.Instance);
    }

    private static ArticleRecord CreateRecord(string messageId, string body = "line1\r\n")
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: presence\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class TempDirs : IDisposable
    {
        private TempDirs(string root, string cacheDir, string controlDir)
        {
            Root = root;
            CacheDir = cacheDir;
            ControlDir = controlDir;
        }

        public string Root { get; }

        public string CacheDir { get; }

        public string ControlDir { get; }

        public static TempDirs Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-p5g1-" + Guid.NewGuid().ToString("N"));
            var cache = Path.Combine(root, "cache");
            var control = Path.Combine(root, "control");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(control);
            return new TempDirs(root, cache, control);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
