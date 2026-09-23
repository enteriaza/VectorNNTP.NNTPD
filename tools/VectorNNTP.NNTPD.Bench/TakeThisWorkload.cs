using System.Diagnostics;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// RFC 4644 STREAM TAKETHIS client workload. Uses a single pre-built article payload
/// and unique Message-IDs on the command line only.
/// </summary>
internal sealed class TakeThisWorkload : IBenchmarkWorkload
{
    public string Name => "TAKETHIS";

    public async Task<int> RunAsync(BenchOptions options)
    {
        if (options.ModeFilter is not null &&
            !string.Equals(options.ModeFilter, "plain", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("TAKETHIS currently supports plain TCP only (omit --mode or use --mode plain).");
        }

        var connections = options.ConnectionsFilter ?? 1;
        if (connections > TakeThisCommandBuffer.MaxConnections)
        {
            throw new ArgumentException(
                $"TAKETHIS supports at most {TakeThisCommandBuffer.MaxConnections} connections (Message-ID width).");
        }

        var article = TakeThisArticlePayload.Build(options.ArticleSize);
        var bodyBytes = TakeThisArticlePayload.BodyBytes(article);
        var commandBytes = new TakeThisCommandBuffer(0).Length;
        var wirePerArticle = commandBytes + article.Length;

        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine("TAKETHIS BENCHMARK");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"Workload:            {Name}");
        Console.WriteLine($"Target:              {options.Host}:{options.PlainPort}");
        Console.WriteLine($"Connections:         {connections}");
        Console.WriteLine($"Target duration:     {options.MeasureSeconds:F3}s");
        Console.WriteLine($"Warmup:              {options.WarmupSeconds:F3}s");
        Console.WriteLine($"Runs:                {options.Runs}");
        Console.WriteLine($"Pipeline depth:      {options.PipelineDepth} outstanding TAKETHIS / connection");
        Console.WriteLine($"Article-size arg:    {options.ArticleSize:N0} bytes (target body)");
        Console.WriteLine($"Article body:        {bodyBytes:N0} bytes");
        Console.WriteLine($"Article on wire:     {article.Length:N0} bytes (headers + body + terminator)");
        Console.WriteLine($"Command + article:   {wirePerArticle:N0} bytes");
        Console.WriteLine($"Article Message-ID:  static <{TakeThisArticlePayload.StaticMessageId}>");
        Console.WriteLine("Command Message-ID:  unique per TAKETHIS (bench-CC-SSSSSSSSSSSS@vectornntp.local)");
        Console.WriteLine("TLS/compression:     not used (plain TCP)");
        Console.WriteLine();

        TakeThisRunResult? last = null;
        for (var run = 1; run <= options.Runs; run++)
        {
            if (options.Runs > 1)
            {
                Console.WriteLine($"=== TAKETHIS × {connections} conn × run {run}/{options.Runs} ===");
            }

            last = await RunOnceAsync(options, connections, article).ConfigureAwait(false);
            PrintResult(last);
            Console.WriteLine();
        }

        return last is { ConnectionErrors: > 0 } or { ProtocolErrors: > 0 } ? 1 : 0;
    }

    private static async Task<TakeThisRunResult> RunOnceAsync(
        BenchOptions options,
        int connections,
        byte[] article)
    {
        ProcessSampler? sampler = null;
        if (options.ServerPid is int pid)
        {
            sampler = ProcessSampler.TryStart(pid);
        }

        var workers = new TakeThisConnection[connections];
        var tasks = new Task[connections];
        var duration = TimeSpan.FromSeconds(options.MeasureSeconds);
        var warmup = TimeSpan.FromSeconds(options.WarmupSeconds);

        sampler?.MarkMeasureStart();
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < connections; i++)
        {
            workers[i] = new TakeThisConnection(
                id: i,
                host: options.Host,
                port: options.PlainPort,
                article: article,
                pipelineDepth: options.PipelineDepth,
                duration: duration,
                warmup: warmup);
            tasks[i] = workers[i].RunAsync(CancellationToken.None);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();
        var sample = sampler?.MarkMeasureEnd();

        long sent = 0;
        long accepted = 0;
        long rejected = 0;
        long protocol = 0;
        long connection = 0;
        long temporary = 0;
        long bytes = 0;
        var maxOutstanding = 0;
        Exception? fault = null;

        foreach (var worker in workers)
        {
            sent += worker.Sent;
            accepted += worker.Accepted239;
            rejected += worker.Rejected439;
            protocol += worker.ProtocolErrors;
            connection += worker.ConnectionErrors;
            temporary += worker.Temporary400;
            bytes += worker.BytesSent;
            if (worker.MaxOutstanding > maxOutstanding)
            {
                maxOutstanding = worker.MaxOutstanding;
            }

            fault ??= worker.Fault;
            await worker.DisposeAsync().ConfigureAwait(false);
        }

        var elapsed = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        var logicalBytes = sent * article.Length;
        return new TakeThisRunResult
        {
            Connections = connections,
            ElapsedSeconds = elapsed,
            MeasureSeconds = options.MeasureSeconds,
            PipelineDepth = options.PipelineDepth,
            ArticleBytes = article.Length,
            Sent = sent,
            Accepted239 = accepted,
            Rejected439 = rejected,
            ProtocolErrors = protocol,
            ConnectionErrors = connection,
            Temporary400 = temporary,
            BytesSent = bytes,
            MaxOutstanding = maxOutstanding,
            ArticlesPerSec = sent / elapsed,
            LogicalGbitPerSec = logicalBytes * 8.0 / elapsed / 1_000_000_000.0,
            WireGbitPerSec = bytes * 8.0 / elapsed / 1_000_000_000.0,
            ServerCpuPercent = sample?.CpuPercent,
            ServerCpuTimeSec = sample?.CpuTimeSeconds,
            Fault = fault,
        };
    }

    private static void PrintResult(TakeThisRunResult r)
    {
        var responses = r.Accepted239 + r.Rejected439;
        var ratio = r.Sent > 0 ? (double)responses / r.Sent : 0.0;

        Console.WriteLine(new string('=', 72));
        Console.WriteLine("RESULT");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"Elapsed:             {r.ElapsedSeconds:F3}s");
        Console.WriteLine();
        Console.WriteLine($"TAKETHIS sent:       {r.Sent:N0}");
        Console.WriteLine($"239 accepted:        {r.Accepted239:N0}");
        Console.WriteLine($"439 rejected:        {r.Rejected439:N0}");
        Console.WriteLine($"Temporary 400s:      {r.Temporary400:N0}");
        Console.WriteLine($"Protocol errors:     {r.ProtocolErrors:N0}");
        Console.WriteLine($"Connection errors:   {r.ConnectionErrors:N0}");
        Console.WriteLine($"Max outstanding:     {r.MaxOutstanding} (limit {r.PipelineDepth}/connection)");
        Console.WriteLine();
        Console.WriteLine($"TAKETHIS/sec:        {r.ArticlesPerSec:N1}");
        Console.WriteLine($"Logical payload:     {r.LogicalGbitPerSec:F3} Gbit/s (article bytes)");
        Console.WriteLine($"Wire throughput:     {r.WireGbitPerSec:F3} Gbit/s (command + article)");
        Console.WriteLine();
        Console.WriteLine($"Response ratio:      {ratio:P4} (239+439 / sent)");
        if (r.ServerCpuPercent is double cpu)
        {
            Console.WriteLine($"Server CPU≈          {cpu:F1}%  cpuTime={r.ServerCpuTimeSec:F2}s");
        }

        if (r.Fault is not null)
        {
            Console.WriteLine($"First fault:         {r.Fault.GetType().Name}: {r.Fault.Message}");
        }

        Console.WriteLine(new string('=', 72));
    }
}

internal sealed class TakeThisRunResult
{
    public int Connections { get; init; }
    public double ElapsedSeconds { get; init; }
    public int MeasureSeconds { get; init; }
    public int PipelineDepth { get; init; }
    public int ArticleBytes { get; init; }
    public long Sent { get; init; }
    public long Accepted239 { get; init; }
    public long Rejected439 { get; init; }
    public long ProtocolErrors { get; init; }
    public long ConnectionErrors { get; init; }
    public long Temporary400 { get; init; }
    public long BytesSent { get; init; }
    public int MaxOutstanding { get; init; }
    public double ArticlesPerSec { get; init; }
    public double LogicalGbitPerSec { get; init; }
    public double WireGbitPerSec { get; init; }
    public double? ServerCpuPercent { get; init; }
    public double? ServerCpuTimeSec { get; init; }
    public Exception? Fault { get; init; }
}
