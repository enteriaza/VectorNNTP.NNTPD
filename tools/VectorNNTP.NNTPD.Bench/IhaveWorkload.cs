using System.Diagnostics;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Real serialized IHAVE command benchmark: TCP to production VectorNNTP.NNTPD,
/// production HistoryDB admission, IHAVE → 335 → raw stuffed article → 235.
/// </summary>
internal sealed class IhaveWorkload : IBenchmarkWorkload
{
    public string Name => "IHAVE";

    public async Task<int> RunAsync(BenchOptions options)
    {
        if (options.ModeFilter is not null &&
            !string.Equals(options.ModeFilter, "plain", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("IHAVE currently supports plain TCP only (omit --mode or use --mode plain).");
        }

        var connections = options.ConnectionsFilter ?? 1;
        if (connections > IhaveCommandBuffer.MaxConnections)
        {
            throw new ArgumentException(
                $"IHAVE supports at most {IhaveCommandBuffer.MaxConnections} connections (Message-ID width).");
        }

        var root = IhaveCorpusCatalog.FindArticlesRoot();
        var inventory = IhaveCorpusCatalog.Load(root);
        var articles = IhavePreparedArticles.FromInventory(inventory);
        var commandBytes = new IhaveCommandBuffer(0).Length;

        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine("REAL SERIALIZED IHAVE COMMAND BENCHMARK");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"Workload:            {Name}");
        Console.WriteLine("Type:                real TCP command (production IHAVE path)");
        Console.WriteLine("Not measured:        IHaveArticleReader Pipe microbenchmark");
        Console.WriteLine($"Target:              {options.Host}:{options.PlainPort}");
        Console.WriteLine($"Connections:         {connections} (each connection is serialized)");
        Console.WriteLine("Serialization:       IHAVE → 335 → article → 235 (RFC 3977 §6.3.2)");
        Console.WriteLine("Pipelining:          not used");
        Console.WriteLine($"Target duration:     {options.MeasureSeconds:F3}s");
        Console.WriteLine($"Warmup:              {options.WarmupSeconds:F3}s");
        Console.WriteLine($"Runs:                {options.Runs}");
        Console.WriteLine($"Corpus:              {articles.Root}");
        Console.WriteLine($"Catalog articles:    {articles.InventoryCount:N0}");
        Console.WriteLine($"Prepared articles:   {articles.PreparedCount:N0} ({articles.PreparedBytes:N0} wire bytes)");
        Console.WriteLine("Preparation:         destuffed stored files restuffed + CRLF.CRLF");
        Console.WriteLine("HistoryDB:           production server HistoryDB / Redis (Peek + Remember)");
        Console.WriteLine($"Command Message-ID:  unique per IHAVE ({IhaveCommandBuffer.FormatMessageId(0, 1, 1)})");
        Console.WriteLine($"Command bytes:       {commandBytes}");
        Console.WriteLine("TLS/compression:     not used (plain TCP)");
        Console.WriteLine();

        IhaveRunResult? last = null;
        for (var run = 1; run <= options.Runs; run++)
        {
            if (options.Runs > 1)
            {
                Console.WriteLine($"=== IHAVE × {connections} conn × run {run}/{options.Runs} ===");
            }

            last = await RunOnceAsync(options, connections, articles).ConfigureAwait(false);
            PrintResult(last);
            Console.WriteLine();
        }

        return last is null || last.Failed ? 1 : 0;
    }

    private static async Task<IhaveRunResult> RunOnceAsync(
        BenchOptions options,
        int connections,
        IhavePreparedArticles articles)
    {
        ProcessSampler? sampler = null;
        if (options.ServerPid is int pid)
        {
            sampler = ProcessSampler.TryStart(pid);
        }

        var workers = new IhaveConnection[connections];
        var tasks = new Task[connections];
        var duration = TimeSpan.FromSeconds(options.MeasureSeconds);
        var warmup = TimeSpan.FromSeconds(options.WarmupSeconds);

        sampler?.MarkMeasureStart();
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < connections; i++)
        {
            workers[i] = new IhaveConnection(
                id: i,
                host: options.Host,
                port: options.PlainPort,
                articles: articles,
                duration: duration,
                warmup: warmup);
            tasks[i] = workers[i].RunAsync(CancellationToken.None);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();
        var sample = sampler?.MarkMeasureEnd();

        long sent = 0;
        long accepted = 0;
        long rejected435 = 0;
        long rejected436 = 0;
        long rejected437 = 0;
        long protocol = 0;
        long connection = 0;
        long bytes = 0;
        long articleBytes = 0;
        var maxOutstanding = 0;
        Exception? fault = null;

        foreach (var worker in workers)
        {
            sent += worker.Sent;
            accepted += worker.Accepted235;
            rejected435 += worker.Rejected435;
            rejected436 += worker.Rejected436;
            rejected437 += worker.Rejected437;
            protocol += worker.ProtocolErrors;
            connection += worker.ConnectionErrors;
            bytes += worker.BytesSent;
            articleBytes += worker.ArticleBytesSent;
            if (worker.MaxOutstanding > maxOutstanding)
            {
                maxOutstanding = worker.MaxOutstanding;
            }

            fault ??= worker.Fault;
            await worker.DisposeAsync().ConfigureAwait(false);
        }

        var elapsed = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        return new IhaveRunResult
        {
            Connections = connections,
            ElapsedSeconds = elapsed,
            MeasureSeconds = options.MeasureSeconds,
            PreparedArticles = articles.PreparedCount,
            InventoryArticles = articles.InventoryCount,
            PreparedBytes = articles.PreparedBytes,
            Sent = sent,
            Accepted235 = accepted,
            Rejected435 = rejected435,
            Rejected436 = rejected436,
            Rejected437 = rejected437,
            ProtocolErrors = protocol,
            ConnectionErrors = connection,
            BytesSent = bytes,
            ArticleBytesSent = articleBytes,
            MaxOutstanding = maxOutstanding,
            ArticlesPerSec = accepted / elapsed,
            LogicalGbitPerSec = articleBytes * 8.0 / elapsed / 1_000_000_000.0,
            WireGbitPerSec = bytes * 8.0 / elapsed / 1_000_000_000.0,
            ServerCpuPercent = sample?.CpuPercent,
            ServerCpuTimeSec = sample?.CpuTimeSeconds,
            Fault = fault,
        };
    }

    private static void PrintResult(IhaveRunResult r)
    {
        Console.WriteLine(new string('=', 72));
        Console.WriteLine("RESULT");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"Elapsed:             {r.ElapsedSeconds:F3}s");
        Console.WriteLine();
        Console.WriteLine($"IHAVE sent:          {r.Sent:N0}");
        Console.WriteLine($"235 accepted:        {r.Accepted235:N0}");
        Console.WriteLine($"435 not wanted:      {r.Rejected435:N0}");
        Console.WriteLine($"436 try later:       {r.Rejected436:N0}");
        Console.WriteLine($"437 rejected:        {r.Rejected437:N0}");
        Console.WriteLine($"Protocol errors:     {r.ProtocolErrors:N0}");
        Console.WriteLine($"Connection errors:   {r.ConnectionErrors:N0}");
        Console.WriteLine($"Max outstanding:     {r.MaxOutstanding} (serialized; limit 1/connection)");
        Console.WriteLine();
        Console.WriteLine($"IHAVE/sec:           {r.ArticlesPerSec:N1} (235 accepted)");
        Console.WriteLine($"Logical payload:     {r.LogicalGbitPerSec:F3} Gbit/s (article bytes)");
        Console.WriteLine($"Wire throughput:     {r.WireGbitPerSec:F3} Gbit/s (command + article)");
        Console.WriteLine();
        if (r.ServerCpuPercent is double cpu)
        {
            Console.WriteLine($"Server CPU≈          {cpu:F1}%  cpuTime={r.ServerCpuTimeSec:F2}s");
        }

        if (r.Fault is not null)
        {
            Console.WriteLine($"First fault:         {r.Fault.GetType().Name}: {r.Fault.Message}");
        }

        if (r.Failed)
        {
            Console.WriteLine("IHAVE benchmark failed: unexpected HistoryDB/protocol/connection result.");
        }

        Console.WriteLine(new string('=', 72));
    }
}

internal sealed class IhaveRunResult
{
    public int Connections { get; init; }
    public double ElapsedSeconds { get; init; }
    public int MeasureSeconds { get; init; }
    public int PreparedArticles { get; init; }
    public int InventoryArticles { get; init; }
    public long PreparedBytes { get; init; }
    public long Sent { get; init; }
    public long Accepted235 { get; init; }
    public long Rejected435 { get; init; }
    public long Rejected436 { get; init; }
    public long Rejected437 { get; init; }
    public long ProtocolErrors { get; init; }
    public long ConnectionErrors { get; init; }
    public long BytesSent { get; init; }
    public long ArticleBytesSent { get; init; }
    public int MaxOutstanding { get; init; }
    public double ArticlesPerSec { get; init; }
    public double LogicalGbitPerSec { get; init; }
    public double WireGbitPerSec { get; init; }
    public double? ServerCpuPercent { get; init; }
    public double? ServerCpuTimeSec { get; init; }
    public Exception? Fault { get; init; }

    public bool Failed =>
        Rejected435 > 0
        || Rejected436 > 0
        || Rejected437 > 0
        || ProtocolErrors > 0
        || ConnectionErrors > 0
        || Accepted235 != Sent
        || Fault is not null;
}
