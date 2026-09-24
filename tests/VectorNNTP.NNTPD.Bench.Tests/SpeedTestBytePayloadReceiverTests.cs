using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VectorNNTP.NNTPD.Bench.Tests;

public sealed class SpeedTestBytePayloadReceiverTests
{
    [Fact]
    public void Parse_ByteReceive_IsExplicitAndDefault()
    {
        var implicitDefault = BenchOptions.Parse(
            ["--benchmark", "SPEEDTEST", "--speedtest-peer", "usenet-ninja"]);
        var explicitByte = BenchOptions.Parse(
        [
            "--benchmark", "SPEEDTEST",
            "--speedtest-peer", "usenet-ninja",
            "--speedtest-receive", "byte",
        ]);
        Assert.Equal(SpeedTestReceiveMode.Byte, implicitDefault.SpeedTestReceive);
        Assert.Equal(SpeedTestReceiveMode.Byte, explicitByte.SpeedTestReceive);
    }

    [Fact]
    public void Scanner_TerminatorInOneSpan_CountsPayloadOnly()
    {
        var scanner = new SpeedTestTerminatorScanner();
        var wire = "hello\r\n.\r\n291 leftover\r\n"u8;
        var consumed = scanner.Feed(wire);
        Assert.True(scanner.Completed);
        Assert.Equal(7, scanner.PayloadBytes);
        Assert.Equal(10, consumed);
    }

    [Fact]
    public void Scanner_EmptyPayload_LeadingDotCrlf()
    {
        var scanner = new SpeedTestTerminatorScanner();
        var consumed = scanner.Feed(".\r\n291\r\n"u8);
        Assert.True(scanner.Completed);
        Assert.Equal(0, scanner.PayloadBytes);
        Assert.Equal(3, consumed);
    }

    [Fact]
    public void Scanner_EmptyContentLineThenTerminator_CountsTwo()
    {
        var scanner = new SpeedTestTerminatorScanner();
        var consumed = scanner.Feed("\r\n.\r\n"u8);
        Assert.True(scanner.Completed);
        Assert.Equal(2, scanner.PayloadBytes);
        Assert.Equal(5, consumed);
    }

    [Fact]
    public void Scanner_PartialDelimiterIsPayload()
    {
        var scanner = new SpeedTestTerminatorScanner();
        var wire = "#\r\n.\rX\r\n#\r\n.\r\n"u8;
        var consumed = scanner.Feed(wire);
        Assert.True(scanner.Completed);
        Assert.Equal("#\r\n.\rX\r\n#\r\n"u8.Length, scanner.PayloadBytes);
        Assert.Equal(wire.Length, consumed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Scanner_TerminatorSplitAcrossFeeds(int firstBytes)
    {
        ReadOnlySpan<byte> wire = "abc\r\n.\r\n"u8;
        Assert.True(firstBytes < wire.Length);
        var scanner = new SpeedTestTerminatorScanner();
        Assert.Equal(-1, scanner.Feed(wire[..firstBytes]));
        Assert.False(scanner.Completed);
        var consumed = scanner.Feed(wire[firstBytes..]);
        Assert.True(scanner.Completed);
        Assert.Equal(5, scanner.PayloadBytes);
        Assert.Equal(wire.Length - firstBytes, consumed);
    }

    [Fact]
    public async Task Drain_TerminatorInOneReceive_CountsAndLeaves291()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        await pair.Server.SendAsync("hello\r\n.\r\n291 SPEEDTEST COMPLETE\r\n"u8.ToArray(), SocketFlags.None);

        var control = new SpeedTestSocketControlReader(pair.Client, bufferBytes: 256);
        var result = await SpeedTestBytePayloadReceiver.DrainAsync(
            pair.Client, new byte[256], control, CancellationToken.None);

        Assert.Equal(7, result.ReceivedBytes);
        Assert.Equal(1, result.ReceiveCalls);
        var complete = await control.ReadLineAsync(CancellationToken.None);
        Assert.Equal("291 SPEEDTEST COMPLETE", complete);
    }

    [Fact]
    public async Task Drain_TerminatorSplitAcrossTwoReceives()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        await pair.Server.SendAsync("hello\r\n."u8.ToArray(), SocketFlags.None);

        var drain = SpeedTestBytePayloadReceiver.DrainAsync(
            pair.Client, new byte[64], control: null, CancellationToken.None);
        await pair.Server.SendAsync("\r\n291 X\r\n"u8.ToArray(), SocketFlags.None);

        var result = await drain;
        Assert.Equal(7, result.ReceivedBytes);
        Assert.True(result.ReceiveCalls >= 2);
    }

    [Fact]
    public async Task Drain_OneByteBuffer_SplitsTerminatorAcrossManyReceives()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        await pair.Server.SendAsync("xy\r\n.\r\n"u8.ToArray(), SocketFlags.None);

        var result = await SpeedTestBytePayloadReceiver.DrainAsync(
            pair.Client, new byte[1], control: null, CancellationToken.None);

        Assert.Equal(4, result.ReceivedBytes);
        Assert.True(result.ReceiveCalls >= 7);
    }

    [Fact]
    public async Task Drain_PartialDelimiterThenRealTerminator()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        await pair.Server.SendAsync("#\r\n.\rX\r\n#\r\n.\r\n"u8.ToArray(), SocketFlags.None);

        var result = await SpeedTestBytePayloadReceiver.DrainAsync(
            pair.Client, new byte[8], control: null, CancellationToken.None);

        Assert.Equal("#\r\n.\rX\r\n#\r\n"u8.Length, result.ReceivedBytes);
    }

    [Fact]
    public async Task Drain_ControlLeftover_ScannedWithoutCopyingPayload()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        var wire = "290 SPEEDTEST x TX\r\n#0123\r\n.\r\n291 SPEEDTEST COMPLETE\r\n"u8.ToArray();
        await pair.Server.SendAsync(wire, SocketFlags.None);

        var control = new SpeedTestSocketControlReader(pair.Client, bufferBytes: 256);
        var ready = await control.ReadLineAsync(CancellationToken.None);
        Assert.Equal("290 SPEEDTEST x TX", ready);
        Assert.True(control.LeftoverBytes > 0);

        var result = await SpeedTestBytePayloadReceiver.DrainAsync(
            pair.Client, new byte[64], control, CancellationToken.None);

        Assert.Equal(7, result.ReceivedBytes);
        Assert.Equal(0, result.ReceiveCalls);
        Assert.Equal(7, result.PrefixBytes);
        var complete = await control.ReadLineAsync(CancellationToken.None);
        Assert.Equal("291 SPEEDTEST COMPLETE", complete);
    }

    [Fact]
    public async Task Drain_PrematureEof_Throws()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        await pair.Server.SendAsync("hello\r\n"u8.ToArray(), SocketFlags.None);
        pair.Server.Shutdown(SocketShutdown.Send);

        var ex = await Assert.ThrowsAsync<IOException>(() => SpeedTestBytePayloadReceiver
            .DrainAsync(pair.Client, new byte[64], control: null, CancellationToken.None));
        Assert.Contains("without terminator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drain_Canceled_Throws()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpeedTestBytePayloadReceiver
            .DrainAsync(pair.Client, new byte[64], control: null, cts.Token));
    }

    [Fact]
    public async Task Drain_DoesNotAllocatePerLine()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        var payload = new byte[(1024 * 8) + 3];
        for (var line = 0; line < 8; line++)
        {
            var offset = line * 1024;
            payload[offset] = (byte)'#';
            payload.AsSpan(offset + 1, 1021).Fill((byte)'0');
            payload[offset + 1022] = (byte)'\r';
            payload[offset + 1023] = (byte)'\n';
        }

        ".\r\n"u8.CopyTo(payload.AsSpan(1024 * 8));
        await pair.Server.SendAsync(payload, SocketFlags.None);

        var buffer = new byte[64 * 1024];
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = await SpeedTestBytePayloadReceiver.DrainAsync(
            pair.Client, buffer, control: null, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1024 * 8, result.ReceivedBytes);
        Assert.True(allocated < 4096, $"payload drain allocated {allocated} bytes");
    }

    [Fact]
    public void Result_DoesNotCarryPayloadBytes()
    {
        var result = new SpeedTestRawReceiveResult(64, TimeSpan.FromMilliseconds(1), 2, 0);
        Assert.Null(result.GetType().GetProperty("Payload"));
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
