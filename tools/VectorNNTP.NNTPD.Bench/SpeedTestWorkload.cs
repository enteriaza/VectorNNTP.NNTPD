using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Exercises the VectorNNTP <c>SPEEDTEST</c> extension over a real TCP session.
/// Independent of TAKETHIS/IHAVE/CHECK methodology.
/// </summary>
internal sealed class SpeedTestWorkload : IBenchmarkWorkload
{
    private static readonly ReadOnlyMemory<byte> CapabilitiesCommand = "CAPABILITIES\r\n"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> QuitCommand = "QUIT\r\n"u8.ToArray();

    public string Name => "SPEEDTEST";

    public Task<int> RunAsync(BenchOptions options) =>
        options.SpeedTestReceive switch
        {
            SpeedTestReceiveMode.Raw => RunRawAsync(options),
            SpeedTestReceiveMode.Line => RunLineAsync(options),
            _ => RunByteAsync(options),
        };

    private static async Task<int> RunLineAsync(BenchOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SpeedTestPeer))
        {
            Console.Error.WriteLine("SPEEDTEST requires --speedtest-peer <configured Transit identifier>.");
            return 2;
        }

        WritePreamble(options);

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        await socket.ConnectAsync(options.Host, options.PlainPort).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

        var greeting = await reader.ReadLineAsync().ConfigureAwait(false);
        Console.WriteLine($"greeting: {greeting}");

        await writer.WriteLineAsync("CAPABILITIES").ConfigureAwait(false);
        var capStatus = await reader.ReadLineAsync().ConfigureAwait(false);
        var advertised = false;
        if (capStatus is not null && capStatus.StartsWith("101", StringComparison.Ordinal))
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null || line == ".")
                {
                    break;
                }

                if (line.Equals("SPEEDTEST", StringComparison.OrdinalIgnoreCase))
                {
                    advertised = true;
                }
            }
        }

        if (!advertised)
        {
            Console.WriteLine("SPEEDTEST NOT SUPPORTED");
            return 3;
        }

        await writer.WriteLineAsync("SPEEDTEST " + options.SpeedTestPeer).ConfigureAwait(false);
        var ready = await reader.ReadLineAsync().ConfigureAwait(false);
        Console.WriteLine(ready);
        if (ready is null)
        {
            return 1;
        }

        if (!ready.StartsWith("290 ", StringComparison.Ordinal))
        {
            Console.WriteLine("SPEEDTEST did not start a measurement (no 290).");
            return 1;
        }

        long payloadBytes = 0;
        var payloadStarted = Stopwatch.GetTimestamp();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                Console.WriteLine("SPEEDTEST payload ended without terminator.");
                return 1;
            }

            if (line == ".")
            {
                break;
            }

            payloadBytes += Encoding.ASCII.GetByteCount(line) + 2;
        }

        var payloadElapsed = Stopwatch.GetElapsedTime(payloadStarted);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        var complete = await reader.ReadLineAsync().ConfigureAwait(false);
        Console.WriteLine(complete);
        if (complete is null || !complete.StartsWith("291 ", StringComparison.Ordinal))
        {
            Console.WriteLine("SPEEDTEST did not report a completed measurement.");
            return 1;
        }

        Console.WriteLine($"CLIENT_PAYLOAD_BYTES={payloadBytes}");
        WriteClientMetrics(SpeedTestReceiveMode.Line, payloadBytes, payloadElapsed);
        Console.WriteLine($"CLIENT_ALLOCATED_BYTES={allocated}");
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null || line == ".")
            {
                break;
            }

            Console.WriteLine(line);
        }

        await writer.WriteLineAsync("QUIT").ConfigureAwait(false);
        _ = await reader.ReadLineAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunByteAsync(BenchOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SpeedTestPeer))
        {
            Console.Error.WriteLine("SPEEDTEST requires --speedtest-peer <configured Transit identifier>.");
            return 2;
        }

        WritePreamble(options);
        Console.WriteLine(
            $"RECEIVE_MODE=BYTE buffer={SpeedTestBytePayloadReceiver.BufferBytes}");

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        await socket.ConnectAsync(options.Host, options.PlainPort).ConfigureAwait(false);
        var control = new SpeedTestSocketControlReader(socket);

        var greeting = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"greeting: {greeting}");

        await socket.SendAsync(CapabilitiesCommand, SocketFlags.None).ConfigureAwait(false);
        var capStatus = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
        var advertised = false;
        if (capStatus is not null && capStatus.StartsWith("101", StringComparison.Ordinal))
        {
            while (true)
            {
                var line = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                if (line is null || line == ".")
                {
                    break;
                }

                if (line.Equals("SPEEDTEST", StringComparison.OrdinalIgnoreCase))
                {
                    advertised = true;
                }
            }
        }

        if (!advertised)
        {
            Console.WriteLine("SPEEDTEST NOT SUPPORTED");
            return 3;
        }

        var command = Encoding.ASCII.GetBytes("SPEEDTEST " + options.SpeedTestPeer + "\r\n");
        await socket.SendAsync(command, SocketFlags.None).ConfigureAwait(false);
        var ready = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(ready);
        if (ready is null)
        {
            return 1;
        }

        if (!ready.StartsWith("290 ", StringComparison.Ordinal))
        {
            Console.WriteLine("SPEEDTEST did not start a measurement (no 290).");
            return 1;
        }

        var buffer = new byte[SpeedTestBytePayloadReceiver.BufferBytes];
        SpeedTestRawReceiveResult drain;
        long allocated;
        try
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            drain = await SpeedTestBytePayloadReceiver
                .DrainAsync(socket, buffer, control, CancellationToken.None)
                .ConfigureAwait(false);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        }
        catch (IOException ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }

        var complete = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(complete);
        if (complete is null || !complete.StartsWith("291 ", StringComparison.Ordinal))
        {
            Console.WriteLine("SPEEDTEST did not report a completed measurement.");
            return 1;
        }

        Console.WriteLine($"CLIENT_PAYLOAD_BYTES={drain.ReceivedBytes}");
        Console.WriteLine($"RECEIVED_BYTES={drain.ReceivedBytes}");
        WriteClientMetrics(SpeedTestReceiveMode.Byte, drain.ReceivedBytes, drain.Elapsed);
        Console.WriteLine($"BYTE_RECEIVE_CALLS={drain.ReceiveCalls}");
        Console.WriteLine($"BYTE_PREFIX_BYTES={drain.PrefixBytes}");
        Console.WriteLine($"CLIENT_ALLOCATED_BYTES={allocated}");
        while (true)
        {
            var line = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
            if (line is null || line == ".")
            {
                break;
            }

            Console.WriteLine(line);
        }

        await socket.SendAsync(QuitCommand, SocketFlags.None).ConfigureAwait(false);
        _ = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunRawAsync(BenchOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SpeedTestPeer))
        {
            Console.Error.WriteLine("SPEEDTEST requires --speedtest-peer <configured Transit identifier>.");
            return 2;
        }

        WritePreamble(options);
        Console.WriteLine(
            $"RECEIVE_MODE=RAW expectedBytes={options.SpeedTestBytes} buffer={SpeedTestRawPayloadReceiver.BufferBytes}");

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        await socket.ConnectAsync(options.Host, options.PlainPort).ConfigureAwait(false);
        var control = new SpeedTestSocketControlReader(socket);

        var greeting = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"greeting: {greeting}");

        await socket.SendAsync(CapabilitiesCommand, SocketFlags.None).ConfigureAwait(false);
        var capStatus = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
        var advertised = false;
        if (capStatus is not null && capStatus.StartsWith("101", StringComparison.Ordinal))
        {
            while (true)
            {
                var line = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                if (line is null || line == ".")
                {
                    break;
                }

                if (line.Equals("SPEEDTEST", StringComparison.OrdinalIgnoreCase))
                {
                    advertised = true;
                }
            }
        }

        if (!advertised)
        {
            Console.WriteLine("SPEEDTEST NOT SUPPORTED");
            return 3;
        }

        var command = Encoding.ASCII.GetBytes("SPEEDTEST " + options.SpeedTestPeer + "\r\n");
        await socket.SendAsync(command, SocketFlags.None).ConfigureAwait(false);
        var ready = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(ready);
        if (ready is null)
        {
            return 1;
        }

        if (!ready.StartsWith("290 ", StringComparison.Ordinal))
        {
            Console.WriteLine("SPEEDTEST did not start a measurement (no 290).");
            return 1;
        }

        var buffer = new byte[SpeedTestRawPayloadReceiver.BufferBytes];
        SpeedTestRawReceiveResult drain;
        try
        {
            drain = await SpeedTestRawPayloadReceiver
                .DrainAsync(socket, options.SpeedTestBytes, buffer, control, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }

        var terminator = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
        if (terminator != ".")
        {
            Console.WriteLine("SPEEDTEST payload ended without terminator.");
            return 1;
        }

        var complete = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(complete);
        if (complete is null || !complete.StartsWith("291 ", StringComparison.Ordinal))
        {
            Console.WriteLine("SPEEDTEST did not report a completed measurement.");
            return 1;
        }

        Console.WriteLine($"CLIENT_PAYLOAD_BYTES={drain.ReceivedBytes}");
        Console.WriteLine($"RECEIVED_BYTES={drain.ReceivedBytes}");
        WriteClientMetrics(SpeedTestReceiveMode.Raw, drain.ReceivedBytes, drain.Elapsed);
        Console.WriteLine($"RAW_RECEIVE_CALLS={drain.ReceiveCalls}");
        Console.WriteLine($"RAW_PREFIX_BYTES={drain.PrefixBytes}");
        while (true)
        {
            var line = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
            if (line is null || line == ".")
            {
                break;
            }

            Console.WriteLine(line);
        }

        await socket.SendAsync(QuitCommand, SocketFlags.None).ConfigureAwait(false);
        _ = await control.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static void WritePreamble(BenchOptions options)
    {
        Console.WriteLine("VectorNNTP.NNTPD.Bench — SPEEDTEST protocol client");
        Console.WriteLine($"Workload:            SPEEDTEST");
        Console.WriteLine(
            $"Host={options.Host} Port={options.PlainPort} Peer={options.SpeedTestPeer}");
        Console.WriteLine("Measures the current NNTP session TX path. Does not initiate outbound Transit.");
        Console.WriteLine();
    }

    private static void WriteClientMetrics(SpeedTestReceiveMode mode, long bytes, TimeSpan elapsed)
    {
        var receive = mode switch
        {
            SpeedTestReceiveMode.Raw => "RAW",
            SpeedTestReceiveMode.Line => "LINE",
            _ => "BYTE",
        };
        Console.WriteLine("RECEIVE_MODE=" + receive);
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"CLIENT_ELAPSED_MS={elapsed.TotalMilliseconds:F3}"));
        var seconds = elapsed.TotalSeconds;
        var gbit = bytes > 0 && seconds > 0
            ? bytes * 8d / seconds / 1_000_000_000d
            : 0;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"CLIENT_THROUGHPUT_GBIT={gbit:F6}"));
    }
}
