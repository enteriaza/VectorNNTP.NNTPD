using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using VectorNNTP.StandaloneTxPoc;
using Xunit;

namespace VectorNNTP.StandaloneTxPoc.Tests;

public sealed class StandaloneTxPocTests
{
    [Fact]
    public void Parse_Defaults_AreSpeedTestEndpoint()
    {
        var options = PocOptions.Parse([]);
        Assert.Equal("198.18.0.66", options.Host);
        Assert.Equal(1199, options.Port);
        Assert.Equal("all", options.Mode);
        Assert.Equal(5, options.Runs);
        Assert.Equal(64L * 1024 * 1024, options.TotalBytes);
        Assert.False(options.ListenOnly);
    }

    [Fact]
    public void Parse_RejectsUnknownMode()
    {
        Assert.Throws<ArgumentException>(() => PocOptions.Parse(["--mode", "takethis"]));
    }

    [Fact]
    public void ModesFor_All_IsThreeModes()
    {
        Assert.Equal(["raw", "pipe", "production-loop"], PocOptions.ModesFor("all"));
        Assert.Equal(["production-loop"], PocOptions.ModesFor("production-loop"));
    }

    [Fact]
    public void PipeOptions_MatchProductionTx()
    {
        var options = TxModes.CreateProductionTxPipeOptions();
        Assert.Same(PipeScheduler.Inline, options.ReaderScheduler);
        Assert.Same(PipeScheduler.Inline, options.WriterScheduler);
        Assert.Equal(64 * 1024, options.PauseWriterThreshold);
        Assert.Equal(32 * 1024, options.ResumeWriterThreshold);
        Assert.Equal(4 * 1024, options.MinimumSegmentSize);
        Assert.False(options.UseSynchronizationContext);
    }

    [Fact]
    public void Median_UsesMiddleOfOddSeries()
    {
        Assert.Equal(3, RunStats.Median([5, 1, 3]));
        Assert.Equal(3, RunStats.Mean([2, 3, 4]));
        var (min, max) = RunStats.MinMax([7, 2, 5]);
        Assert.Equal(2, min);
        Assert.Equal(7, max);
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("pipe")]
    [InlineData("production-loop")]
    public async Task Mode_Loopback_WritesExactBytes(string mode)
    {
        var address = IPAddress.Loopback;
        await using var listener = DrainListener.Start(address, 0);
        var port = listener.LocalEndPoint.Port;
        var chunk = new byte[64 * 1024];
        Random.Shared.NextBytes(chunk);
        const long total = 256 * 1024;

        using var client = new TcpClient();
        await client.ConnectAsync(address, port);
        await using var stream = client.GetStream();
        var result = mode switch
        {
            "raw" => await TxModes.RunRawAsync(stream, chunk, total, CancellationToken.None),
            "pipe" => await TxModes.RunPipeAsync(stream, chunk, total, CancellationToken.None),
            _ => await TxModes.RunProductionLoopAsync(stream, chunk, total, CancellationToken.None),
        };

        Assert.Equal(total, result.Bytes);
        Assert.Equal(4, result.WriteAsyncCount);
        Assert.Equal(64 * 1024, result.AverageWriteBytes);
        Assert.Equal(result.SyncWrites + result.AsyncWrites, result.WriteAsyncCount);
        Assert.True(result.ElapsedMs >= 0);
        Assert.True(result.GbitPerSecond >= 0);
        var snap = SocketSnapshot.Capture(client.Client);
        Assert.Equal(SocketType.Stream, snap.SocketType);
        Assert.Equal(ProtocolType.Tcp, snap.ProtocolType);
        Assert.False(string.IsNullOrWhiteSpace(snap.LocalEndPoint));
        Assert.False(string.IsNullOrWhiteSpace(snap.RemoteEndPoint));
    }
}
