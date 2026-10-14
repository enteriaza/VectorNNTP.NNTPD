using VectorNNTP.TftpSmoke.Benchmark;
using VectorNNTP.TftpSmoke.Transfer;
using Xunit;

namespace VectorNNTP.TftpSmoke.Tests;

public sealed class BenchmarkAccountingTests
{
    [Theory]
    [InlineData(0, 512, 1)]
    [InlineData(512, 512, 2)]
    [InlineData(1024, 512, 3)]
    [InlineData(1000, 512, 2)]
    [InlineData(768000, 1428, 538)]
    [InlineData(768000, 512, 1501)]
    [InlineData(768000, 8192, 94)]
    public void MinimumDataDatagrams_IncludesTheTerminatingBlock(int payload, int blockSize, int expected)
    {
        Assert.Equal(expected, BenchmarkMath.MinimumDataDatagrams(payload, blockSize));
    }

    [Fact]
    public void MinimumAcknowledgements_CoversEveryWindowOnce()
    {
        Assert.Equal(34, BenchmarkMath.MinimumAcknowledgements(538, 16));
        Assert.Equal(17, BenchmarkMath.MinimumAcknowledgements(538, 32));
        Assert.Equal(6, BenchmarkMath.MinimumAcknowledgements(94, 16));
        Assert.Equal(1501, BenchmarkMath.MinimumAcknowledgements(1501, 1));
    }

    [Fact]
    public void OnlySuccessfulTransfersCountTowardPayload()
    {
        Assert.True(BenchmarkMath.CountsTowardPayload(TftpTransferStatus.Succeeded));
        Assert.False(BenchmarkMath.CountsTowardPayload(TftpTransferStatus.Failed));
        Assert.False(BenchmarkMath.CountsTowardPayload(TftpTransferStatus.Cancelled));
    }

    [Fact]
    public void DiagnoseAndMemoryStore_ParseAsMeasurementModes()
    {
        var parsed = CommandLine.Parse(["benchmark", "--diagnose", "--store", "none", "--transfers", "1"], TextWriter.Null);
        Assert.Equal(0, parsed.ExitCode);
        Assert.NotNull(parsed.Benchmark);
        Assert.True(parsed.Benchmark.Diagnose);
        Assert.False(parsed.Benchmark.StoreFiles);
    }
}
