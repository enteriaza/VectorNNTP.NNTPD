using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VectorNNTP.TcpSmoke.Payload;
using VectorNNTP.TcpSmoke.Protocol;
using VectorNNTP.TcpSmoke.Transfer;

namespace VectorNNTP.TcpSmoke.Benchmark;

/// <summary>One self-hosted or client-only TCP article run.</summary>
public sealed class BenchmarkPlan
{
    /// <summary>Server address. Self-host binds this address.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>Server port. Zero on a self-host run asks the operating system for a port.</summary>
    public int Port { get; init; }

    /// <summary>When true, this process starts the server.</summary>
    public bool SelfHost { get; init; } = true;

    /// <summary>When true, a non-loopback self-host bind is allowed.</summary>
    public bool AllowRemote { get; init; }

    /// <summary><c>transport</c> discards the body after hashing. <c>file</c> publishes a file before RESULT.</summary>
    public string Mode { get; init; } = "transport";

    /// <summary><c>persistent</c> reuses one connection per worker. <c>fresh</c> connects for every transfer.</summary>
    public string Connections { get; init; } = "persistent";

    /// <summary>Article size in bytes.</summary>
    public int PayloadBytes { get; init; } = ArticlePayload.DefaultBytes;

    /// <summary><c>random</c> or <c>article</c>.</summary>
    public string PayloadKind { get; init; } = "random";

    /// <summary>Worker connections.</summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>Measured transfers when <see cref="DurationSeconds"/> is zero.</summary>
    public int Transfers { get; init; } = 16;

    /// <summary>Transfers before the clock starts. Failures abort the run.</summary>
    public int WarmupTransfers { get; init; } = 4;

    /// <summary>Measured wall-clock budget after warmup. Zero uses <see cref="Transfers"/>.</summary>
    public int DurationSeconds { get; init; }

    /// <summary>Connect and transfer timeout.</summary>
    public int TimeoutMilliseconds { get; init; } = 30_000;

    /// <summary>Directory for <c>summary.json</c> and, in file mode, the articles.</summary>
    public string ResultsDirectory { get; init; } = "";

    /// <summary>When true, file mode leaves published articles in place.</summary>
    public bool KeepFiles { get; init; }

    /// <summary>When true, file mode hashes the temp file a second time before RESULT.</summary>
    public bool Reread { get; init; }

    /// <summary>Payload seed. The same seed and kind always produce the same bytes.</summary>
    public ulong Seed { get; init; } = 1;

    /// <summary>Server article cap.</summary>
    public int MaxArticleBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Accepted connections. Persistent mode needs at least <see cref="Concurrency"/>.</summary>
    public int ServerMaxClients { get; init; } = 128;
}

/// <summary>Machine-readable summary. Payload rates count completed article bytes over measured wall time.</summary>
public sealed class BenchmarkReport
{
    /// <summary>Schema name.</summary>
    public string Schema { get; init; } = "vectornntp.tcp-smoke.v1";

    /// <summary>UTC start, including warmup.</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Server address.</summary>
    public string Host { get; init; } = "";

    /// <summary>Server port.</summary>
    public int Port { get; init; }

    /// <summary>True when this process hosted the server. CPU then includes both peers.</summary>
    public bool SelfHosted { get; init; }

    /// <summary><c>transport</c> or <c>file</c>.</summary>
    public string Mode { get; init; } = "";

    /// <summary><c>persistent</c> or <c>fresh</c>.</summary>
    public string Connections { get; init; } = "";

    /// <summary>True when file mode hashed the temp file a second time.</summary>
    public bool Reread { get; init; }

    /// <summary>Article size.</summary>
    public int PayloadBytes { get; init; }

    /// <summary>Payload generator name.</summary>
    public string PayloadKind { get; init; } = "";

    /// <summary>Hex SHA-256 of the generated payload.</summary>
    public string PayloadSha256 { get; init; } = "";

    /// <summary>Generator seed.</summary>
    public ulong Seed { get; init; }

    /// <summary>Configured workers.</summary>
    public int Concurrency { get; init; }

    /// <summary>Warmup count.</summary>
    public int WarmupTransfers { get; init; }

    /// <summary>Warmup failures. A non-zero value means the measured interval did not run.</summary>
    public int WarmupFailures { get; init; }

    /// <summary>Measured attempts.</summary>
    public int MeasuredAttempts { get; init; }

    /// <summary>RESULT Ok transfers.</summary>
    public int SuccessfulTransfers { get; init; }

    /// <summary>RESULT with a non-success status.</summary>
    public int FailedTransfers { get; init; }

    /// <summary>Disconnects and timeouts before RESULT.</summary>
    public int IncompleteTransfers { get; init; }

    /// <summary>Hash mismatches reported by the receiver.</summary>
    public int VerificationFailures { get; init; }

    /// <summary>Completed article bytes. Incomplete transfers contribute nothing.</summary>
    public long PayloadBytesTransferred { get; init; }

    /// <summary>Wall time from the end of warmup until in-flight measured transfers finish.</summary>
    public double ElapsedSeconds { get; init; }

    /// <summary>Successful transfers per second.</summary>
    public double TransfersPerSecond { get; init; }

    /// <summary>Completed article bytes per second, in MiB.</summary>
    public double PayloadMebibytesPerSecond { get; init; }

    /// <summary>Completed article bytes per second, in decimal Gbit/s. This is not NIC line rate.</summary>
    public double PayloadGigabitsPerSecond { get; init; }

    /// <summary>How the rates were computed.</summary>
    public string ThroughputBasis { get; init; } = "successful article bytes divided by measured wall time; loopback is not NIC line rate";

    /// <summary>Mean successful transfer latency.</summary>
    public double LatencyMeanMilliseconds { get; init; }

    /// <summary>Median successful transfer latency.</summary>
    public double LatencyMedianMilliseconds { get; init; }

    /// <summary>Nearest-rank p95 successful transfer latency.</summary>
    public double LatencyP95Milliseconds { get; init; }

    /// <summary>Nearest-rank p99 successful transfer latency.</summary>
    public double LatencyP99Milliseconds { get; init; }

    /// <summary>Mean TCP connect time for connects observed by this process.</summary>
    public double ConnectMeanMilliseconds { get; init; }

    /// <summary>True when connect time is included in each transfer latency. Fresh connections do. Persistent connections do not.</summary>
    public bool ConnectIncludedInLatency { get; init; }

    /// <summary>Process processor seconds over the measured interval. Self-host includes both peers.</summary>
    public double CpuProcessorSeconds { get; init; }

    /// <summary>Processor time divided by wall time.</summary>
    public double CpuPercentOfOneCore { get; init; }

    /// <summary>Processor time divided by wall time and logical processors.</summary>
    public double CpuPercentOfMachine { get; init; }

    /// <summary>Managed bytes allocated during the measured interval.</summary>
    public long AllocatedBytes { get; init; }

    /// <summary>Generation 0 collections during the interval.</summary>
    public int Gen0Collections { get; init; }

    /// <summary>Generation 1 collections during the interval.</summary>
    public int Gen1Collections { get; init; }

    /// <summary>Generation 2 collections during the interval.</summary>
    public int Gen2Collections { get; init; }

    /// <summary>Highest number of measured transfers in flight.</summary>
    public int MaxOutstanding { get; init; }

    /// <summary>Logical processors.</summary>
    public int ProcessorCount { get; init; }

    /// <summary>Runtime description.</summary>
    public string Runtime { get; init; } = "";

    /// <summary>OS description.</summary>
    public string Os { get; init; } = "";

    /// <summary>What the successes imply about verification.</summary>
    public string Integrity { get; init; } = "";

    /// <summary>True when published files were left on disk.</summary>
    public bool KeepFiles { get; init; }

    /// <summary>Up to 20 failure strings.</summary>
    public string[] Failures { get; init; } = [];
}

/// <summary>Runs warmup and a measured interval.</summary>
public static class BenchmarkRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Executes the plan and writes <c>summary.json</c>.</summary>
    public static async Task<BenchmarkReport> RunAsync(BenchmarkPlan plan, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        var storeFiles = plan.Mode.Equals("file", StringComparison.OrdinalIgnoreCase);
        var fresh = plan.Connections.Equals("fresh", StringComparison.OrdinalIgnoreCase);
        var results = string.IsNullOrWhiteSpace(plan.ResultsDirectory)
            ? Path.Combine("artifacts", "tcp-smoke", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ"))
            : plan.ResultsDirectory;
        Directory.CreateDirectory(results);
        var payload = plan.PayloadKind.Equals("article", StringComparison.OrdinalIgnoreCase)
            ? ArticlePayload.CreateArticle(plan.PayloadBytes, plan.Seed)
            : ArticlePayload.CreatePseudorandom(plan.PayloadBytes, plan.Seed);
        var payloadHash = SHA256.HashData(payload);
        var startedUtc = DateTimeOffset.UtcNow;
        var success = 0;
        var failed = 0;
        var incomplete = 0;
        var mismatches = 0;
        var warmupFailures = 0;
        var outstanding = 0;
        var maxOutstanding = 0;
        var latencies = new ConcurrentBag<double>();
        var connectTimes = new ConcurrentBag<double>();
        var failures = new ConcurrentQueue<string>();
        var host = IPAddress.Parse(plan.Host);
        var timing = TimeSpan.FromMilliseconds(plan.TimeoutMilliseconds);
        ArticleServer? server = null;
        Task? serverTask = null;
        using var serverStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var root = Path.Combine(results, "files");
        if (plan.SelfHost)
        {
            if (storeFiles)
            {
                Directory.CreateDirectory(root);
            }

            server = new ArticleServer(new ArticleServerOptions
            {
                BindAddress = host,
                Port = plan.Port,
                AllowRemote = plan.AllowRemote,
                MaxConcurrentClients = Math.Max(plan.ServerMaxClients, plan.Concurrency),
                MaxArticleBytes = plan.MaxArticleBytes,
                StoreFiles = storeFiles,
                RootDirectory = storeFiles ? root : null,
                RereadBeforeResult = plan.Reread && storeFiles,
                OperationTimeout = timing,
            });
            serverTask = server.RunAsync(serverStop.Token);
            await server.Started.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var port = server?.LocalEndPoint?.Port ?? plan.Port;
        var client = new ArticleClient(host, port, timing);
        var articleSequence = 0;
        var persistent = new ArticleConnection?[plan.Concurrency];
        try
        {
            if (!fresh)
            {
                for (var index = 0; index < persistent.Length; index++)
                {
                    persistent[index] = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    connectTimes.Add(persistent[index]!.ConnectElapsed.TotalMilliseconds);
                }
            }

            await RunWorkersAsync(plan.WarmupTransfers, TimeSpan.Zero, record: false).ConfigureAwait(false);
            if (Volatile.Read(ref warmupFailures) > 0)
            {
                throw new InvalidOperationException($"Warmup failed {warmupFailures} transfer(s). Measured interval was not started.");
            }

            var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuStart = process.TotalProcessorTime;
            var allocatedStart = GC.GetTotalAllocatedBytes(precise: false);
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var wall = Stopwatch.StartNew();
            if (plan.DurationSeconds > 0)
            {
                await RunWorkersAsync(1_000_000, TimeSpan.FromSeconds(plan.DurationSeconds), record: true).ConfigureAwait(false);
            }
            else
            {
                await RunWorkersAsync(plan.Transfers, TimeSpan.Zero, record: true).ConfigureAwait(false);
            }

            wall.Stop();
            process.Refresh();
            var elapsed = wall.Elapsed.TotalSeconds;
            var safeElapsed = elapsed <= 0 ? double.Epsilon : elapsed;
            var transferred = (long)success * payload.Length;
            var cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
            var latency = latencies.ToArray();
            Array.Sort(latency);
            var connects = connectTimes.ToArray();
            var report = new BenchmarkReport
            {
                StartedUtc = startedUtc,
                Host = plan.Host,
                Port = port,
                SelfHosted = plan.SelfHost,
                Mode = storeFiles ? "file" : "transport",
                Connections = fresh ? "fresh" : "persistent",
                Reread = plan.Reread && storeFiles,
                PayloadBytes = payload.Length,
                PayloadKind = plan.PayloadKind,
                PayloadSha256 = ArticlePayload.Sha256Hex(payloadHash),
                Seed = plan.Seed,
                Concurrency = plan.Concurrency,
                WarmupTransfers = plan.WarmupTransfers,
                WarmupFailures = warmupFailures,
                MeasuredAttempts = success + failed + incomplete,
                SuccessfulTransfers = success,
                FailedTransfers = failed,
                IncompleteTransfers = incomplete,
                VerificationFailures = mismatches,
                PayloadBytesTransferred = transferred,
                ElapsedSeconds = elapsed,
                TransfersPerSecond = success / safeElapsed,
                PayloadMebibytesPerSecond = transferred / safeElapsed / (1024d * 1024d),
                PayloadGigabitsPerSecond = transferred * 8d / safeElapsed / 1_000_000_000d,
                LatencyMeanMilliseconds = latency.Length == 0 ? 0 : latency.Average(),
                LatencyMedianMilliseconds = Percentile(latency, 0.50),
                LatencyP95Milliseconds = Percentile(latency, 0.95),
                LatencyP99Milliseconds = Percentile(latency, 0.99),
                ConnectMeanMilliseconds = connects.Length == 0 ? 0 : connects.Average(),
                ConnectIncludedInLatency = fresh,
                CpuProcessorSeconds = cpuSeconds,
                CpuPercentOfOneCore = elapsed <= 0 ? 0 : cpuSeconds / elapsed * 100d,
                CpuPercentOfMachine = elapsed <= 0 ? 0 : cpuSeconds / elapsed / Environment.ProcessorCount * 100d,
                AllocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedStart,
                Gen0Collections = GC.CollectionCount(0) - gen0,
                Gen1Collections = GC.CollectionCount(1) - gen1,
                Gen2Collections = GC.CollectionCount(2) - gen2,
                MaxOutstanding = maxOutstanding,
                ProcessorCount = Environment.ProcessorCount,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Integrity = IntegrityOf(plan.SelfHost, storeFiles, failed, incomplete, mismatches),
                KeepFiles = plan.KeepFiles,
                Failures = failures.Take(20).ToArray(),
            };
            var json = JsonSerializer.Serialize(report, JsonOptions);
            await File.WriteAllTextAsync(Path.Combine(results, "summary.json"), json, cancellationToken).ConfigureAwait(false);
            WriteHuman(output, report, results);
            output.WriteLine();
            output.WriteLine(json);
            if (!plan.KeepFiles && storeFiles)
            {
                TryDeleteFiles(root);
            }

            return report;
        }
        finally
        {
            if (serverTask is not null)
            {
                await serverStop.CancelAsync().ConfigureAwait(false);
                try
                {
                    await serverTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (server is not null)
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }

            for (var index = 0; index < persistent.Length; index++)
            {
                if (persistent[index] is not null)
                {
                    await persistent[index]!.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        async Task RunWorkersAsync(int count, TimeSpan duration, bool record)
        {
            var issue = duration > TimeSpan.Zero ? Stopwatch.StartNew() : null;
            var phaseCount = 0;
            var workers = new Task[plan.Concurrency];
            for (var index = 0; index < workers.Length; index++)
            {
                var workerIndex = index;
                workers[index] = Task.Run(async () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        if (issue is not null && issue.Elapsed >= duration)
                        {
                            break;
                        }

                        var phaseId = Interlocked.Increment(ref phaseCount);
                        if (phaseId > count)
                        {
                            break;
                        }

                        var articleIdNumber = Interlocked.Increment(ref articleSequence);

                        ArticleConnection? owned = null;
                        var connection = persistent[workerIndex];
                        if (fresh || connection is null || !connection.IsOpen)
                        {
                            owned = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                            connection = owned;
                            if (record)
                            {
                                connectTimes.Add(connection.ConnectElapsed.TotalMilliseconds);
                            }

                            if (!fresh)
                            {
                                persistent[workerIndex] = connection;
                                owned = null;
                            }
                        }

                        try
                        {
                            await TransferOneAsync(connection, articleIdNumber, record).ConfigureAwait(false);
                        }
                        finally
                        {
                            if (owned is not null)
                            {
                                await owned.DisposeAsync().ConfigureAwait(false);
                            }
                        }
                    }
                }, cancellationToken);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }

        async Task TransferOneAsync(ArticleConnection connection, int id, bool record)
        {
            var articleId = plan.Seed ^ (ulong)(uint)id;
            var now = Interlocked.Increment(ref outstanding);
            UpdateMax(now);
            var started = Stopwatch.GetTimestamp();
            try
            {
                ArticleConnection active = connection;
                var opened = false;
                if (!active.IsOpen)
                {
                    active = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    opened = true;
                    if (record)
                    {
                        connectTimes.Add(active.ConnectElapsed.TotalMilliseconds);
                    }
                }

                try
                {
                    var result = await active.PutAsync(articleId, (ulong)id, payload, payloadHash, cancellationToken).ConfigureAwait(false);
                    var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (fresh)
                    {
                        elapsedMs += active.ConnectElapsed.TotalMilliseconds;
                    }

                    if (!record)
                    {
                        if (result.Kind != ArticleTransferKind.Succeeded)
                        {
                            Interlocked.Increment(ref warmupFailures);
                            failures.Enqueue("warmup " + articleId.ToString("x16") + " " + result.Status);
                        }

                        return;
                    }

                    if (result.Kind == ArticleTransferKind.Succeeded)
                    {
                        if (!CryptographicOperations.FixedTimeEquals(result.Sha256, payloadHash))
                        {
                            Interlocked.Increment(ref mismatches);
                            Interlocked.Increment(ref failed);
                            failures.Enqueue(articleId.ToString("x16") + " hash mismatch");
                            return;
                        }

                        latencies.Add(elapsedMs);
                        Interlocked.Increment(ref success);
                        if (storeFiles && plan.KeepFiles)
                        {
                            var published = Path.Combine(root, articleId.ToString("x16") + ".bin");
                            if (!File.Exists(published) || new FileInfo(published).Length != payload.Length)
                            {
                                Interlocked.Increment(ref mismatches);
                            }
                        }
                    }
                    else if (result.Kind == ArticleTransferKind.Failed)
                    {
                        Interlocked.Increment(ref failed);
                        if (result.Status == ArticleStatus.HashMismatch)
                        {
                            Interlocked.Increment(ref mismatches);
                        }

                        failures.Enqueue(articleId.ToString("x16") + " " + result.Status);
                    }
                    else
                    {
                        Interlocked.Increment(ref incomplete);
                        failures.Enqueue(articleId.ToString("x16") + " " + result.Kind);
                    }
                }
                finally
                {
                    if (opened)
                    {
                        await active.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref outstanding);
            }
        }

        void UpdateMax(int now)
        {
            int previous;
            do
            {
                previous = Volatile.Read(ref maxOutstanding);
                if (now <= previous)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref maxOutstanding, now, previous) != previous);
        }
    }

    /// <summary>Nearest-rank percentile of a sorted sample. An empty sample is zero.</summary>
    public static double Percentile(double[] sorted, double rank)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(rank * sorted.Length) - 1;
        if (index < 0)
        {
            index = 0;
        }

        if (index >= sorted.Length)
        {
            index = sorted.Length - 1;
        }

        return sorted[index];
    }

    private static string IntegrityOf(bool selfHost, bool storeFiles, int failed, int incomplete, int mismatches)
    {
        if (!selfHost)
        {
            return "server_result_not_reread_by_client";
        }

        if (failed != 0 || incomplete != 0 || mismatches != 0)
        {
            return "verification_failures";
        }

        return storeFiles ? "published_and_hashed" : "hashed_in_memory";
    }

    private static void TryDeleteFiles(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void WriteHuman(TextWriter output, BenchmarkReport report, string results)
    {
        output.WriteLine($"results={results}");
        output.WriteLine($"host={report.Host}:{report.Port} selfHost={report.SelfHosted} mode={report.Mode} connections={report.Connections} payload={report.PayloadBytes} {report.PayloadKind} concurrency={report.Concurrency} reread={report.Reread}");
        output.WriteLine($"success={report.SuccessfulTransfers} failed={report.FailedTransfers} incomplete={report.IncompleteTransfers} mismatches={report.VerificationFailures}");
        output.WriteLine($"payload={report.PayloadMebibytesPerSecond:F2} MiB/s ({report.PayloadGigabitsPerSecond:F3} Gbit/s) articles/s={report.TransfersPerSecond:F2} elapsed={report.ElapsedSeconds:F3}s");
        output.WriteLine($"latency_ms mean={report.LatencyMeanMilliseconds:F2} median={report.LatencyMedianMilliseconds:F2} p95={report.LatencyP95Milliseconds:F2} p99={report.LatencyP99Milliseconds:F2} connect_mean_ms={report.ConnectMeanMilliseconds:F3} connect_in_latency={report.ConnectIncludedInLatency}");
        output.WriteLine($"cpu_s={report.CpuProcessorSeconds:F3} cpu_one_core={report.CpuPercentOfOneCore:F1}% cpu_machine={report.CpuPercentOfMachine:F1}% allocated={report.AllocatedBytes} max_outstanding={report.MaxOutstanding}");
        output.WriteLine($"integrity={report.Integrity} sha256={report.PayloadSha256}");
        output.WriteLine("payload throughput counts completed article bytes only. Loopback is not NIC line rate.");
        output.WriteLine(report.SelfHosted
            ? "CPU figures are the single self-host process and include both peers."
            : "CPU figures are this client process only. The server process is not included.");
    }
}
