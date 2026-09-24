using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.History;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Commands;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.Session;

public sealed class IHaveCommandTests
{
    private static NntpAuthorization TransitAuth { get; } = new(
        isAuthenticated: true,
        authorizedReader: false,
        authorizedTransit: true,
        postingPermitted: false,
        streamingPermitted: true);

    [Fact]
    public async Task ValidIhave_AcceptsArticle_EnqueuesWire_AndReturns235()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <want@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync("Subject: hi\r\n\r\n..body\r\n.\r\n");
        Assert.Equal("235 Article transferred OK", await duplex.ReadClientLineAsync());

        using var dequeueCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        Assert.NotNull(inbound);
        Assert.Equal("<want@example.com>", inbound!.MessageId);
        Assert.Equal(InboundArticleProducer.IHave, inbound.Producer);
        Assert.Null(inbound.Structured);
        Assert.Equal("Subject: hi\r\n\r\n..body\r\n", Encoding.ASCII.GetString(inbound.Payload.Span));

        var interpreted = IhaveArticleInterpreter.Interpret(inbound, 64 * 1024);
        Assert.NotNull(interpreted.Structured);
        Assert.Equal(".body\r\n", Encoding.ASCII.GetString(interpreted.Structured!.Value.Body.Span));
        Assert.Equal(interpreted.Structured.Value.Size, interpreted.Payload.Length);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task HistoryHit_Returns435_AndDoesNotReadArticleBytes()
    {
        var redis = new FakeRedisService();
        var history = new HistoryDb(
            redis,
            TimeSpan.FromHours(2),
            NullLogger<HistoryDb>.Instance,
            TimeProvider.System,
            new HistoryWriteQueue());
        history.Remember("<have@example.com>"u8.ToArray());
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 2 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <have@example.com>");
        Assert.Equal("435 Article not wanted", await duplex.ReadClientLineAsync());
        await duplex.WriteClientLineAsync("DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task RedisUnavailable_Returns436()
    {
        var redis = new FakeRedisService { IsUnavailable = true };
        var history = new HistoryDb(
            redis,
            TimeSpan.FromHours(2),
            NullLogger<HistoryDb>.Instance,
            TimeProvider.System,
            new HistoryWriteQueue());
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 2 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue, history);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <later@example.com>");
        Assert.Equal("436 Transfer not possible; try again later", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task MalformedMessageId_Is501()
    {
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(new ArticleIngestionQueue(new ArticleIngestionOptions()));
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE not-an-id");
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task QueueUnavailable_Returns436AfterArticle()
    {
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(DisabledArticleIngestionQueue.Instance);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <q@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync("Subject: x\r\n\r\ny\r\n.\r\n");
        Assert.Equal("436 Transfer failed; try again later", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TooLarge_Returns437_AndLeavesNextCommand()
    {
        var queue = new ArticleIngestionQueue(
            new ArticleIngestionOptions { QueueCapacity = 2, MaxArticleBytes = 16 });
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(queue);
        session.SetAuthorization(TransitAuth);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("IHAVE <big@example.com>");
        Assert.Equal("335 Send article to be transferred", await duplex.ReadClientLineAsync());
        await duplex.WriteClientAsync("Subject: big\r\n\r\n" + new string('Z', 64) + "\r\n.\r\nDATE\r\n");
        Assert.Equal("437 Transfer rejected; do not retry", await duplex.ReadClientLineAsync());
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task Capabilities_AdvertisesIhave()
    {
        await using var duplex = await IHaveDuplex.CreateAsync();
        var session = duplex.CreateSession(new ArticleIngestionQueue(new ArticleIngestionOptions()));
        session.SetAuthorization(TransitAuth);
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

        Assert.Contains("IHAVE", lines);
        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    private sealed class IHaveDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();

        public static Task<IHaveDuplex> CreateAsync() => Task.FromResult(new IHaveDuplex());

        public NntpSession CreateSession(IArticleIngestionQueue queue, IHistoryDb? history = null)
        {
            var connection = new PipeConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                historyDb: history);
        }

        public async Task WriteClientLineAsync(string line)
        {
            await _clientToServer.Writer.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
            await _clientToServer.Writer.FlushAsync();
        }

        public async Task WriteClientAsync(string payload)
        {
            await _clientToServer.Writer.WriteAsync(Encoding.ASCII.GetBytes(payload));
            await _clientToServer.Writer.FlushAsync();
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

    private sealed class PipeConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();

        public PipeConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
        {
            Input = input;
            Output = output;
            ClientIdentity = identity;
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }

        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

        public System.Net.EndPoint? LocalEndPoint => null;

        public ConnectionClientIdentity ClientIdentity { get; }

        public bool IsTls => false;

        public bool IsCompressed => false;

        public CancellationToken ConnectionClosed => _cts.Token;

        public bool IsCompleted => _cts.IsCancellationRequested;

        public long OutboundIdleVersion => 0;

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CompleteAsync(Exception? exception = null)
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
