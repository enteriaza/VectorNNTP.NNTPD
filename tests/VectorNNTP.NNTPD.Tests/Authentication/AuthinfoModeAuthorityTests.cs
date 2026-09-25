using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.Authentication;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Authentication;
using VectorNNTP.NNTPD.Session.CommandProcessor;
using VectorNNTP.NNTPD.Tests.Session;
using VectorNNTP.NNTPD.Tests.TestDoubles;
using VectorNNTP.NNTPD.Tests.Transit;
using VectorNNTP.NNTPD.Transit;

namespace VectorNNTP.NNTPD.Tests.Authentication;

/// <summary>
/// AUTHINFO authority is MODE, not source IP. Transit and MySQL never fall through to each other.
/// </summary>
[Collection(nameof(NntpCommandLoggerCollection))]
public sealed class AuthinfoModeAuthorityTests
{
    private static readonly IPAddress LabClient = IPAddress.Parse("198.18.0.70");
    private static readonly IPAddress OtherClient = IPAddress.Parse("198.51.100.1");

    [Fact]
    public async Task Reader_NormalClient_ValidMysql_Succeeds()
    {
        await using var harness = await ModeAuthHarness.CreateAsync(OtherClient, mysqlUser: "alice", mysqlPassword: "secret");
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("MODE READER");
        Assert.StartsWith("201 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.AuthenticateAsync("alice", "secret");

        Assert.True(session.Authentication.IsAuthenticated);
        Assert.Equal("alice", session.Authentication.Username);
        Assert.True(session.Authorization.AuthorizedReader);
        Assert.True(session.Authorization.PostingPermitted);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.Null(session.Authorization.TransitPeerName);
        Assert.Equal(1, harness.Reader.AuthenticateCount);
        Assert.Equal(0, harness.Transit.AuthenticateCount);

        await harness.QuitAsync(run);
    }

    [Fact]
    public async Task Reader_AllowFromMatch_ValidMysql_UsesMysqlNotTransit()
    {
        await using var harness = await ModeAuthHarness.CreateAsync(
            LabClient,
            mysqlUser: "a",
            mysqlPassword: "a",
            transitUsername: "a",
            transitPassword: "a",
            allowFromCidr: "198.18.0.0/15");
        var session = harness.CreateSession();
        Assert.NotNull(session.Authorization.TransitPeerPolicy);
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("MODE READER");
        Assert.StartsWith("201 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.Equal(NntpAuthenticationAuthority.Reader, session.AuthenticationAuthority);
        await harness.AuthenticateAsync("a", "a");

        Assert.True(session.Authentication.IsAuthenticated);
        Assert.Equal("a", session.Authentication.Username);
        Assert.True(session.Authorization.AuthorizedReader);
        Assert.True(session.Authorization.PostingPermitted);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.Null(session.Authorization.TransitPeerName);
        Assert.Equal(1, harness.Reader.AuthenticateCount);
        Assert.Equal(0, harness.Transit.AuthenticateCount);

        await harness.QuitAsync(run);
    }

    [Fact]
    public async Task Reader_AllowFromMatch_InvalidMysql_FailsWithoutTransit()
    {
        await using var harness = await ModeAuthHarness.CreateAsync(
            LabClient,
            mysqlUser: "a",
            mysqlPassword: "a",
            transitUsername: "a",
            transitPassword: "a",
            allowFromCidr: "198.18.0.0/15");
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("MODE READER");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO USER a");
        Assert.StartsWith("381 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.WriteClientLineAsync("AUTHINFO PASS wrong");
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());

        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Equal("a", session.PendingAuthUsername);
        Assert.True(session.Authorization.AuthorizedTransit);
        Assert.Equal(1, harness.Reader.AuthenticateCount);
        Assert.Equal(0, harness.Transit.AuthenticateCount);

        await harness.QuitAsync(run);
    }

    [Fact]
    public async Task Stream_ValidTransit_SucceedsWithoutMysql()
    {
        await using var harness = await ModeAuthHarness.CreateAsync(
            LabClient,
            mysqlUser: "a",
            mysqlPassword: "a",
            transitUsername: "feed",
            transitPassword: "peer-secret",
            allowFromCidr: "198.18.0.0/15");
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("MODE STREAM");
        Assert.Equal("203 Streaming permitted", await harness.ReadClientLineAsync());
        Assert.Equal(NntpSessionMode.Unspecified, session.Mode);
        Assert.Equal(NntpAuthenticationAuthority.Transit, session.AuthenticationAuthority);
        await harness.WriteClientLineAsync("AUTHINFO USER feed");
        Assert.StartsWith("381 ", await harness.ReadClientLineAsync(), StringComparison.Ordinal);
        await harness.WriteClientLineAsync("AUTHINFO PASS peer-secret");
        Assert.Equal("281 Authentication accepted", await harness.ReadClientLineAsync());

        Assert.True(session.Authentication.IsAuthenticated);
        Assert.Equal("feed", session.Authentication.Username);
        Assert.True(session.Authorization.AuthorizedTransit);
        Assert.True(session.Authorization.StreamingPermitted);
        Assert.False(session.Authorization.AuthorizedReader);
        Assert.False(session.Authorization.PostingPermitted);
        Assert.Equal("usenet-ninja", session.Authorization.TransitPeerName);
        Assert.Equal(0, harness.Reader.AuthenticateCount);
        Assert.Equal(1, harness.Transit.AuthenticateCount);

        await harness.QuitAsync(run);
    }

    [Fact]
    public async Task Stream_InvalidTransit_ValidMysql_FailsWithoutMysql()
    {
        await using var harness = await ModeAuthHarness.CreateAsync(
            LabClient,
            mysqlUser: "a",
            mysqlPassword: "a",
            transitUsername: "feed",
            transitPassword: "peer-secret",
            allowFromCidr: "198.18.0.0/15");
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("MODE STREAM");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO USER a");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO PASS a");
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());

        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Null(session.Authentication.Username);
        Assert.True(session.Authorization.AuthorizedTransit);
        Assert.False(session.Authorization.AuthorizedReader);
        Assert.Equal("a", session.PendingAuthUsername);
        Assert.Equal(0, harness.Reader.AuthenticateCount);
        Assert.Equal(1, harness.Transit.AuthenticateCount);

        await harness.QuitAsync(run);
    }

    [Fact]
    public async Task Stream_InvalidTransit_Generic481_NoReaderIdentity()
    {
        await using var harness = await ModeAuthHarness.CreateAsync(
            LabClient,
            mysqlUser: "a",
            mysqlPassword: "a",
            transitUsername: "feed",
            transitPassword: "peer-secret",
            allowFromCidr: "198.18.0.0/15");
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("MODE STREAM");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO USER feed");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO PASS wrong");
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());

        Assert.False(session.Authentication.IsAuthenticated);
        Assert.Null(session.AccountPolicy);
        Assert.False(session.Authorization.AuthorizedReader);
        Assert.False(session.Authorization.PostingPermitted);
        Assert.True(session.Authorization.AuthorizedTransit);
        Assert.Equal(0, harness.Reader.AuthenticateCount);

        await harness.QuitAsync(run);
    }

    [Fact]
    public async Task Unspecified_AllowFromMatch_UsesMysqlNotTransit()
    {
        await using var harness = await ModeAuthHarness.CreateAsync(
            LabClient,
            mysqlUser: "a",
            mysqlPassword: "a",
            transitUsername: "feed",
            transitPassword: "peer-secret",
            allowFromCidr: "198.18.0.0/15");
        var session = harness.CreateSession();
        Assert.Equal(NntpAuthenticationAuthority.Reader, session.AuthenticationAuthority);
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.AuthenticateAsync("a", "a");
        Assert.True(session.Authorization.AuthorizedReader);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.Equal(1, harness.Reader.AuthenticateCount);
        Assert.Equal(0, harness.Transit.AuthenticateCount);

        await harness.QuitAsync(run);
    }

    [Fact]
    public async Task Stream_NewsmasterCredentials_DoNotAuthenticate()
    {
        await using var harness = await ModeAuthHarness.CreateAsync(
            LabClient,
            mysqlUser: "a",
            mysqlPassword: "a",
            transitUsername: "feed",
            transitPassword: "peer-secret",
            allowFromCidr: "198.18.0.0/15",
            newsmasterUser: "news",
            newsmasterPassword: "master-secret");
        var session = harness.CreateSession();
        var run = session.RunAsync();
        await harness.ReadGreetingAsync();

        await harness.WriteClientLineAsync("MODE STREAM");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO USER news");
        await harness.ReadClientLineAsync();
        await harness.WriteClientLineAsync("AUTHINFO PASS master-secret");
        Assert.Equal("481 Authentication failed", await harness.ReadClientLineAsync());
        Assert.False(session.Authorization.ControlCancelPermitted);
        Assert.Equal(0, harness.Reader.AuthenticateCount);
        Assert.Equal(1, harness.Transit.AuthenticateCount);

        await harness.QuitAsync(run);
    }
}

internal sealed class ModeAuthHarness : IAsyncDisposable
{
    private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
    private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());
    private readonly INntpAuthenticationProvider _provider;
    private readonly ITransitPeerAuthorization _peers;
    private readonly IPAddress _clientIp;

    private ModeAuthHarness(
        INntpAuthenticationProvider provider,
        RecordingNntpAuthenticationProvider reader,
        RecordingTransitPeerAuthenticator transit,
        ITransitPeerAuthorization peers,
        IPAddress clientIp)
    {
        _provider = provider;
        Reader = reader;
        Transit = transit;
        _peers = peers;
        _clientIp = clientIp;
    }

    public RecordingNntpAuthenticationProvider Reader { get; }

    public RecordingTransitPeerAuthenticator Transit { get; }

    public static Task<ModeAuthHarness> CreateAsync(
        IPAddress clientIp,
        string mysqlUser,
        string mysqlPassword,
        string transitUsername = "",
        string transitPassword = "",
        string? allowFromCidr = null,
        string? newsmasterUser = null,
        string? newsmasterPassword = null)
    {
        var store = new MemoryNntpUserRecordStore();
        store.Add(MemoryNntpUserRecordStore.Create(mysqlUser, mysqlPassword));
        var validator = new MySqlNntpCredentialValidator(store, NullLogger<MySqlNntpCredentialValidator>.Instance);
        INntpAuthenticationProvider inner = new CompositeNntpAuthenticationProvider(
            newsmasterUser is null
                ? DenyAllNntpAuthenticationProvider.Instance
                : NewsmasterNntpAuthenticationProvider.Create(new VectorNNTP.NNTPD.Configuration.NntpdOptions
                {
                    NewsmasterUser = newsmasterUser,
                    NewsmasterPassword = newsmasterPassword ?? string.Empty,
                }),
            newsmasterUser,
            validator);
        var reader = new RecordingNntpAuthenticationProvider(inner);
        var transit = new RecordingTransitPeerAuthenticator();
        var peers = TransitPeerAuthorization.CreateStatic(
            TransitTestPeers.Snapshot(
                "usenet-ninja",
                TransitTestPeers.Peer(
                    allowFrom: [allowFromCidr ?? clientIp.ToString()],
                    username: transitUsername,
                    password: transitPassword)));
        return Task.FromResult(new ModeAuthHarness(reader, reader, transit, peers, clientIp));
    }

    public NntpSession CreateSession()
    {
        var connection = new PipeNntpConnection(
            _clientToServer.Reader,
            _serverToClient.Writer,
            ConnectionClientIdentity.Direct(new IPEndPoint(_clientIp, 119)));
        return new NntpSession(
            connection,
            NullLogger<NntpSession>.Instance,
            authenticationProvider: _provider,
            transitPeerAuthorization: _peers,
            transitAuthenticator: Transit);
    }

    public async Task AuthenticateAsync(string username, string password)
    {
        await WriteClientLineAsync($"AUTHINFO USER {username}");
        Assert.StartsWith("381 ", await ReadClientLineAsync(), StringComparison.Ordinal);
        await WriteClientLineAsync($"AUTHINFO PASS {password}");
        Assert.StartsWith("281 ", await ReadClientLineAsync(), StringComparison.Ordinal);
    }

    public async Task ReadGreetingAsync()
    {
        var line = await ReadClientLineAsync();
        Assert.StartsWith("20", line, StringComparison.Ordinal);
    }

    public async Task WriteClientLineAsync(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
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

    public async Task QuitAsync(Task run)
    {
        await WriteClientLineAsync("QUIT");
        await ReadClientLineAsync();
        await run;
    }

    public async ValueTask DisposeAsync()
    {
        await _clientToServer.Writer.CompleteAsync();
        await _clientToServer.Reader.CompleteAsync();
        await _serverToClient.Writer.CompleteAsync();
        await _serverToClient.Reader.CompleteAsync();
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

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public bool IsCompressed => false;

        public CancellationToken ConnectionClosed => _cts.Token;

        public bool IsCompleted => _cts.IsCancellationRequested;

        public long OutboundIdleVersion => 0;

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
