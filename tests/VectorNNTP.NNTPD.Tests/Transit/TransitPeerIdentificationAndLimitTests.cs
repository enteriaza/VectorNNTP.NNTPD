using System.Net;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Transit;

namespace VectorNNTP.NNTPD.Tests.Transit;

public sealed class TransitPeerIdentificationAndLimitTests
{
    [Fact]
    public void Identify_MatchingSource_ReturnsNamedPeerPolicy()
    {
        var snapshot = new TransitConfigurationSnapshot(
            new Dictionary<string, TransitPeerPolicy>(StringComparer.Ordinal)
            {
                [TransitTestPeers.DefaultPeerName] = TransitConfigurationSnapshot.CreatePeer(
                    TransitTestPeers.DefaultPeerName,
                    TransitTestPeers.Peer(
                        maxIncoming: 3,
                        username: "feed",
                        password: "secret",
                        patterns: "comp.*,!comp.sources.*",
                        ssl: "TLS",
                        allowFrom: ["192.0.2.10"])),
            });
        var authz = TransitPeerAuthorization.CreateStatic(snapshot).Resolve(IPAddress.Parse("192.0.2.10"));
        Assert.True(authz.AuthorizedTransit);
        Assert.Equal(TransitTestPeers.DefaultPeerName, authz.TransitPeerName);
        Assert.NotNull(authz.TransitPeerPolicy);
        Assert.Equal(TransitTestPeers.DefaultPeerName, authz.TransitPeerPolicy.Identifier);
        Assert.Equal(TransitTestPeers.DefaultPeerDisplayName, authz.TransitPeerPolicy.PeerName);
        Assert.Equal(3, authz.TransitPeerPolicy.MaxIncomingConnections);
        Assert.True(authz.TransitPeerPolicy.HasPeerCredentials);
        Assert.Equal(TransitSslMode.Tls, authz.TransitPeerPolicy.Ssl);
        Assert.True(authz.TransitPeerPolicy.DeferOnDuplicate);
        Assert.Equal(string.Empty, authz.TransitPeerPolicy.PathToken);
        Assert.Equal(TransitPeerOptions.DefaultMaxSize, authz.TransitPeerPolicy.MaxSize);
        Assert.Equal(TransitMessageTypes.Default, authz.TransitPeerPolicy.MessageTypes);
        Assert.True(authz.TransitPeerPolicy.Patterns.MatchesNewsgroup("comp.lang.c"));
        Assert.False(authz.TransitPeerPolicy.Patterns.MatchesNewsgroup("comp.sources.unix"));
    }

    [Fact]
    public void Identify_NonMatchingSource_IsNotTransit()
    {
        var authz = TransitPeerAuthorization.CreateStatic(
            TransitTestPeers.Snapshot(
                TransitTestPeers.DefaultPeerName,
                TransitTestPeers.Peer(allowFrom: ["192.0.2.10"])));
        var result = authz.Resolve(IPAddress.Parse("198.51.100.1"));
        Assert.False(result.AuthorizedTransit);
        Assert.Null(result.TransitPeerName);
        Assert.Null(result.TransitPeerPolicy);
    }

    [Fact]
    public void Identify_AmbiguousRuntimeOverlap_DeniesTransit()
    {
        // Validation would reject literal overlap; construct a snapshot that simulates
        // two peers matching the same address without going through the validator.
        var snapshot = new TransitConfigurationSnapshot(new Dictionary<string, TransitPeerPolicy>(StringComparer.Ordinal)
        {
            ["one"] = TransitConfigurationSnapshot.CreatePeer("one", TransitTestPeers.Peer(allowFrom: ["192.0.2.0/24"])),
            ["two"] = TransitConfigurationSnapshot.CreatePeer("two", TransitTestPeers.Peer(allowFrom: ["192.0.2.0/24"])),
        });
        var result = TransitPeerAuthorization.CreateStatic(snapshot).Resolve(IPAddress.Parse("192.0.2.10"));
        Assert.False(result.AuthorizedTransit);
        Assert.Null(result.TransitPeerName);
    }

    [Fact]
    public void Limit_BelowAtAndOver_IdentifyPeerFromSourceIp()
    {
        var source = IPAddress.Parse("192.0.2.10");
        var store = CreatePeerStore(source, maxIncoming: 2);
        var limiter = new TransitInboundConnectionLimiter(store);
        var peers = TransitPeerAuthorization.CreateForStore(store);

        var first = CreateSession(peers, source);
        Assert.Equal(TransitTestPeers.DefaultPeerName, first.Authorization.TransitPeerName);
        Assert.True(TransitConnectionAdmission.TryAdmit(limiter, first, out var lease1));

        var second = CreateSession(peers, source);
        Assert.True(TransitConnectionAdmission.TryAdmit(limiter, second, out var lease2));
        Assert.Equal(2, limiter.GetCount(TransitTestPeers.DefaultPeerName));

        var over = CreateSession(peers, source);
        Assert.False(TransitConnectionAdmission.TryAdmit(limiter, over, out var lease3));
        Assert.False(lease3.IsHeld);

        lease1.Dispose();
        Assert.Equal(1, limiter.GetCount(TransitTestPeers.DefaultPeerName));
        var reused = CreateSession(peers, source);
        Assert.True(TransitConnectionAdmission.TryAdmit(limiter, reused, out var lease4));
        lease2.Dispose();
        lease4.Dispose();
        Assert.Equal(0, limiter.GetCount(TransitTestPeers.DefaultPeerName));
    }

    [Fact]
    public void Limit_AmbiguousMatch_DoesNotConsumeSlot()
    {
        var source = IPAddress.Parse("192.0.2.10");
        var store = new TransitConfigurationStore();
        store.Replace(
            new TransitConfigurationSnapshot(
                new Dictionary<string, TransitPeerPolicy>(StringComparer.Ordinal)
                {
                    ["one"] = TransitConfigurationSnapshot.CreatePeer(
                        "one",
                        TransitTestPeers.Peer(maxIncoming: 1, allowFrom: ["192.0.2.0/24"])),
                    ["two"] = TransitConfigurationSnapshot.CreatePeer(
                        "two",
                        TransitTestPeers.Peer(maxIncoming: 1, allowFrom: ["192.0.2.0/24"])),
                }));
        var limiter = new TransitInboundConnectionLimiter(store);
        var peers = TransitPeerAuthorization.CreateForStore(store);
        var session = CreateSession(peers, source);
        Assert.Null(session.Authorization.TransitPeerName);
        Assert.False(session.Authorization.AuthorizedTransit);
        Assert.True(TransitConnectionAdmission.TryAdmit(limiter, session, out var lease));
        Assert.False(lease.IsHeld);
        Assert.Equal(0, limiter.GetCount("one"));
        Assert.Equal(0, limiter.GetCount("two"));
    }

    [Fact]
    public void Limit_NonMatchingSource_AdmittedWithoutSlot()
    {
        var source = IPAddress.Parse("192.0.2.10");
        var store = CreatePeerStore(source, maxIncoming: 1);
        var limiter = new TransitInboundConnectionLimiter(store);
        var peers = TransitPeerAuthorization.CreateForStore(store);
        var other = CreateSession(peers, IPAddress.Parse("198.51.100.1"));
        Assert.Null(other.Authorization.TransitPeerName);
        Assert.True(TransitConnectionAdmission.TryAdmit(limiter, other, out var lease));
        Assert.False(lease.IsHeld);
        Assert.Equal(0, limiter.GetCount(TransitTestPeers.DefaultPeerName));
    }

    [Fact]
    public void Limit_RemovedPeer_NewConnectionIsNotIdentified()
    {
        var source = IPAddress.Parse("192.0.2.10");
        var store = CreatePeerStore(source, maxIncoming: 1);
        var limiter = new TransitInboundConnectionLimiter(store);
        var peers = TransitPeerAuthorization.CreateForStore(store);
        var existing = CreateSession(peers, source);
        Assert.True(TransitConnectionAdmission.TryAdmit(limiter, existing, out var lease));
        Assert.Equal(1, limiter.GetCount(TransitTestPeers.DefaultPeerName));

        store.Replace(TransitConfigurationSnapshot.Empty);
        Assert.Equal(TransitTestPeers.DefaultPeerName, existing.Authorization.TransitPeerName);
        var fresh = CreateSession(peers, source);
        Assert.Null(fresh.Authorization.TransitPeerName);
        Assert.True(TransitConnectionAdmission.TryAdmit(limiter, fresh, out var uncounted));
        Assert.False(uncounted.IsHeld);
        Assert.Equal(1, limiter.GetCount(TransitTestPeers.DefaultPeerName));
        lease.Dispose();
        Assert.Equal(0, limiter.GetCount(TransitTestPeers.DefaultPeerName));
    }

    [Fact]
    public void Limit_LoweredOnReload_AffectsNewConnectionsOnly()
    {
        var source = IPAddress.Parse("192.0.2.10");
        var store = CreatePeerStore(source, maxIncoming: 2);
        var limiter = new TransitInboundConnectionLimiter(store);
        var peers = TransitPeerAuthorization.CreateForStore(store);
        var existing = CreateSession(peers, source);
        Assert.True(TransitConnectionAdmission.TryAdmit(limiter, existing, out var lease));

        store.Replace(
            TransitTestPeers.Snapshot(
                TransitTestPeers.DefaultPeerName,
                TransitTestPeers.Peer(maxIncoming: 1, allowFrom: [source.ToString()])));

        Assert.Equal(2, existing.Authorization.TransitPeerPolicy!.MaxIncomingConnections);
        var blocked = CreateSession(peers, source);
        Assert.Equal(1, blocked.Authorization.TransitPeerPolicy!.MaxIncomingConnections);
        Assert.False(TransitConnectionAdmission.TryAdmit(limiter, blocked, out _));
        Assert.Equal(1, limiter.GetCount(TransitTestPeers.DefaultPeerName));
        lease.Dispose();
        var next = CreateSession(peers, source);
        Assert.True(TransitConnectionAdmission.TryAdmit(limiter, next, out var reused));
        reused.Dispose();
    }

    [Fact]
    public async Task Limit_ConcurrentBoundary_DoesNotOverAdmit()
    {
        var source = IPAddress.Parse("192.0.2.10");
        var store = CreatePeerStore(source, maxIncoming: 8);
        var limiter = new TransitInboundConnectionLimiter(store);
        var peers = TransitPeerAuthorization.CreateForStore(store);
        var leases = new TransitInboundConnectionLease[32];
        var admitted = 0;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(async i =>
        {
            await Task.Yield();
            var session = CreateSession(peers, source);
            Assert.Equal(TransitTestPeers.DefaultPeerName, session.Authorization.TransitPeerName);
            if (TransitConnectionAdmission.TryAdmit(limiter, session, out var lease))
            {
                Interlocked.Increment(ref admitted);
                leases[i] = lease;
            }
        }));
        Assert.Equal(8, admitted);
        Assert.Equal(8, limiter.GetCount(TransitTestPeers.DefaultPeerName));
        foreach (var lease in leases)
        {
            lease.Dispose();
        }

        Assert.Equal(0, limiter.GetCount(TransitTestPeers.DefaultPeerName));
    }

    [Fact]
    public async Task Admission_Writes400WhenLimitExceeded()
    {
        var source = IPAddress.Parse("192.0.2.10");
        var store = CreatePeerStore(source, maxIncoming: 0);
        var limiter = new TransitInboundConnectionLimiter(store);
        var peers = TransitPeerAuthorization.CreateForStore(store);
        var input = new System.IO.Pipelines.Pipe();
        var output = new System.IO.Pipelines.Pipe();
        var connection = new LimitPipeConnection(input.Reader, output.Writer, source);
        var session = new NntpSession(
            connection,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NntpSession>.Instance,
            transitPeerAuthorization: peers);
        Assert.Equal(TransitTestPeers.DefaultPeerName, session.Authorization.TransitPeerName);
        Assert.False(TransitConnectionAdmission.TryAdmit(limiter, session, out _));
        await TransitConnectionAdmission.WriteUnavailableAsync(connection, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var line = await VectorNNTP.NNTPD.Session.CommandProcessor.NntpCommandLineReader.ReadLineAsync(output.Reader, cts.Token);
        Assert.Equal("400 Service temporarily unavailable", line);
    }

    [Fact]
    public void Limit_CancellationStyleRelease_ViaDispose()
    {
        var source = IPAddress.Parse("192.0.2.10");
        var store = CreatePeerStore(source, maxIncoming: 1);
        var limiter = new TransitInboundConnectionLimiter(store);
        var peers = TransitPeerAuthorization.CreateForStore(store);
        var first = CreateSession(peers, source);
        Assert.True(TransitConnectionAdmission.TryAdmit(limiter, first, out var lease));
        using (lease)
        {
            var blocked = CreateSession(peers, source);
            Assert.False(TransitConnectionAdmission.TryAdmit(limiter, blocked, out _));
        }

        var after = CreateSession(peers, source);
        Assert.True(TransitConnectionAdmission.TryAdmit(limiter, after, out var reused));
        reused.Dispose();
    }

    private static TransitConfigurationStore CreatePeerStore(IPAddress source, int maxIncoming)
    {
        var store = new TransitConfigurationStore();
        store.Replace(
            TransitTestPeers.Snapshot(
                TransitTestPeers.DefaultPeerName,
                TransitTestPeers.Peer(maxIncoming: maxIncoming, allowFrom: [source.ToString()])));
        return store;
    }

    private static NntpSession CreateSession(ITransitPeerAuthorization peers, IPAddress source)
    {
        var input = new System.IO.Pipelines.Pipe();
        var output = new System.IO.Pipelines.Pipe();
        var connection = new LimitPipeConnection(input.Reader, output.Writer, source);
        return new NntpSession(
            connection,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NntpSession>.Instance,
            transitPeerAuthorization: peers);
    }

    private sealed class LimitPipeConnection(System.IO.Pipelines.PipeReader input, System.IO.Pipelines.PipeWriter output, IPAddress address) : INntpConnection
    {
        private readonly CancellationTokenSource _closed = new();

        public System.IO.Pipelines.PipeReader Input { get; } = input;
        public System.IO.Pipelines.PipeWriter Output { get; } = output;
        public ConnectionClientIdentity ClientIdentity { get; } = ConnectionClientIdentity.Direct(new IPEndPoint(address, 40000));
        public EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;
        public EndPoint? LocalEndPoint => null;
        public bool IsTls => false;
        public bool IsCompressed => false;
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

        public Task WaitForOutboundDeliveryAsync(long outboundIdleVersionBeforeFlush, CancellationToken cancellationToken = default) =>
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

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipherSuite)
        {
            tlsVersion = string.Empty;
            cipherSuite = string.Empty;
            return false;
        }
    }
}
