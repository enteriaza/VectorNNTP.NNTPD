using System.Diagnostics;
using System.Net.Sockets;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Diagnostic SPEEDTEST payload drain: reusable buffer, discard, no line parsing.
/// </summary>
internal static class SpeedTestRawPayloadReceiver
{
    public const int BufferBytes = 64 * 1024;

    public static async Task<SpeedTestRawReceiveResult> DrainAsync(
        Socket socket,
        long expectedBytes,
        Memory<byte> buffer,
        SpeedTestSocketControlReader? control,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedBytes);
        if (buffer.Length == 0)
        {
            throw new ArgumentException("Raw receive buffer must be non-empty.", nameof(buffer));
        }

        var started = Stopwatch.GetTimestamp();
        var received = control?.ConsumeLeftover(expectedBytes) ?? 0;
        var prefixBytes = received;
        var receiveCalls = 0;

        while (received < expectedBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = expectedBytes - received;
            var slice = buffer[..(int)Math.Min(buffer.Length, remaining)];
            var n = await socket.ReceiveAsync(slice, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            receiveCalls++;
            if (n == 0)
            {
                throw new IOException(
                    "SPEEDTEST raw receive: connection closed before expected payload bytes arrived.");
            }

            received += n;
        }

        return new SpeedTestRawReceiveResult(
            received,
            Stopwatch.GetElapsedTime(started),
            receiveCalls,
            prefixBytes);
    }
}
