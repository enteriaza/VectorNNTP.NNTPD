using System.Net;
using System.Net.Sockets;
using System.Text;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Bench.Tests;

public sealed class SpeedTestRawPayloadReceiverTests
{
    [Fact]
    public void Parse_DefaultReceiveIsByte_AndDefaultBytesMatchServer()
    {
        var options = BenchOptions.Parse(
            ["--benchmark", "SPEEDTEST", "--speedtest-peer", "usenet-ninja"]);
        Assert.Equal(SpeedTestReceiveMode.Byte, options.SpeedTestReceive);
        Assert.Equal(SpeedTestOptions.DefaultMaxBytes, options.SpeedTestBytes);
    }

    [Fact]
    public void Parse_LineReceive_RemainsDiagnosticOptIn()
    {
        var options = BenchOptions.Parse(
        [
            "--benchmark", "SPEEDTEST",
            "--speedtest-peer", "usenet-ninja",
            "--speedtest-receive", "line",
        ]);
        Assert.Equal(SpeedTestReceiveMode.Line, options.SpeedTestReceive);
    }

    [Fact]
    public void Parse_RawReceive_IsOptIn()
    {
        var options = BenchOptions.Parse(
        [
            "--benchmark", "SPEEDTEST",
            "--speedtest-peer", "usenet-ninja",
            "--speedtest-receive", "raw",
            "--speedtest-bytes", "4096",
        ]);
        Assert.Equal(SpeedTestReceiveMode.Raw, options.SpeedTestReceive);
        Assert.Equal(4096, options.SpeedTestBytes);
    }

    [Fact]
    public void Parse_UnknownReceiveMode_Throws()
    {
        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(
        [
            "--benchmark", "SPEEDTEST",
            "--speedtest-peer", "usenet-ninja",
            "--speedtest-receive", "stream",
        ]));
    }

    [Fact]
    public void Parse_NonPositiveBytes_Throws()
    {
        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(
        [
            "--benchmark", "SPEEDTEST",
            "--speedtest-peer", "usenet-ninja",
            "--speedtest-bytes", "0",
        ]));
    }

    [Fact]
    public void Parse_RawFlag_DoesNotChangeTakeThis()
    {
        var takeThis = BenchOptions.Parse(["--benchmark", "TAKETHIS"]);
        Assert.Equal(SpeedTestReceiveMode.Byte, takeThis.SpeedTestReceive);
        Assert.Equal(string.Empty, takeThis.SpeedTestPeer);
    }

    [Fact]
    public async Task Drain_ExactPayloadLength_OneReceive()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        var payload = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        await pair.Server.SendAsync(payload, SocketFlags.None);

        var buffer = new byte[256];
        var result = await SpeedTestRawPayloadReceiver.DrainAsync(
            pair.Client, payload.Length, buffer, control: null, CancellationToken.None);

        Assert.Equal(payload.Length, result.ReceivedBytes);
        Assert.Equal(1, result.ReceiveCalls);
        Assert.Equal(0, result.PrefixBytes);
    }

    [Fact]
    public async Task Drain_MultipleReceiveAsyncCompletions()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        var payload = new byte[100];
        payload.AsSpan().Fill(0x5A);
        const int chunk = 7;
        for (var offset = 0; offset < payload.Length; offset += chunk)
        {
            var n = Math.Min(chunk, payload.Length - offset);
            await pair.Server.SendAsync(payload.AsMemory(offset, n), SocketFlags.None);
        }

        var buffer = new byte[16];
        var result = await SpeedTestRawPayloadReceiver.DrainAsync(
            pair.Client, payload.Length, buffer, control: null, CancellationToken.None);

        Assert.Equal(payload.Length, result.ReceivedBytes);
        Assert.True(result.ReceiveCalls >= 7, "small buffer must require multiple ReceiveAsync calls");
    }

    [Fact]
    public async Task Drain_ExactFinalReceive_UsesRemainingSlice()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        await pair.Server.SendAsync(new byte[10], SocketFlags.None);
        await pair.Server.SendAsync(new byte[3], SocketFlags.None);

        var buffer = new byte[8];
        var result = await SpeedTestRawPayloadReceiver.DrainAsync(
            pair.Client, 13, buffer, control: null, CancellationToken.None);

        Assert.Equal(13, result.ReceivedBytes);
        Assert.Equal(2, result.ReceiveCalls);
    }

    [Fact]
    public async Task Drain_RemoteCloseBeforeExpected_Throws()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        await pair.Server.SendAsync(new byte[4], SocketFlags.None);
        pair.Server.Shutdown(SocketShutdown.Send);

        var buffer = new byte[64];
        var ex = await Assert.ThrowsAsync<IOException>(() => SpeedTestRawPayloadReceiver
            .DrainAsync(pair.Client, 32, buffer, control: null, CancellationToken.None));
        Assert.Contains("closed before expected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drain_Canceled_Throws()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var buffer = new byte[64];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpeedTestRawPayloadReceiver
            .DrainAsync(pair.Client, 32, buffer, control: null, cts.Token));
    }

    [Fact]
    public async Task Drain_ControlLeftover_CountsWithoutCopyingPayload()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        var payload = new byte[50];
        payload.AsSpan().Fill(0x23);
        var wire = new byte[20 + payload.Length];
        Encoding.ASCII.GetBytes("290 SPEEDTEST x TX\r\n").CopyTo(wire, 0);
        payload.CopyTo(wire, 20);
        await pair.Server.SendAsync(wire, SocketFlags.None);

        var control = new SpeedTestSocketControlReader(pair.Client, bufferBytes: 256);
        var ready = await control.ReadLineAsync(CancellationToken.None);
        Assert.Equal("290 SPEEDTEST x TX", ready);
        Assert.True(control.LeftoverBytes > 0);

        var buffer = new byte[64];
        var result = await SpeedTestRawPayloadReceiver.DrainAsync(
            pair.Client, payload.Length, buffer, control, CancellationToken.None);

        Assert.Equal(payload.Length, result.ReceivedBytes);
        Assert.Equal(payload.Length, result.PrefixBytes);
        Assert.Equal(0, result.ReceiveCalls);
        Assert.Equal(0, control.LeftoverBytes);
    }

    [Fact]
    public void Result_DoesNotCarryPayloadBytes()
    {
        var result = new SpeedTestRawReceiveResult(64, TimeSpan.FromMilliseconds(1), 2, 0);
        Assert.Equal(typeof(long), result.ReceivedBytes.GetType());
        Assert.Equal(64, result.ReceivedBytes);
        Assert.True(result.GbitPerSecond > 0);
        Assert.Null(result.GetType().GetProperty("Payload"));
        Assert.Null(result.GetType().GetProperty("Bytes"));
    }

    private sealed class ConnectedPair : IAsyncDisposable
    {
        private ConnectedPair(Socket client, Socket server, TcpListener listener)
        {
            Client = client;
            Server = server;
            _listener = listener;
        }

        public Socket Client { get; }
        public Socket Server { get; }
        private readonly TcpListener _listener;

        public static async Task<ConnectedPair> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            var server = await listener.AcceptSocketAsync().ConfigureAwait(false);
            await connect.ConfigureAwait(false);
            return new ConnectedPair(client, server, listener);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Server.Dispose();
            _listener.Stop();
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
