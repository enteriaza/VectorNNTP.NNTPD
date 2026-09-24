namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Session/application CHECK benchmark (NntpSession + Pipes + fake Redis).
/// Not a TCP socket throughput measurement. Depth is the production constant 16.
/// </summary>
internal sealed class CheckWorkload : IBenchmarkWorkload
{
    public string Name => "CHECK";

    public async Task<int> RunAsync(BenchOptions options)
    {
        _ = options;
        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine("CHECK BENCHMARK");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine("Type:                session/application (NntpSession + Pipes + fake Redis)");
        Console.WriteLine("Not measured:        TCP socket throughput");
        Console.WriteLine($"CHECK pipeline depth: {CheckSessionMeasure.ProductionDepth} (fixed architectural constant)");
        Console.WriteLine("Configurable:        no");
        Console.WriteLine($"Iterations:          {CheckSessionMeasure.ZeroDelayIterations} when Redis delay is 0 ms; {CheckSessionMeasure.DelayedIterations} when delay is 1/2/5 ms");
        Console.WriteLine("Warmup:              none (validation session bench: GC.Collect before each measure)");
        Console.WriteLine("Redis delays:        0 / 1 / 2 / 5 ms");
        Console.WriteLine("Workloads:           redis-hit (0/1/2/5), redis-miss (1/2/5), local-hit (0/1/2/5), cooldown");
        Console.WriteLine("Ordering:            hard fail if Message-IDs are not in command order");
        Console.WriteLine();

        var rows = await CheckSessionMeasure.RunAllAsync().ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine("CHECK summary");
        Console.WriteLine(
            $"{"Workload",-12} {"Delay",6} {"CHECKs",8} {"CHECK/s",10} {"Peak",6} {"EXISTS",8} {"Responses",22} {"Ordered",8}");
        foreach (var row in rows)
        {
            Console.WriteLine(
                $"{row.Workload,-12} {row.DelayMs,4} ms {row.Checks,8} {row.ChecksPerSecond,10:F0} {row.PeakInFlight,6} {row.Exists,8} {row.Responses,22} {true,8}");
        }

        return 0;
    }
}
