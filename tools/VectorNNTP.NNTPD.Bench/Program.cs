using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Real-TCP benchmark client for VectorNNTP.NNTPD BENCHIT transport measurement.
/// </summary>
internal static class Program
{
    private const int DefaultArticleBytes = 750 * 1024;
    private static int WarmupSeconds = 5;
    private static int MeasureSeconds = 60;
    private const int ReceiveBufferSize = 1024 * 1024;
    private const int SendBufferSize = 256 * 1024;

    private static readonly string[] Modes = ["plain", "deflate", "tls", "tls+deflate"];
    private static readonly int[] Concurrencies = [1, 10, 50];

    public static async Task<int> Main(string[] args)
    {
        var options = BenchOptions.Parse(args);
        WarmupSeconds = options.WarmupSeconds;
        MeasureSeconds = options.MeasureSeconds;
        Console.WriteLine("VectorNNTP.NNTPD.Bench — real-TCP BENCHIT harness");
        Console.WriteLine(
            $"Host={options.Host} PlainPort={options.PlainPort} TlsPort={options.TlsPort} " +
            $"Warmup={WarmupSeconds}s Measure={MeasureSeconds}s Runs={options.Runs}");
        Console.WriteLine($"ArticleBytes(expected)={DefaultArticleBytes} ServerPid={options.ServerPid?.ToString() ?? "(none)"}");
        Console.WriteLine();

        if (options.IperfOnly)
        {
            return RunIperfBaseline(options) ? 0 : 1;
        }

        var results = new List<ScenarioResult>();
        foreach (var mode in Modes)
        {
            if (options.ModeFilter is not null &&
                !string.Equals(options.ModeFilter, mode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var connections in Concurrencies)
            {
                if (options.ConnectionsFilter is int only && only != connections)
                {
                    continue;
                }

                for (var run = 1; run <= options.Runs; run++)
                {
                    Console.WriteLine($"=== {mode} × {connections} conn × run {run}/{options.Runs} ===");
                    var result = await RunScenarioAsync(options, mode, connections, run).ConfigureAwait(false);
                    results.Add(result);
                    PrintScenario(result);
                    Console.WriteLine();
                }
            }
        }

        PrintSummaryTable(results);
        return 0;
    }

    private static async Task<ScenarioResult> RunScenarioAsync(
        BenchOptions options,
        string mode,
        int connections,
        int run)
    {
        var useTls = mode.Contains("tls", StringComparison.Ordinal);
        var useDeflate = mode.Contains("deflate", StringComparison.Ordinal);
        var port = useTls ? options.TlsPort : options.PlainPort;

        using var measureCts = new CancellationTokenSource();
        var workers = new BenchWorker[connections];
        var ready = new Task[connections];

        for (var i = 0; i < connections; i++)
        {
            workers[i] = new BenchWorker(i);
            ready[i] = workers[i].ConnectAndNegotiateAsync(
                options.Host,
                port,
                useTls,
                useDeflate,
                options.TlsHostName,
                measureCts.Token);
        }

        await Task.WhenAll(ready).ConfigureAwait(false);

        var expectedWire = workers[0].ValidatedWireBytes
            ?? throw new InvalidOperationException("First connection failed to validate BENCHIT wire size.");
        var expectedArticle = workers[0].ValidatedArticleBytes ?? DefaultArticleBytes;

        ProcessSampler? sampler = null;
        if (options.ServerPid is int pid)
        {
            sampler = ProcessSampler.TryStart(pid);
        }

        // Warm-up (excluded from reported metrics). Finish in-flight responses for framing alignment.
        var warmTasks = workers
            .Select(w => w.RunAsync(TimeSpan.FromSeconds(WarmupSeconds), record: false, measureCts.Token))
            .ToArray();
        await Task.WhenAll(warmTasks).ConfigureAwait(false);

        foreach (var w in workers)
        {
            w.ResetCounters();
        }

        sampler?.MarkMeasureStart();
        var sw = Stopwatch.StartNew();
        var runTasks = workers
            .Select(w => w.RunAsync(TimeSpan.FromSeconds(MeasureSeconds), record: true, measureCts.Token))
            .ToArray();
        await Task.WhenAll(runTasks).ConfigureAwait(false);

        sw.Stop();
        var sample = sampler?.MarkMeasureEnd();

        long requests = 0;
        long logical = 0;
        long wireRead = 0;
        long wireWrite = 0;
        var latency = new LatencyHistogram();
        string? tlsVersion = null;
        string? cipher = null;

        foreach (var w in workers)
        {
            requests += w.Requests;
            logical += w.LogicalBytes;
            wireRead += w.WireBytesRead;
            wireWrite += w.WireBytesWritten;
            latency.Merge(w.Latency);
            tlsVersion ??= w.TlsVersion;
            cipher ??= w.Cipher;
            await w.DisposeAsync().ConfigureAwait(false);
        }

        var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        var wireTotal = wireRead + wireWrite;
        return new ScenarioResult
        {
            Mode = mode,
            Connections = connections,
            Run = run,
            Seconds = seconds,
            Requests = requests,
            RequestsPerSec = requests / seconds,
            LogicalBytes = logical,
            LogicalBytesPerSec = logical / seconds,
            LogicalGbitPerSec = logical * 8.0 / seconds / 1_000_000_000.0,
            WireBytesRead = wireRead,
            WireBytesWritten = wireWrite,
            WireBytesTotal = wireTotal,
            WireBytesPerSec = wireTotal / seconds,
            WireGbitPerSec = wireTotal * 8.0 / seconds / 1_000_000_000.0,
            CompressionRatio = useDeflate && wireRead > 0 ? (double)logical / wireRead : null,
            ExpectedArticleBytes = expectedArticle,
            ExpectedWireBytes = expectedWire,
            LatencyMinMs = latency.MinMs,
            LatencyAvgMs = latency.AverageMs,
            LatencyP50Ms = latency.PercentileMs(50),
            LatencyP95Ms = latency.PercentileMs(95),
            LatencyP99Ms = latency.PercentileMs(99),
            LatencyMaxMs = latency.MaxMs,
            TlsVersion = tlsVersion,
            Cipher = cipher,
            ServerCpuPercent = sample?.CpuPercent,
            ServerCpuTimeSec = sample?.CpuTimeSeconds,
            ServerWorkingSetMb = sample?.WorkingSetMb,
            ServerGc0 = sample?.Gen0,
            ServerGc1 = sample?.Gen1,
            ServerGc2 = sample?.Gen2,
        };
    }

    private static bool RunIperfBaseline(BenchOptions options)
    {
        var iperf = options.IperfPath;
        if (!File.Exists(iperf))
        {
            Console.Error.WriteLine($"iperf3 not found at {iperf}");
            return false;
        }

        Console.WriteLine($"iperf3 baseline against {options.Host} (start server separately if needed)");
        Console.WriteLine("This harness expects an iperf3 server already listening, e.g.:");
        Console.WriteLine($"  {iperf} -s -B {options.Host} -p {options.IperfPort}");
        Console.WriteLine();

        foreach (var streams in Concurrencies)
        {
            var psi = new ProcessStartInfo
            {
                FileName = iperf,
                ArgumentList =
                {
                    "-c", options.Host,
                    "-p", options.IperfPort.ToString(),
                    "-P", streams.ToString(),
                    "-t", "60",
                    "-f", "g",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            Console.WriteLine($"--- iperf3 -P {streams} -t 60 ---");
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Console.WriteLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Console.WriteLine(stderr);
            }
        }

        return true;
    }

    private static void PrintScenario(ScenarioResult r)
    {
        Console.WriteLine(
            $"requests={r.Requests} ({r.RequestsPerSec:F1}/s)  " +
            $"logical={r.LogicalGbitPerSec:F3} Gbit/s  wire={r.WireGbitPerSec:F3} Gbit/s  " +
            $"comp={(r.CompressionRatio is double c ? c.ToString("F2") : "-")}  " +
            $"p50={r.LatencyP50Ms:F2}ms p95={r.LatencyP95Ms:F2}ms p99={r.LatencyP99Ms:F2}ms max={r.LatencyMaxMs:F2}ms");
        if (r.ServerCpuPercent is double cpu)
        {
            Console.WriteLine(
                $"server CPU≈{cpu:F1}%  cpuTime={r.ServerCpuTimeSec:F2}s  WS={r.ServerWorkingSetMb:F0} MB  " +
                $"GC0/1/2={r.ServerGc0}/{r.ServerGc1}/{r.ServerGc2}");
        }

        if (r.TlsVersion is not null)
        {
            Console.WriteLine($"TLS={r.TlsVersion} cipher={r.Cipher}");
        }
    }

    private static void PrintSummaryTable(List<ScenarioResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("| Mode        | Conn | Run | Req/s   | Logical Gbps | Wire Gbps | Comp  | p50 ms | p95 ms | p99 ms | CPU % |");
        Console.WriteLine("| ----------- | ---: | --: | ------: | -----------: | --------: | ----: | -----: | -----: | -----: | ----: |");
        foreach (var r in results)
        {
            Console.WriteLine(
                $"| {r.Mode,-11} | {r.Connections,4} | {r.Run,3} | {r.RequestsPerSec,7:F1} | {r.LogicalGbitPerSec,12:F3} | {r.WireGbitPerSec,9:F3} | {(r.CompressionRatio is double c ? c.ToString("F2") : "-"),5} | {r.LatencyP50Ms,6:F2} | {r.LatencyP95Ms,6:F2} | {r.LatencyP99Ms,6:F2} | {(r.ServerCpuPercent is double cpu ? cpu.ToString("F1") : "-"),5} |");
        }
    }
}

internal sealed class BenchOptions
{
    public string Host { get; init; } = "198.18.0.66";
    public int PlainPort { get; init; } = 1199;
    public int TlsPort { get; init; } = 5633;
    public string TlsHostName { get; init; } = "nntpd01.usenet.ninja";
    public int Runs { get; init; } = 2;
    public int WarmupSeconds { get; init; } = 5;
    public int MeasureSeconds { get; init; } = 60;
    public int? ServerPid { get; init; }
    public string? ModeFilter { get; init; }
    public int? ConnectionsFilter { get; init; }
    public bool IperfOnly { get; init; }
    public string IperfPath { get; init; } = @"C:\Tools\iperf3.exe";
    public int IperfPort { get; init; } = 5201;

    public static BenchOptions Parse(string[] args)
    {
        var host = "198.18.0.66";
        var plain = 1199;
        var tls = 5633;
        var tlsHost = "nntpd01.usenet.ninja";
        var runs = 2;
        var warmup = 5;
        var measure = 60;
        int? pid = null;
        string? mode = null;
        int? conn = null;
        var iperfOnly = false;
        var iperfPath = @"C:\Tools\iperf3.exe";
        var iperfPort = 5201;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host": host = args[++i]; break;
                case "--plain-port": plain = int.Parse(args[++i]); break;
                case "--tls-port": tls = int.Parse(args[++i]); break;
                case "--tls-host": tlsHost = args[++i]; break;
                case "--runs": runs = int.Parse(args[++i]); break;
                case "--warmup-seconds": warmup = int.Parse(args[++i]); break;
                case "--measure-seconds": measure = int.Parse(args[++i]); break;
                case "--server-pid": pid = int.Parse(args[++i]); break;
                case "--mode": mode = args[++i]; break;
                case "--connections": conn = int.Parse(args[++i]); break;
                case "--iperf-only": iperfOnly = true; break;
                case "--iperf-path": iperfPath = args[++i]; break;
                case "--iperf-port": iperfPort = int.Parse(args[++i]); break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        return new BenchOptions
        {
            Host = host,
            PlainPort = plain,
            TlsPort = tls,
            TlsHostName = tlsHost,
            Runs = runs,
            WarmupSeconds = warmup,
            MeasureSeconds = measure,
            ServerPid = pid,
            ModeFilter = mode,
            ConnectionsFilter = conn,
            IperfOnly = iperfOnly,
            IperfPath = iperfPath,
            IperfPort = iperfPort,
        };
    }
}

internal sealed class ScenarioResult
{
    public required string Mode { get; init; }
    public int Connections { get; init; }
    public int Run { get; init; }
    public double Seconds { get; init; }
    public long Requests { get; init; }
    public double RequestsPerSec { get; init; }
    public long LogicalBytes { get; init; }
    public double LogicalBytesPerSec { get; init; }
    public double LogicalGbitPerSec { get; init; }
    public long WireBytesRead { get; init; }
    public long WireBytesWritten { get; init; }
    public long WireBytesTotal { get; init; }
    public double WireBytesPerSec { get; init; }
    public double WireGbitPerSec { get; init; }
    public double? CompressionRatio { get; init; }
    public int ExpectedArticleBytes { get; init; }
    public int ExpectedWireBytes { get; init; }
    public double LatencyMinMs { get; init; }
    public double LatencyAvgMs { get; init; }
    public double LatencyP50Ms { get; init; }
    public double LatencyP95Ms { get; init; }
    public double LatencyP99Ms { get; init; }
    public double LatencyMaxMs { get; init; }
    public string? TlsVersion { get; init; }
    public string? Cipher { get; init; }
    public double? ServerCpuPercent { get; init; }
    public double? ServerCpuTimeSec { get; init; }
    public double? ServerWorkingSetMb { get; init; }
    public int? ServerGc0 { get; init; }
    public int? ServerGc1 { get; init; }
    public int? ServerGc2 { get; init; }
}
