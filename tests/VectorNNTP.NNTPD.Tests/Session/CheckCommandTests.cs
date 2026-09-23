using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Commands;

namespace VectorNNTP.NNTPD.Tests.Session;

/// <summary>
/// RFC 4644 CHECK stub authorization — must match TAKETHIS
/// (<see cref="NntpCommandAccess.RequiresTransit"/> / <see cref="NntpAuthorization.AuthorizedTransit"/>).
/// </summary>
public sealed class CheckCommandTests
{
    private static readonly IPAddress TransitPeer = IPAddress.Parse("198.18.0.70");
    private static readonly IPAddress OtherPeer = IPAddress.Parse("198.18.0.71");

    [Fact]
    public void Catalog_CheckAndTakeThis_ShareRequiresTransit()
    {
        var registry = DefaultNntpCommandCatalog.Create();
        Assert.True(NntpCommandParser.TryParse("CHECK <msg@example.com>", out var check));
        Assert.True(NntpCommandParser.TryParse("TAKETHIS <msg@example.com>", out var takeThis));
        Assert.True(registry.TryResolve(check, out var checkDescriptor, out var checkArgs, out var checkStatus));
        Assert.True(registry.TryResolve(takeThis, out var takeThisDescriptor, out _, out var takeThisStatus));
        Assert.Equal(NntpCommandResolveStatus.Found, checkStatus);
        Assert.Equal(NntpCommandResolveStatus.Found, takeThisStatus);
        Assert.NotNull(checkDescriptor);
        Assert.NotNull(takeThisDescriptor);
        Assert.Equal("CHECK", checkDescriptor.RegistryKey);
        Assert.Equal("TAKETHIS", takeThisDescriptor.RegistryKey);
        Assert.Equal(NntpCommandAccess.RequiresTransit, checkDescriptor.Access);
        Assert.Equal(takeThisDescriptor.Access, checkDescriptor.Access);
        Assert.Equal(["<msg@example.com>"], checkArgs);
    }

    [Fact]
    public async Task TransitPeer_Check_Returns238_WithoutUserAuthentication()
    {
        var peers = TransitPeerAuthorization.FromAddresses([TransitPeer]);
        await using var duplex = await CheckDuplex.CreateAsync(TransitPeer);
        var session = duplex.CreateSession(peers);
        Assert.False(session.Authorization.IsAuthenticated);
        Assert.True(session.Authorization.AuthorizedTransit);
        Assert.Equal(NntpAuthorization.TrustedTransitPeer.IsAuthenticated, session.Authorization.IsAuthenticated);
        Assert.Equal(NntpAuthorization.TrustedTransitPeer.AuthorizedTransit, session.Authorization.AuthorizedTransit);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        const string id = "<i.am.an.article.you.will.want@example.com>";
        await duplex.WriteClientLineAsync("CHECK " + id);
        Assert.Equal("238 " + id + " send article to be transferred", await duplex.ReadClientLineAsync());
        Assert.False(session.Authorization.IsAuthenticated);
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TransitPeer_CheckDoesNotRequireModeStream()
    {
        var peers = TransitPeerAuthorization.FromAddresses([TransitPeer]);
        await using var duplex = await CheckDuplex.CreateAsync(TransitPeer);
        var session = duplex.CreateSession(peers);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);
        await duplex.WriteClientLineAsync("CHECK <want@example.com>");
        Assert.Equal(
            "238 <want@example.com> send article to be transferred",
            await duplex.ReadClientLineAsync());
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TransitPeer_CheckThenTakeThis_SameAuthorization()
    {
        var peers = TransitPeerAuthorization.FromAddresses([TransitPeer]);
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var duplex = await CheckDuplex.CreateAsync(TransitPeer);
        var session = duplex.CreateSession(peers, queue);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <x@ex.com>");
        Assert.Equal("238 <x@ex.com> send article to be transferred", await duplex.ReadClientLineAsync());

        await duplex.WriteClientAsync("TAKETHIS <x@ex.com>\r\nSubject: t\r\n\r\nbody\r\n.\r\n");
        Assert.Equal("239 <x@ex.com>", await duplex.ReadClientLineAsync());
        Assert.Equal(1, queue.Count);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task NonAuthorizedPeer_CheckAndTakeThis_BothReturn480()
    {
        var peers = TransitPeerAuthorization.FromAddresses([TransitPeer]);
        await using var duplex = await CheckDuplex.CreateAsync(OtherPeer);
        var session = duplex.CreateSession(peers);
        Assert.False(session.Authorization.IsAuthenticated);
        Assert.False(session.Authorization.AuthorizedTransit);

        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <msg@example.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("TAKETHIS <msg@example.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task UnauthenticatedDefault_CheckAndTakeThis_BothReturn480()
    {
        await using var duplex = await CheckDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <msg@example.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("TAKETHIS <x@ex.com>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task AuthenticatedWithoutTransit_CheckAndTakeThis_BothReturn502()
    {
        await using var duplex = await CheckDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: false,
            postingPermitted: false,
            streamingPermitted: false));
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK <msg@example.com>");
        Assert.Equal("502 Permission denied", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("TAKETHIS <msg@example.com>");
        Assert.Equal("502 Permission denied", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task TransitPeer_MissingMessageId_Returns501AfterAuthorization()
    {
        var peers = TransitPeerAuthorization.FromAddresses([TransitPeer]);
        await using var duplex = await CheckDuplex.CreateAsync(TransitPeer);
        var session = duplex.CreateSession(peers);
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK");
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public async Task NonAuthorizedPeer_MissingMessageId_IsRejectedBeforeSyntax()
    {
        await using var duplex = await CheckDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync();

        await duplex.WriteClientLineAsync("CHECK");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    private sealed class CheckDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());
        private readonly IPAddress _clientAddress;

        private CheckDuplex(IPAddress clientAddress) => _clientAddress = clientAddress;

        public static Task<CheckDuplex> CreateAsync(IPAddress? clientAddress = null) =>
            Task.FromResult(new CheckDuplex(clientAddress ?? IPAddress.Loopback));

        public NntpSession CreateSession(
            ITransitPeerAuthorization? peers = null,
            IArticleIngestionQueue? queue = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new IPEndPoint(_clientAddress, 40000)));
            return new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                articleIngestion: queue,
                transitPeerAuthorization: peers);
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

    private sealed class PipeNntpConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();

        public PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
        {
            Input = input;
            Output = output;
            ClientIdentity = identity;
        }

        public PipeReader Input { get; }
        public PipeWriter Output { get; }
        public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public EndPoint? LocalEndPoint => null;
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
