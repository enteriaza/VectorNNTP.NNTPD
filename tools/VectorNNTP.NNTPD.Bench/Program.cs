using System.Diagnostics;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Benchmark runner for VectorNNTP.NNTPD. Workload is selected with
/// <c>--benchmark BENCHIT|TAKETHIS|CHECK</c> (default BENCHIT).
/// BENCHIT and TAKETHIS are real-TCP clients. CHECK is a session/application
/// measure (Pipes + fake Redis) of the frozen depth-16 CHECK pipeline.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = BenchOptions.Parse(args);

        if (options.IperfOnly)
        {
            return RunIperfBaseline(options) ? 0 : 1;
        }

        var workload = BenchmarkWorkloadCatalog.Resolve(options.Benchmark);
        return await workload.RunAsync(options).ConfigureAwait(false);
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

        foreach (var streams in new[] { 1, 10, 50 })
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
}
