using System.Net;
using System.Net.Sockets;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.PostFilter;

namespace VectorNNTP.NNTPD.Tests.PostFilter;

public sealed class SpamdTransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SequentialChecks_ReuseOneConnection()
    {
        await using var server = await FakeSpamd.StartAsync(keepOpen: true);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var target = Target(server.Port, maxConnections: 1);
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, (await CheckAsync(client, target)).Status);
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, (await CheckAsync(client, target)).Status);
        Assert.Equal(2, server.Checks);
        Assert.Equal(1, server.Connections);
        Assert.Equal(1, metrics.ConnectionsEstablished);
        Assert.Equal(2, metrics.CheckRequests);
        Assert.Equal(1, metrics.ConnectionReuses);
    }

    [Fact]
    public async Task BrokenConnection_IsEvictedAndReplaced()
    {
        await using var server = await FakeSpamd.StartAsync(keepOpen: false);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var target = Target(server.Port, maxConnections: 1);
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, (await CheckAsync(client, target)).Status);
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, (await CheckAsync(client, target)).Status);
        Assert.Equal(2, server.Checks);
        Assert.Equal(2, server.Connections);
        Assert.Equal(2, metrics.ConnectionsEstablished);
        Assert.True(metrics.Evictions >= 1);
    }

    [Fact]
    public async Task ProtocolError_DoesNotReuseConnection()
    {
        await using var server = await FakeSpamd.StartAsync(keepOpen: true, response: "NOPE\r\n\r\n"u8.ToArray());
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var result = await CheckAsync(client, Target(server.Port, maxConnections: 1));
        Assert.Equal(PostFilterSpamAssassinStatus.Failed, result.Status);
        Assert.Contains("malformed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, metrics.Evictions);
    }

    [Fact]
    public async Task Timeout_IsClassifiedAndEvicts()
    {
        await using var server = await FakeSpamd.StartAsync(keepOpen: true, hang: true);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var target = Target(server.Port, maxConnections: 1, operation: TimeSpan.FromMilliseconds(200));
        var result = await CheckAsync(client, target);
        Assert.Equal(PostFilterSpamAssassinStatus.Failed, result.Status);
        Assert.Equal("timeout", result.Detail);
        Assert.True(metrics.Evictions >= 1);
    }

    [Fact]
    public async Task Cancellation_IsRethrown()
    {
        await using var server = await FakeSpamd.StartAsync(keepOpen: true, hang: true);
        await using var client = new SpamdCheckClient();
        using var cts = new CancellationTokenSource();
        var check = CheckAsync(client, Target(server.Port, operation: TimeSpan.FromSeconds(5)), cts.Token);
        await server.Accepted;
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await check);
    }

    [Fact]
    public void HostSelection_RoundRobinRotates_FailoverStaysAtZero()
    {
        var rr = 0;
        Assert.Equal(0, SpamdHostSelector.NextStart(PostFilterSpamAssassinHostSelection.RoundRobin, 2, ref rr));
        Assert.Equal(1, SpamdHostSelector.NextStart(PostFilterSpamAssassinHostSelection.RoundRobin, 2, ref rr));
        Assert.Equal(0, SpamdHostSelector.NextStart(PostFilterSpamAssassinHostSelection.RoundRobin, 2, ref rr));
        var failover = 99;
        Assert.Equal(0, SpamdHostSelector.NextStart(PostFilterSpamAssassinHostSelection.Failover, 3, ref failover));
        Assert.Equal(0, SpamdHostSelector.NextStart(PostFilterSpamAssassinHostSelection.Failover, 3, ref failover));
        Assert.Equal(99, failover);
    }

    [Fact]
    public async Task Failover_SkipsUnreachableHost()
    {
        await using var good = await FakeSpamd.StartAsync(keepOpen: true);
        await using var client = new SpamdCheckClient();
        var result = await client.CheckAsync(
            Article(),
            "poster",
            new PostFilterSpamAssassinTarget(
                ["203.0.113.1", "127.0.0.1"],
                good.Port,
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromSeconds(3),
                "1.5",
                1,
                PostFilterSpamAssassinHostSelection.Failover),
            ScanContext());
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, result.Status);
        Assert.Equal(1, good.Checks);
    }

    [Fact]
    public async Task ConcurrentChecks_HonorPoolSize()
    {
        await using var server = await FakeSpamd.StartAsync(keepOpen: true, delay: TimeSpan.FromMilliseconds(80));
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var target = Target(server.Port, maxConnections: 2);
        var tasks = Enumerable.Range(0, 4).Select(_ => CheckAsync(client, target).AsTask()).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, result => Assert.Equal(PostFilterSpamAssassinStatus.Ham, result.Status));
        Assert.Equal(4, metrics.CheckRequests);
        Assert.True(server.PeakConnections <= 2);
        Assert.True(metrics.ConnectionsEstablished <= 2);
    }

    [Fact]
    public async Task DisposeAsync_RejectsFurtherChecks()
    {
        await using var client = new SpamdCheckClient();
        await client.DisposeAsync();
        var result = await CheckAsync(client, Target(9));
        Assert.Equal(PostFilterSpamAssassinStatus.Failed, result.Status);
        Assert.Equal("disposed", result.Detail);
    }

    [Fact]
    public async Task LeftoverBytes_AreProtocolFailure()
    {
        var extra = "SPAMD/1.1 0 EX_OK\r\nSpam: False ; 0.0 / 5.0\r\n\r\nEXTRA"u8.ToArray();
        await using var server = await FakeSpamd.StartAsync(keepOpen: true, response: extra);
        await using var client = new SpamdCheckClient();
        var result = await CheckAsync(client, Target(server.Port));
        Assert.Equal(PostFilterSpamAssassinStatus.Failed, result.Status);
        Assert.Equal("leftover", result.Detail);
    }

    private static ValueTask<PostFilterSpamAssassinResult> CheckAsync(
        SpamdCheckClient client,
        PostFilterSpamAssassinTarget target,
        CancellationToken cancellationToken = default) =>
        client.CheckAsync(Article(), "poster", target, ScanContext(), cancellationToken);

    private static PostFilterSpamAssassinTarget Target(
        int port,
        int maxConnections = 4,
        TimeSpan? operation = null) =>
        new(
            ["127.0.0.1"],
            port,
            TimeSpan.FromSeconds(2),
            operation ?? TimeSpan.FromSeconds(5),
            "1.5",
            maxConnections,
            PostFilterSpamAssassinHostSelection.RoundRobin);

    private static SpamdScanContext ScanContext() =>
        new(IPAddress.Loopback, "nntpd01.usenet.ninja", Now);

    private static ArticleRecord Article()
    {
        var bytes = "From: a@b\r\nNewsgroups: misc.test\r\nSubject: t\r\nMessage-ID: <h@example.com>\r\nDate: 1 Jan 2026 00:00:00 +0000\r\n\r\nbody\r\n"u8.ToArray();
        return new ArticleRecord(
            ArticleId.FromMessageId("<h@example.com>"u8),
            artHash: 1,
            artType: ArticleType.Default,
            artLines: 1,
            canonicalUtc: Now.UtcDateTime,
            parseStatus: ArticleParseStatus.CanonicalV1,
            artData: bytes,
            fields: default);
    }

    private sealed class FakeSpamd : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _accept;
        private readonly TaskCompletionSource _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connections;
        private int _checks;
        private int _inflight;
        private int _peak;

        private FakeSpamd(TcpListener listener, bool keepOpen, bool hang, TimeSpan delay, byte[]? response)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _accept = AcceptLoopAsync(keepOpen, hang, delay, response);
        }

        public int Port { get; }

        public int Connections => Volatile.Read(ref _connections);

        public int Checks => Volatile.Read(ref _checks);

        public int PeakConnections => Volatile.Read(ref _peak);

        public Task Accepted => _accepted.Task;

        public static async Task<FakeSpamd> StartAsync(
            bool keepOpen,
            bool hang = false,
            TimeSpan delay = default,
            byte[]? response = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var server = new FakeSpamd(listener, keepOpen, hang, delay, response);
            await Task.Yield();
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _accept.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync(bool keepOpen, bool hang, TimeSpan delay, byte[]? response)
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                _accepted.TrySetResult();
                _ = Task.Run(() => ServeAsync(client, keepOpen, hang, delay, response), _cts.Token);
            }
        }

        private async Task ServeAsync(TcpClient client, bool keepOpen, bool hang, TimeSpan delay, byte[]? response)
        {
            var inflight = Interlocked.Increment(ref _inflight);
            UpdatePeak(inflight);
            try
            {
                await using var stream = client.GetStream();
                do
                {
                    if (hang)
                    {
                        await Task.Delay(Timeout.Infinite, _cts.Token).ConfigureAwait(false);
                    }

                    if (!await TryReadCheckAsync(stream).ConfigureAwait(false))
                    {
                        return;
                    }

                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                    }

                    Interlocked.Increment(ref _checks);
                    var bytes = response ?? "SPAMD/1.1 0 EX_OK\r\nSpam: False ; 0.0 / 5.0\r\n\r\n"u8.ToArray();
                    await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                    await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
                while (keepOpen);
            }
            catch (Exception)
            {
            }
            finally
            {
                Interlocked.Decrement(ref _inflight);
                client.Dispose();
            }
        }

        private void UpdatePeak(int inflight)
        {
            while (true)
            {
                var peak = Volatile.Read(ref _peak);
                if (inflight <= peak || Interlocked.CompareExchange(ref _peak, inflight, peak) == peak)
                {
                    return;
                }
            }
        }

        private async Task<bool> TryReadCheckAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), _cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return total > 0 && buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0;
                }

                total += read;
                var headers = buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8);
                if (headers < 0)
                {
                    continue;
                }

                var headerText = Encoding.ASCII.GetString(buffer.AsSpan(0, headers));
                var length = 0;
                foreach (var line in headerText.Split("\r\n"))
                {
                    if (line.StartsWith("Content-length:", StringComparison.OrdinalIgnoreCase))
                    {
                        length = int.Parse(line["Content-length:".Length..].Trim());
                    }
                }

                var bodyStart = headers + 4;
                while (total - bodyStart < length)
                {
                    var readBody = await stream.ReadAsync(buffer.AsMemory(total), _cts.Token).ConfigureAwait(false);
                    if (readBody == 0)
                    {
                        return false;
                    }

                    total += readBody;
                }

                return true;
            }

            return false;
        }
    }
}
