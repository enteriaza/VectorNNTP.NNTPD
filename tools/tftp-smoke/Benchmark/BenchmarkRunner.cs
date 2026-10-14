using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VectorNNTP.TftpSmoke.Payload;
using VectorNNTP.TftpSmoke.Protocol;
using VectorNNTP.TftpSmoke.Transfer;
using VectorNNTP.TftpSmoke.Transport;

namespace VectorNNTP.TftpSmoke.Benchmark;

/// <summary>One self-contained or client-only TFTP upload run.</summary>
public sealed class BenchmarkPlan
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; }

    public bool SelfHost { get; init; } = true;

    public int PayloadBytes { get; init; } = ArticlePayload.DefaultBytes;

    public string PayloadKind { get; init; } = "random";

    public int Concurrency { get; init; } = 1;

    public int Transfers { get; init; } = 16;

    public int WarmupTransfers { get; init; } = 4;

    public int DurationSeconds { get; init; }

    public int BlockSize { get; init; } = TftpLimits.EthernetSafeBlockSize;

    public int WindowSize { get; init; } = 16;

    public int TimeoutMilliseconds { get; init; } = 250;

    public int MaxTimeouts { get; init; } = 6;

    public string ResultsDirectory { get; init; } = "";

    public bool KeepFiles { get; init; }

    public ulong Seed { get; init; } = 1;

    public long MaxArticleBytes { get; init; } = 8 * 1024 * 1024;

    public int ServerMaxTransfers { get; init; } = 256;

    /// <summary>When false, the server hashes the payload and does not create a file. The default measures the file path.</summary>
    public bool StoreFiles { get; init; } = true;

    /// <summary>When true, the report includes summed send, receive, file, hash, and ACK-wait durations.</summary>
    public bool Diagnose { get; init; }
}

/// <summary>Machine-readable summary of one measured interval.</summary>
public sealed class BenchmarkReport
{
    public string Schema { get; init; } = "vectornntp.tftp-smoke.v1";

    public DateTimeOffset StartedUtc { get; init; }

    public string Host { get; init; } = "";

    public int Port { get; init; }

    public bool SelfHosted { get; init; }

    public int PayloadBytes { get; init; }

    public string PayloadKind { get; init; } = "";

    public string PayloadSha256 { get; init; } = "";

    public ulong Seed { get; init; }

    public int RequestedBlockSize { get; init; }

    public int RequestedWindowSize { get; init; }

    public int Concurrency { get; init; }

    public int WarmupTransfers { get; init; }

    public int WarmupFailures { get; init; }

    public int MeasuredAttempts { get; init; }

    public int SuccessfulTransfers { get; init; }

    public int FailedTransfers { get; init; }

    public int HashMismatches { get; init; }

    public long Retries { get; init; }

    public long Timeouts { get; init; }

    public long PayloadBytesTransferred { get; init; }

    public double ElapsedSeconds { get; init; }

    public double TransfersPerSecond { get; init; }

    public double PayloadMebibytesPerSecond { get; init; }

    public double PayloadGigabitsPerSecond { get; init; }

    public long ClientWireBytesSent { get; init; }

    public long ClientWireBytesReceived { get; init; }

    public long ServerWireBytesSent { get; init; }

    public long ServerWireBytesReceived { get; init; }

    public double ClientWireMebibytesPerSecond { get; init; }

    public string ThroughputBasis { get; init; } = "payload bytes divided by measured wall time; wire figures are TFTP datagram bytes and exclude IP and UDP headers";

    public double LatencyMeanMilliseconds { get; init; }

    public double LatencyMedianMilliseconds { get; init; }

    public double LatencyP95Milliseconds { get; init; }

    public double LatencyP99Milliseconds { get; init; }

    public double CpuProcessorSeconds { get; init; }

    public double CpuPercentOfOneCore { get; init; }

    public double CpuPercentOfMachine { get; init; }

    public long AllocatedBytes { get; init; }

    public int Gen0Collections { get; init; }

    public int Gen1Collections { get; init; }

    public int Gen2Collections { get; init; }

    public int MaxOutstanding { get; init; }

    public int ProcessorCount { get; init; }

    public string Runtime { get; init; } = "";

    public string Os { get; init; } = "";

    public string Integrity { get; init; } = "";

    public bool KeepFiles { get; init; }

    public long LingerSkipped { get; init; }

    public string[] Failures { get; init; } = [];

    /// <summary>False when <see cref="BenchmarkPlan.StoreFiles"/> is false. Memory mode is not an end-to-end file result.</summary>
    public bool StoredFiles { get; init; } = true;

    /// <summary>DATA datagrams a lossless transfer of this payload and block size must send, times the successful transfers.</summary>
    public long MinimumDataDatagrams { get; init; }

    /// <summary>Window ACKs a lossless transfer must receive, times the successful transfers. The OACK is not included.</summary>
    public long MinimumAckDatagrams { get; init; }

    /// <summary>Client DATA datagrams sent for the first time.</summary>
    public long ClientDataDatagramsSent { get; init; }

    /// <summary>Client DATA datagrams sent again after a timeout or duplicate ACK.</summary>
    public long ClientDataDatagramsRetransmitted { get; init; }

    /// <summary>ACK datagrams the client received, including duplicates. The OACK is not included.</summary>
    public long ClientAckDatagramsReceived { get; init; }

    /// <summary>DATA datagrams the server accepted into a receive window.</summary>
    public long ServerDataDatagramsReceived { get; init; }

    /// <summary>DATA datagrams the server ignored as duplicates.</summary>
    public long ServerDuplicateDataDatagrams { get; init; }

    /// <summary>DATA datagrams the server could not place in the current window.</summary>
    public long ServerOutOfWindowDatagrams { get; init; }

    /// <summary>ACK datagrams the server sent.</summary>
    public long ServerAckDatagramsSent { get; init; }

    /// <summary>Handshake timer fires on both peers. These are not DATA retransmissions.</summary>
    public long HandshakeTimeouts { get; init; }

    /// <summary>Managed heap size from <see cref="GC.GetGCMemoryInfo"/> at the end of the interval. Zero when diagnostics are off. This is a sample, not a peak.</summary>
    public long GcHeapBytes { get; init; }

    /// <summary>Process working set sampled at the end of the measured interval.</summary>
    public long WorkingSetBytes { get; init; }

    /// <summary>Populated when <see cref="BenchmarkPlan.Diagnose"/> is set. Null on an ordinary run.</summary>
    public DiagnosticSnapshot? Diagnostics { get; init; }
}

/// <summary>Runs warmup and a measured upload interval against a loopback server or an already running server.</summary>
public static class BenchmarkRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Executes the plan and writes <c>summary.json</c> under the results directory.</summary>
    public static async Task<BenchmarkReport> RunAsync(BenchmarkPlan plan, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        var results = string.IsNullOrWhiteSpace(plan.ResultsDirectory)
            ? Path.Combine("artifacts", "tftp-smoke", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ"))
            : plan.ResultsDirectory;
        Directory.CreateDirectory(results);
        var startedUtc = DateTimeOffset.UtcNow;
        var payload = plan.PayloadKind.Equals("article", StringComparison.OrdinalIgnoreCase)
            ? ArticlePayload.CreateArticle(plan.PayloadBytes, plan.Seed)
            : ArticlePayload.CreatePseudorandom(plan.PayloadBytes, plan.Seed);
        var payloadHash = SHA256.HashData(payload);
        var expected = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var failures = new ConcurrentQueue<string>();
        var latencies = new ConcurrentBag<double>();
        var success = 0;
        var failed = 0;
        var mismatches = 0;
        var retries = 0L;
        var timeouts = 0L;
        var clientSent = 0L;
        var clientReceived = 0L;
        var clientData = 0L;
        var clientRetransmitted = 0L;
        var clientAcks = 0L;
        var clientHandshakeTimeouts = 0L;
        var diagnostics = plan.Diagnose ? new TransferDiagnostics() : null;
        var outstanding = 0;
        var maxOutstanding = 0;
        var warmupFailures = 0;

        var timing = new TftpTiming
        {
            AcknowledgementTimeout = TimeSpan.FromMilliseconds(plan.TimeoutMilliseconds),
            MaxTimeouts = plan.MaxTimeouts,
            FinalAckLinger = TimeSpan.FromMilliseconds(plan.TimeoutMilliseconds),
        };
        var factory = new UdpDatagramTransportFactory();
        var client = new TftpClient(factory, IPAddress.Parse(plan.Host), timing, diagnostics);
        TftpServer? server = null;
        Task? serverTask = null;
        using var serverStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var root = Path.Combine(results, "files");
        if (plan.SelfHost)
        {
            Directory.CreateDirectory(root);
            server = new TftpServer(new TftpServerOptions
            {
                RootDirectory = root,
                BindAddress = IPAddress.Parse(plan.Host),
                Port = plan.Port,
                MaxConcurrentTransfers = plan.ServerMaxTransfers,
                MaxArticleBytes = plan.MaxArticleBytes,
                MaxBlockSize = plan.BlockSize,
                MaxWindowSize = plan.WindowSize,
                Timing = timing,
                DiscardBody = !plan.StoreFiles,
                Diagnostics = diagnostics,
                OnReceived = async (article, token) =>
                {
                    if (!expected.ContainsKey(article.FileName)
                        || article.Length != payload.Length
                        || !CryptographicOperations.FixedTimeEquals(article.Sha256, payloadHash))
                    {
                        Interlocked.Increment(ref mismatches);
                        return;
                    }

                    var verifyStart = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                    if (!plan.StoreFiles)
                    {
                        article.AcceptVerifiedHash();
                    }
                    else
                    {
                        if (!await RereadMatchesAsync(article.TemporaryPath, payloadHash, token).ConfigureAwait(false))
                        {
                            Interlocked.Increment(ref mismatches);
                            return;
                        }

                        article.Publish();
                        if (!plan.KeepFiles)
                        {
                            File.Delete(article.FinalPath);
                        }
                    }

                    if (diagnostics is not null)
                    {
                        diagnostics.AddVerify(Stopwatch.GetTimestamp() - verifyStart);
                    }
                },
            }, factory);
            serverTask = server.RunAsync(serverStop.Token);
            await server.Started.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var port = server?.LocalEndPoint?.Port ?? plan.Port;
        var remote = new IPEndPoint(IPAddress.Parse(plan.Host), port);
        try
        {
            await RunWorkersAsync(plan.WarmupTransfers, TimeSpan.Zero, record: false).ConfigureAwait(false);
            if (Volatile.Read(ref warmupFailures) > 0)
            {
                throw new InvalidOperationException($"Warmup failed {warmupFailures} transfer(s). Measured interval was not started.");
            }

            var serverSentBaseline = server?.Counters.WireBytesSent ?? 0;
            var serverReceivedBaseline = server?.Counters.WireBytesReceived ?? 0;
            var serverRetriesBaseline = server?.Counters.Retries ?? 0;
            var serverTimeoutsBaseline = server?.Counters.Timeouts ?? 0;
            var serverDataBaseline = server?.Counters.DataDatagramsReceived ?? 0;
            var serverDuplicateBaseline = server?.Counters.DuplicateDataDatagrams ?? 0;
            var serverOutOfWindowBaseline = server?.Counters.OutOfWindowDatagrams ?? 0;
            var serverAckBaseline = server?.Counters.AckDatagramsSent ?? 0;
            var serverHandshakeBaseline = server?.Counters.HandshakeTimeouts ?? 0;
            var lingerBaseline = server?.Counters.LingerSkipped ?? 0;

            var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuStart = process.TotalProcessorTime;
            var allocatedStart = GC.GetTotalAllocatedBytes(precise: false);
            var gen0Start = GC.CollectionCount(0);
            var gen1Start = GC.CollectionCount(1);
            var gen2Start = GC.CollectionCount(2);
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
            var cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
            var elapsed = wall.Elapsed.TotalSeconds;
            var safeElapsed = elapsed <= 0 ? double.Epsilon : elapsed;
            var transferred = (long)success * payload.Length;
            var attempts = success + failed;
            var serverSent = (server?.Counters.WireBytesSent ?? 0) - serverSentBaseline;
            var serverReceived = (server?.Counters.WireBytesReceived ?? 0) - serverReceivedBaseline;
            retries += (server?.Counters.Retries ?? 0) - serverRetriesBaseline;
            timeouts += (server?.Counters.Timeouts ?? 0) - serverTimeoutsBaseline;
            var minimumDataPerTransfer = BenchmarkMath.MinimumDataDatagrams(payload.Length, plan.BlockSize);
            var minimumAckPerTransfer = BenchmarkMath.MinimumAcknowledgements(minimumDataPerTransfer, plan.WindowSize);
            var latency = latencies.ToArray();
            Array.Sort(latency);
            var report = new BenchmarkReport
            {
                StartedUtc = startedUtc,
                Host = plan.Host,
                Port = port,
                SelfHosted = plan.SelfHost,
                PayloadBytes = payload.Length,
                PayloadKind = plan.PayloadKind,
                PayloadSha256 = ArticlePayload.Sha256Hex(payloadHash),
                Seed = plan.Seed,
                RequestedBlockSize = plan.BlockSize,
                RequestedWindowSize = plan.WindowSize,
                Concurrency = plan.Concurrency,
                WarmupTransfers = plan.WarmupTransfers,
                WarmupFailures = warmupFailures,
                MeasuredAttempts = attempts,
                SuccessfulTransfers = success,
                FailedTransfers = failed,
                HashMismatches = mismatches,
                Retries = retries,
                Timeouts = timeouts,
                PayloadBytesTransferred = transferred,
                ElapsedSeconds = elapsed,
                TransfersPerSecond = success / safeElapsed,
                PayloadMebibytesPerSecond = transferred / safeElapsed / (1024d * 1024d),
                PayloadGigabitsPerSecond = transferred * 8d / safeElapsed / 1_000_000_000d,
                ClientWireBytesSent = clientSent,
                ClientWireBytesReceived = clientReceived,
                ServerWireBytesSent = serverSent,
                ServerWireBytesReceived = serverReceived,
                ClientWireMebibytesPerSecond = (clientSent + clientReceived) / safeElapsed / (1024d * 1024d),
                LatencyMeanMilliseconds = latency.Length == 0 ? 0 : latency.Average(),
                LatencyMedianMilliseconds = Percentile(latency, 0.50),
                LatencyP95Milliseconds = Percentile(latency, 0.95),
                LatencyP99Milliseconds = Percentile(latency, 0.99),
                CpuProcessorSeconds = cpuSeconds,
                CpuPercentOfOneCore = elapsed <= 0 ? 0 : cpuSeconds / elapsed * 100d,
                CpuPercentOfMachine = elapsed <= 0 ? 0 : cpuSeconds / elapsed / Environment.ProcessorCount * 100d,
                AllocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedStart,
                Gen0Collections = GC.CollectionCount(0) - gen0Start,
                Gen1Collections = GC.CollectionCount(1) - gen1Start,
                Gen2Collections = GC.CollectionCount(2) - gen2Start,
                MaxOutstanding = maxOutstanding,
                ProcessorCount = Environment.ProcessorCount,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Integrity = !plan.SelfHost
                    ? "not_verified_external_server"
                    : mismatches == 0 && failed == 0
                        ? plan.StoreFiles ? "all_verified" : "hash_verified_memory"
                        : "verification_failures",
                KeepFiles = plan.KeepFiles,
                LingerSkipped = (server?.Counters.LingerSkipped ?? 0) - lingerBaseline,
                Failures = failures.Take(20).ToArray(),
                StoredFiles = plan.StoreFiles,
                MinimumDataDatagrams = (long)minimumDataPerTransfer * success,
                MinimumAckDatagrams = (long)minimumAckPerTransfer * success,
                ClientDataDatagramsSent = clientData,
                ClientDataDatagramsRetransmitted = clientRetransmitted,
                ClientAckDatagramsReceived = clientAcks,
                ServerDataDatagramsReceived = (server?.Counters.DataDatagramsReceived ?? 0) - serverDataBaseline,
                ServerDuplicateDataDatagrams = (server?.Counters.DuplicateDataDatagrams ?? 0) - serverDuplicateBaseline,
                ServerOutOfWindowDatagrams = (server?.Counters.OutOfWindowDatagrams ?? 0) - serverOutOfWindowBaseline,
                ServerAckDatagramsSent = (server?.Counters.AckDatagramsSent ?? 0) - serverAckBaseline,
                HandshakeTimeouts = clientHandshakeTimeouts + ((server?.Counters.HandshakeTimeouts ?? 0) - serverHandshakeBaseline),
                GcHeapBytes = plan.Diagnose ? GC.GetGCMemoryInfo().HeapSizeBytes : 0,
                WorkingSetBytes = plan.Diagnose ? process.WorkingSet64 : 0,
                Diagnostics = diagnostics?.Snapshot(),
            };

            var json = JsonSerializer.Serialize(report, JsonOptions);
            await File.WriteAllTextAsync(Path.Combine(results, "summary.json"), json, cancellationToken).ConfigureAwait(false);
            WriteHuman(output, report, results);
            output.WriteLine();
            output.WriteLine(json);
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
        }

        async Task RunWorkersAsync(int count, TimeSpan duration, bool record)
        {
            var next = 0;
            var issue = duration > TimeSpan.Zero ? Stopwatch.StartNew() : null;
            var workers = new Task[plan.Concurrency];
            for (var worker = 0; worker < workers.Length; worker++)
            {
                workers[worker] = Task.Run(async () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        if (issue is not null && issue.Elapsed >= duration)
                        {
                            break;
                        }

                        var id = Interlocked.Increment(ref next);
                        if (issue is null && id > count)
                        {
                            break;
                        }

                        if (issue is not null && id > count)
                        {
                            break;
                        }

                        await TransferOneAsync(id, record).ConfigureAwait(false);
                    }
                }, cancellationToken);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }

        async Task TransferOneAsync(int id, bool record)
        {
            var name = (record ? "m-" : "w-") + $"{plan.Seed:x16}-{id:x8}.bin";
            expected[name] = 0;
            var now = Interlocked.Increment(ref outstanding);
            UpdateMax(now);
            var started = Stopwatch.GetTimestamp();
            try
            {
                var result = await client.UploadAsync(remote, name, payload, plan.BlockSize, plan.WindowSize, cancellationToken).ConfigureAwait(false);
                var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (!record)
                {
                    if (result.Status != TftpTransferStatus.Succeeded)
                    {
                        Interlocked.Increment(ref warmupFailures);
                        failures.Enqueue("warmup " + name + " " + result.ErrorMessage);
                    }

                    return;
                }

                Interlocked.Add(ref retries, result.Retries);
                Interlocked.Add(ref timeouts, result.Timeouts);
                Interlocked.Add(ref clientSent, result.WireBytesSent);
                Interlocked.Add(ref clientReceived, result.WireBytesReceived);
                Interlocked.Add(ref clientData, result.DataDatagramsSent);
                Interlocked.Add(ref clientRetransmitted, result.DataDatagramsRetransmitted);
                Interlocked.Add(ref clientAcks, result.AckDatagramsReceived);
                Interlocked.Add(ref clientHandshakeTimeouts, result.HandshakeTimeouts);
                if (BenchmarkMath.CountsTowardPayload(result.Status))
                {
                    latencies.Add(elapsedMs);
                    Interlocked.Increment(ref success);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                    failures.Enqueue(name + " " + result.ErrorMessage);
                }
            }
            finally
            {
                Interlocked.Decrement(ref outstanding);
                expected.TryRemove(name, out _);
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

    private static async ValueTask<bool> RereadMatchesAsync(string path, byte[] expectedHash, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, read));
            }

            var actual = hash.GetHashAndReset();
            return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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

    private static void WriteHuman(TextWriter output, BenchmarkReport report, string results)
    {
        output.WriteLine($"results={results}");
        output.WriteLine($"host={report.Host}:{report.Port} selfHost={report.SelfHosted} payload={report.PayloadBytes} {report.PayloadKind} block={report.RequestedBlockSize} window={report.RequestedWindowSize} concurrency={report.Concurrency}");
        output.WriteLine($"success={report.SuccessfulTransfers} failed={report.FailedTransfers} mismatches={report.HashMismatches} retries={report.Retries} timeouts={report.Timeouts} handshake_timeouts={report.HandshakeTimeouts}");
        output.WriteLine($"payload={report.PayloadMebibytesPerSecond:F2} MiB/s ({report.PayloadGigabitsPerSecond:F3} Gbit/s) transfers/s={report.TransfersPerSecond:F2} elapsed={report.ElapsedSeconds:F3}s");
        output.WriteLine($"wire_client_sent={report.ClientWireBytesSent} wire_client_received={report.ClientWireBytesReceived} wire_server_sent={report.ServerWireBytesSent} wire_server_received={report.ServerWireBytesReceived}");
        output.WriteLine($"latency_ms mean={report.LatencyMeanMilliseconds:F2} median={report.LatencyMedianMilliseconds:F2} p95={report.LatencyP95Milliseconds:F2} p99={report.LatencyP99Milliseconds:F2}");
        output.WriteLine($"cpu_s={report.CpuProcessorSeconds:F3} cpu_one_core={report.CpuPercentOfOneCore:F1}% cpu_machine={report.CpuPercentOfMachine:F1}% allocated={report.AllocatedBytes} max_outstanding={report.MaxOutstanding}");
        output.WriteLine($"integrity={report.Integrity} storedFiles={report.StoredFiles} sha256={report.PayloadSha256}");
        output.WriteLine($"data_sent={report.ClientDataDatagramsSent} data_retransmitted={report.ClientDataDatagramsRetransmitted} data_minimum={report.MinimumDataDatagrams} server_data_received={report.ServerDataDatagramsReceived} server_duplicates={report.ServerDuplicateDataDatagrams} acks_received={report.ClientAckDatagramsReceived} acks_minimum={report.MinimumAckDatagrams}");
        if (report.Diagnostics is { } diagnostic)
        {
            output.WriteLine(
                $"diag_ms timer_setup={diagnostic.TimerSetupMilliseconds:F0} socket_send={diagnostic.SocketSendMilliseconds:F0} socket_recv={diagnostic.SocketReceiveMilliseconds:F0} file_write={diagnostic.FileWriteMilliseconds:F0} hash={diagnostic.HashMilliseconds:F0} verify={diagnostic.VerifyMilliseconds:F0} flush={diagnostic.FlushMilliseconds:F0} commit={diagnostic.CommitMilliseconds:F0} ack_wait={diagnostic.AckWaitMilliseconds:F0}");
            output.WriteLine(
                $"windows={diagnostic.WindowCount} mean_blocks_per_window={diagnostic.MeanBlocksPerWindow:F2} send_fraction={diagnostic.EstimatedSendFraction:F3} ack_wait_mean_ms={diagnostic.AckWaitMeanMilliseconds:F3} ack_wait_p50_bucket_ms={diagnostic.AckWaitP50Milliseconds:F3} ack_wait_p95_bucket_ms={diagnostic.AckWaitP95Milliseconds:F3}");
            output.WriteLine("diagnostic milliseconds are sums across overlapping transfers, not wall-clock time.");
        }

        output.WriteLine("payload throughput counts article bytes only. It is not NIC line rate. Wire byte counts exclude IP and UDP headers.");
    }
}
