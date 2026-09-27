using System.Diagnostics;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Real serialized POST command benchmark: TCP to production VectorNNTP.NNTPD,
/// AUTHINFO, POST → 340 → article → 240.
/// </summary>
internal sealed class PostWorkload : IBenchmarkWorkload
{
    public string Name => "POST";

    public async Task<int> RunAsync(BenchOptions options)
    {
        if (options.ModeFilter is not null &&
            !string.Equals(options.ModeFilter, "plain", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("POST currently supports plain TCP only (omit --mode or use --mode plain).");
        }

        if (string.IsNullOrWhiteSpace(options.AuthUser) || string.IsNullOrEmpty(options.AuthPassword))
        {
            throw new ArgumentException(
                "POST requires posting credentials: --auth-user / --auth-pass or BENCH_POST_USER / BENCH_POST_PASSWORD.");
        }

        var connections = options.ConnectionsFilter ?? 1;
        if (connections > TakeThisCommandBuffer.MaxConnections)
        {
            throw new ArgumentException(
                $"POST supports at most {TakeThisCommandBuffer.MaxConnections} connections (Message-ID width).");
        }

        var article = TakeThisArticlePayload.Build(options.ArticleSize);
        var bodyBytes = TakeThisArticlePayload.BodyBytes(article);
        var commandBytes = "POST\r\n".Length;

        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine("POST BENCHMARK");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"Workload:            {Name}");
        Console.WriteLine("Type:                real TCP command (production POST path)");
        Console.WriteLine($"Target:              {options.Host}:{options.PlainPort}");
        Console.WriteLine($"Connections:         {connections} (each connection is serialized)");
        Console.WriteLine("Serialization:       POST → 340 → article → 240 (RFC 3977 §6.3.1)");
        Console.WriteLine("Pipelining:          not used");
        Console.WriteLine($"Target duration:     {options.MeasureSeconds:F3}s");
        Console.WriteLine($"Warmup:              {options.WarmupSeconds:F3}s");
        Console.WriteLine($"Runs:                {options.Runs}");
        Console.WriteLine($"Article-size arg:    {options.ArticleSize:N0} bytes (target body)");
        Console.WriteLine($"Article body:        {bodyBytes:N0} bytes");
        Console.WriteLine($"Article on wire:     {article.Length:N0} bytes (headers + body + terminator)");
        Console.WriteLine($"Command + article:   {commandBytes + article.Length:N0} bytes");
        Console.WriteLine("Article headers:     Path, From, Newsgroups, Subject, Date, Message-ID");
        Console.WriteLine($"Newsgroups:          {TakeThisArticlePayload.Newsgroups}");
        Console.WriteLine("Article Message-ID:  unique per POST <pIIIIIIIIII-CC-SSSSSSSSSSSS@vectornntp.local>");
        Console.WriteLine("History isolation:   new 'p' + 10-digit instance per connection");
        Console.WriteLine("AUTHINFO:            USER/PASS (password not printed)");
        Console.WriteLine("ArticleRecordFactory: accepted at client build (CanonicalV1 destuffed article)");
        Console.WriteLine("Intended server path: receive → POST processing → factory → queue (240)");
        Console.WriteLine("TLS/compression:     not used (plain TCP)");
        Console.WriteLine();

        PostRunResult? last = null;
        for (var run = 1; run <= options.Runs; run++)
        {
            if (options.Runs > 1)
            {
                Console.WriteLine($"=== POST × {connections} conn × run {run}/{options.Runs} ===");
            }

            last = await RunOnceAsync(options, connections, article).ConfigureAwait(false);
            PrintResult(last);
            Console.WriteLine();
        }

        return last is { Failed: true } ? 1 : 0;
    }

    private static async Task<PostRunResult> RunOnceAsync(
        BenchOptions options,
        int connections,
        byte[] article)
    {
        ProcessSampler? sampler = null;
        if (options.ServerPid is int pid)
        {
            sampler = ProcessSampler.TryStart(pid);
        }

        var workers = new PostConnection[connections];
        var tasks = new Task[connections];
        var duration = TimeSpan.FromSeconds(options.MeasureSeconds);
        var warmup = TimeSpan.FromSeconds(options.WarmupSeconds);

        sampler?.MarkMeasureStart();
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < connections; i++)
        {
            workers[i] = new PostConnection(
                id: i,
                host: options.Host,
                port: options.PlainPort,
                articleTemplate: article,
                authUser: options.AuthUser,
                authPassword: options.AuthPassword,
                duration: duration,
                warmup: warmup);
            tasks[i] = workers[i].RunAsync(CancellationToken.None);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();
        var sample = sampler?.MarkMeasureEnd();

        long sent = 0;
        long accepted = 0;
        long rejected441 = 0;
        long rejected440 = 0;
        long temporary = 0;
        long protocol = 0;
        long connection = 0;
        long bytes = 0;
        long articleBytes = 0;
        var maxOutstanding = 0;
        Exception? fault = null;

        foreach (var worker in workers)
        {
            sent += worker.Sent;
            accepted += worker.Accepted240;
            rejected441 += worker.Rejected441;
            rejected440 += worker.Rejected440;
            temporary += worker.Temporary400;
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
        return new PostRunResult
        {
            Connections = connections,
            ElapsedSeconds = elapsed,
            MeasureSeconds = options.MeasureSeconds,
            ArticleBytes = article.Length,
            Sent = sent,
            Accepted240 = accepted,
            Rejected441 = rejected441,
            Rejected440 = rejected440,
            Temporary400 = temporary,
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

    private static void PrintResult(PostRunResult r)
    {
        Console.WriteLine(new string('=', 72));
        Console.WriteLine("RESULT");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"Elapsed:             {r.ElapsedSeconds:F3}s");
        Console.WriteLine();
        Console.WriteLine($"POST sent:           {r.Sent:N0}");
        Console.WriteLine($"240 accepted:        {r.Accepted240:N0}");
        Console.WriteLine($"441 rejected:        {r.Rejected441:N0}");
        Console.WriteLine($"440 not permitted:   {r.Rejected440:N0}");
        Console.WriteLine($"Temporary 400s:      {r.Temporary400:N0}");
        Console.WriteLine($"Other responses:     {r.ProtocolErrors:N0}");
        Console.WriteLine($"Protocol errors:     {r.ProtocolErrors:N0}");
        Console.WriteLine($"Connection errors:   {r.ConnectionErrors:N0}");
        Console.WriteLine("Max outstanding:     1 (serialized POST; no pipeline)");
        Console.WriteLine();
        Console.WriteLine($"POST/sec:            {r.ArticlesPerSec:N1} (240 accepted)");
        Console.WriteLine($"Logical payload:     {r.LogicalGbitPerSec:F3} Gbit/s (article bytes)");
        Console.WriteLine($"Wire throughput:     {r.WireGbitPerSec:F3} Gbit/s (command + article)");
        Console.WriteLine();
        var unseenOk = r.Sent > 0
            && r.Accepted240 == r.Sent
            && r.Rejected441 == 0
            && r.Rejected440 == 0
            && r.ProtocolErrors == 0
            && r.Temporary400 == 0;
        Console.WriteLine(
            unseenOk
                ? "Path check:          240==sent, 441==0, other==0 (factory reached on ingress binary)"
                : "Path check:          NOT a clean accept (see 441/440/other/400 counts)");
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

internal sealed class PostRunResult
{
    public int Connections { get; init; }
    public double ElapsedSeconds { get; init; }
    public int MeasureSeconds { get; init; }
    public int ArticleBytes { get; init; }
    public long Sent { get; init; }
    public long Accepted240 { get; init; }
    public long Rejected441 { get; init; }
    public long Rejected440 { get; init; }
    public long Temporary400 { get; init; }
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
        Rejected441 > 0
        || Rejected440 > 0
        || Temporary400 > 0
        || ProtocolErrors > 0
        || ConnectionErrors > 0
        || Accepted240 != Sent
        || Fault is not null;
}
