using VectorNNTP.TftpSmoke.Transfer;

namespace VectorNNTP.TftpSmoke.Benchmark;

/// <summary>Payload accounting shared by the benchmark and its tests.</summary>
public static class BenchmarkMath
{
    /// <summary>
    /// DATA datagrams required by RFC 1350 for <paramref name="payloadBytes"/> at <paramref name="blockSize"/>.
    /// An exact multiple of the block size includes the zero-length terminating block. An empty payload is one zero-length block.
    /// </summary>
    public static int MinimumDataDatagrams(int payloadBytes, int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
        return (payloadBytes / blockSize) + 1;
    }

    /// <summary>ACKs required when every window, including the short final window, is acknowledged once and nothing is lost.</summary>
    public static int MinimumAcknowledgements(int dataDatagrams, int windowSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dataDatagrams, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);
        return (dataDatagrams + windowSize - 1) / windowSize;
    }

    /// <summary>Only a transfer that reached the final ACK contributes its payload bytes to throughput and latency.</summary>
    public static bool CountsTowardPayload(TftpTransferStatus status) => status == TftpTransferStatus.Succeeded;
}
