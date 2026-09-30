using System.IO.Pipelines;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Messaging.Cache;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Storage;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.NNTPD.Transport.Vatp;

namespace VectorNNTP.NNTPD.Tests.Storage;

public sealed class ArticlePlacementTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T00:00:00Z");

    [Fact]
    public void Selector_PrefersHighestAvailableBytes_ThenServerId_ThenFqdn()
    {
        var entries = new StorageServerFleetEntry[]
        {
            Entry("cache02.example", 2, available: 10, port: 563),
            Entry("cache09.example", 9, available: 50, port: 563),
            Entry("cache03.example", 3, available: 50, port: 563),
        };
        Assert.True(StorageServerPlacementSelector.TrySelect(entries, out var selected));
        Assert.Equal(3, selected.ServerId);

        var tiedId = new StorageServerFleetEntry[]
        {
            Entry("m.example", 4, available: 20, port: 563),
            Entry("a.example", 4, available: 20, port: 563),
        };
        Assert.True(StorageServerPlacementSelector.TrySelect(tiedId, out var fqdn));
        Assert.Equal("a.example", fqdn.Fqdn);
    }

    [Fact]
    public void Selector_ExcludesMissingPort_AndEmptySetDoesNotSelect()
    {
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("cache01.example", 1, 100, port: null), Now);
        registry.ApplyAdvertisement(Advertisement("stale.example", 2, 500, port: 563), Now.AddSeconds(-10));
        var active = registry.GetActive(Now);
        Assert.Contains(active, static entry => entry.Fqdn == "cache01.example");
        Assert.DoesNotContain(active, static entry => entry.Fqdn == "stale.example");
        Assert.False(StorageServerPlacementSelector.TrySelect(active, out _));
        Assert.False(StorageServerPlacementSelector.TrySelect([], out _));
    }

    [Theory]
    [InlineData(InboundArticleProducer.TakeThis)]
    [InlineData(InboundArticleProducer.IHave)]
    [InlineData(InboundArticleProducer.Post)]
    public async Task LocalProducer_PlacesOnceAfterPersist(InboundArticleProducer producer)
    {
        var client = new RecordingPlacement();
        var registry = RegistryWithTarget();
        var persister = new OrderedPersister();
        var article = CanonicalArticleText.CreateQueued("<place@example.test>", producer);
        await RunAsync(article, persister, client, registry);
        Assert.Equal(1, persister.Count);
        Assert.Equal(1, client.Calls);
        Assert.True(client.SawPersist);
        Assert.Equal(article.Record.ArtId, client.Last.ArtId);
    }

    [Fact]
    public async Task BackFiller_DoesNotPlace()
    {
        var client = new RecordingPlacement();
        var article = CanonicalArticleText.CreateQueued("<bf@example.test>", InboundArticleProducer.BackFiller);
        await RunAsync(article, new OrderedPersister(), client, RegistryWithTarget());
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task NoEligibleServer_DoesNotDial()
    {
        var client = new RecordingPlacement { FailIfCalled = true };
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("cache01.example", 1, 100, port: null), Now);
        var article = CanonicalArticleText.CreateQueued("<none@example.test>", InboundArticleProducer.TakeThis);
        await RunAsync(article, new OrderedPersister(), client, registry, time: Now);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task PlacementFailure_DoesNotRequeue()
    {
        var client = new RecordingPlacement
        {
            Result = new ArticlePlacementResult(ArticlePlacementKind.Conflict),
        };
        var article = CanonicalArticleText.CreateQueued("<conflict@example.test>", InboundArticleProducer.Post);
        var persister = new OrderedPersister();
        await RunAsync(article, persister, client, RegistryWithTarget());
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, persister.Count);
    }

    [Fact]
    public void PlacementClient_DoesNotUseReadConnectionPool()
    {
        var fields = typeof(ArticlePlacementClient).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, static field => field.FieldType == typeof(VatpConnectionPool));
        Assert.False(typeof(VatpArticleClient).IsAssignableTo(typeof(IArticlePlacementClient)));
    }

    [Fact]
    public async Task Transfer_ResultAccepted_AndWindowPacing()
    {
        var article = CanonicalArticleText.CreateQueued("<xfer@example.test>", InboundArticleProducer.TakeThis).Record;
        Assert.True(ArticlePlacementMeta.TryFromRecord(article, out var meta));
        var limits = new ArticleTransferLimits
        {
            InitialStreamWindowBytes = 32,
            MaxStreamCreditBytes = 1024 * 1024,
            DefaultMaxFramePayload = 64,
        };
        Assert.True(article.ArtSize > 32);
        await using var link = new Link();
        var server = Task.Run(() => ScriptedServer.RunAsync(link.Server, result: (byte)ArticlePlacementKind.Accepted));
        var result = await VatpPlacementTransfer.PlaceAsync(
            link.Client,
            article,
            meta,
            limits,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Assert.Equal(ArticlePlacementKind.Accepted, result.Kind);
        Assert.True(await server.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Transfer_DropAfterEnd_IsAcknowledgementNotObserved()
    {
        var article = CanonicalArticleText.CreateQueued("<drop@example.test>", InboundArticleProducer.IHave).Record;
        Assert.True(ArticlePlacementMeta.TryFromRecord(article, out var meta));
        await using var link = new Link();
        var server = Task.Run(() => ScriptedServer.RunAsync(link.Server, result: null));
        var placed = await VatpPlacementTransfer.PlaceAsync(
            link.Client,
            article,
            meta,
            ArticleTransferLimits.Default,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Assert.Equal(ArticlePlacementKind.AcknowledgementNotObserved, placed.Kind);
        Assert.False(placed.Succeeded);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Transfer_CancelBeforeSend_DoesNotReportSuccess()
    {
        var article = CanonicalArticleText.CreateQueued("<cancel@example.test>", InboundArticleProducer.TakeThis).Record;
        Assert.True(ArticlePlacementMeta.TryFromRecord(article, out var meta));
        await using var link = new Link();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var placed = await VatpPlacementTransfer.PlaceAsync(
            link.Client,
            article,
            meta,
            ArticleTransferLimits.Default,
            TimeSpan.FromSeconds(5),
            cts.Token);
        Assert.Equal(ArticlePlacementKind.Cancelled, placed.Kind);
        Assert.False(placed.Succeeded);
    }

    private static async Task RunAsync(
        InboundArticle article,
        OrderedPersister persister,
        RecordingPlacement client,
        StorageServerRegistry registry,
        DateTimeOffset? time = null)
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        var writer = new IncomingSpoolWriterService(
            queue,
            persister,
            Options.Create(new NntpdOptions
            {
                ArticleIngestion = new ArticleIngestionOptions
                {
                    MinWorkers = 1,
                    MaxWorkers = 1,
                    ScaleIntervalSeconds = 3600,
                    OverviewDbMinPublisherWorkers = 1,
                    OverviewDbMaxPublisherWorkers = 1,
                },
            }),
            NullLogger<IncomingSpoolWriterService>.Instance,
            timeProvider: new FixedTime(time ?? Now),
            placement: client,
            placementRegistry: registry);
        await writer.StartAsync(CancellationToken.None);
        Assert.Equal(ArticleEnqueueResult.Accepted, await queue.EnqueueAsync(article, CancellationToken.None));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await persister.Completed.Task.WaitAsync(cts.Token);
        if (article.Producer is InboundArticleProducer.TakeThis or InboundArticleProducer.IHave or InboundArticleProducer.Post
            && client.Calls == 0
            && !client.FailIfCalled)
        {
            await client.Placed.Task.WaitAsync(cts.Token);
        }

        queue.Complete();
        await writer.StopAsync(CancellationToken.None);
    }

    private static StorageServerRegistry RegistryWithTarget()
    {
        var registry = new StorageServerRegistry();
        registry.ApplyAdvertisement(Advertisement("cache01.example", 1, 1000, 563), Now);
        return registry;
    }

    private static StorageServerAdvertisement Advertisement(string fqdn, int serverId, long available, int? port) =>
        new(1, serverId, fqdn, 10_000, 10_000 - available, available, Now, port);

    private static StorageServerFleetEntry Entry(string fqdn, int serverId, long available, int? port) =>
        new(serverId, fqdn, 10_000, 10_000 - available, available, Now, port);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class OrderedPersister : IIncomingArticlePersister
    {
        public int Count { get; private set; }

        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PersistAsync(InboundArticle article, CancellationToken cancellationToken)
        {
            Count++;
            Completed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPlacement : IArticlePlacementClient
    {
        public int Calls { get; private set; }

        public bool SawPersist { get; private set; }

        public bool FailIfCalled { get; init; }

        public ArticleRecord Last { get; private set; }

        public ArticlePlacementResult Result { get; init; } = new(ArticlePlacementKind.Accepted);

        public TaskCompletionSource Placed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ArticlePlacementResult> PlaceAsync(
            ArticleRecord record,
            StorageServerFleetEntry target,
            CancellationToken cancellationToken)
        {
            if (FailIfCalled)
            {
                throw new InvalidOperationException("Placement dialed without an eligible server.");
            }

            Calls++;
            SawPersist = true;
            Last = record;
            Placed.TrySetResult();
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class Link : IAsyncDisposable
    {
        private readonly Pipe _toServer = new();
        private readonly Pipe _toClient = new();

        public Link()
        {
            Client = new PipeStream(_toClient.Reader, _toServer.Writer);
            Server = new PipeStream(_toServer.Reader, _toClient.Writer);
        }

        public Stream Client { get; }

        public Stream Server { get; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private sealed class PipeStream(PipeReader reader, PipeWriter writer) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (result.Buffer.IsEmpty && result.IsCompleted)
            {
                return 0;
            }

            var length = (int)Math.Min(buffer.Length, result.Buffer.Length);
            var written = 0;
            foreach (var segment in result.Buffer.Slice(0, length))
            {
                segment.Span.CopyTo(buffer.Span[written..]);
                written += segment.Length;
            }

            reader.AdvanceTo(result.Buffer.GetPosition(length));
            return length;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var copied = buffer.ToArray();
            await writer.WriteAsync(copied, cancellationToken).ConfigureAwait(false);
            var flushed = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (flushed.IsCompleted)
            {
                throw new IOException("peer closed");
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await writer.CompleteAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static class ScriptedServer
    {
        public static async Task<bool> RunAsync(Stream stream, byte? result)
        {
            var pending = new List<byte>();
            var buffer = new byte[8192];
            var windows = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    return windows > 0 || result is null;
                }

                pending.AddRange(buffer.AsSpan(0, read).ToArray());
                while (pending.Count > 0)
                {
                    var parsed = VatpFrameParser.ParseOneFrame(pending.ToArray(), VatpProtocol.DefaultMaxFramePayload);
                    if (parsed.Status == VatpFrameParseStatus.Incomplete)
                    {
                        break;
                    }

                    if (parsed.Status != VatpFrameParseStatus.Success || parsed.Frame is not { } frame)
                    {
                        return false;
                    }

                    pending.RemoveRange(0, checked((int)parsed.ConsumedBytes));
                    if (frame.Header.Type == VatpFrameType.Hello)
                    {
                        var hello = VatpFrameEncoder.ToSingleBuffer(
                            VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload));
                        await stream.WriteAsync(hello);
                    }
                    else if (frame.Header.Type == VatpFrameType.Data && frame.Payload.Length > 0)
                    {
                        windows++;
                        var window = VatpFrameEncoder.ToSingleBuffer(
                            VatpFrameEncoder.EncodeWindow(frame.Header.StreamId, checked((uint)frame.Payload.Length)));
                        await stream.WriteAsync(window);
                    }
                    else if (frame.Header.Type == VatpFrameType.End)
                    {
                        if (result is byte outcome)
                        {
                            var encoded = VatpFrameEncoder.ToSingleBuffer(
                                VatpFrameEncoder.EncodeResult(frame.Header.StreamId, outcome));
                            await stream.WriteAsync(encoded);
                        }
                        else
                        {
                            await stream.DisposeAsync();
                        }

                        return true;
                    }
                }
            }
        }
    }
}
