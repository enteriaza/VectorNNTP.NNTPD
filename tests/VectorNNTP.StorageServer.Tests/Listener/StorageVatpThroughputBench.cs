using System.Buffers;
using System.Diagnostics;
using System.Runtime;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Acme;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Ingress;
using VectorNNTP.StorageServer.Listener;
using VectorNNTP.StorageServer.Tests.Fixtures;
using VectorNNTP.StorageServer.Tests.TestDoubles;
using Xunit.Abstractions;

namespace VectorNNTP.StorageServer.Tests.Listener;

/// <summary>
/// End-to-end VATP STORE benchmark. The sustained sweep runs only when
/// <c>VECTORNNTP_STORAGE_BENCH=1</c>. The single-article fact always runs.
/// </summary>
public sealed class StorageVatpThroughputBench
{
    public const int BodyBytes = 768_000;
    private const int InitialWindowBytes = 256 * 1024;
    private const int MaxFramePayload = 64 * 1024;
    private const long QueueByteCap = 256L * 1024 * 1024;
    private const string PathIdentity = "cache01.usenet.ninja";

    private readonly ITestOutputHelper _output;

    public StorageVatpThroughputBench(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task One_tls_article_is_accepted_and_path_processed()
    {
        var prepared = Prepare(Encoding.ASCII.GetBytes("body-line\r\n"), "<one-tls@bench.test>", 1);
        byte[]? rewritten = null;
        await using var host = await BenchHost.StartAsync(seen => rewritten = seen.ToArray());
        await using var session = await VatpLoadSession.ConnectAsync(host.Port);
        await session.SendArticlesAsync([prepared], count: 1, CancellationToken.None);

        var drained = await WaitForProcessedAsync(host, expected: 1, TimeSpan.FromSeconds(10));
        Assert.True(drained);
        var processor = host.Processor ?? throw new InvalidOperationException("Path worker was not started.");
        Assert.Equal(1, session.Accepted);
        Assert.Equal(0, session.Rejected);
        Assert.Equal(1, processor.Processed);
        Assert.Equal(0, processor.Rejected);
        Assert.Equal(0, host.Queue.EnqueuedCount - host.Queue.DequeuedCount);
        Assert.NotNull(rewritten);
        var text = Encoding.ASCII.GetString(rewritten);
        Assert.Contains(PathIdentity, text, StringComparison.Ordinal);
        Assert.Contains("body-line\r\n", text, StringComparison.Ordinal);
        Assert.Contains("nntpd01.usenet.ninja", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sustained_vatp_path_when_bench_enabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("VECTORNNTP_STORAGE_BENCH"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var sessions = ParseSessions(Environment.GetEnvironmentVariable("VECTORNNTP_STORAGE_BENCH_SESSIONS"));
        var measureSeconds = ParsePositive(Environment.GetEnvironmentVariable("VECTORNNTP_STORAGE_BENCH_SECONDS"), 8);
        var warmupSeconds = ParsePositive(Environment.GetEnvironmentVariable("VECTORNNTP_STORAGE_BENCH_WARMUP"), 2);
        var pool = BuildPool(16);
        var artSize = pool[0].ArtSize;
        var report = new StringBuilder();
        report.AppendLine($"bodyBytes={BodyBytes} artSize={artSize} pool={pool.Length} measureSeconds={measureSeconds} warmupSeconds={warmupSeconds}");
        report.AppendLine($"runtime={Environment.Version} os={Environment.OSVersion} processors={Environment.ProcessorCount} serverGc={GCSettings.IsServerGC}");

        foreach (var sessionCount in sessions)
        {
            var line = await RunLevelAsync(pool, sessionCount, warmupSeconds, measureSeconds);
            report.AppendLine(line);
            _output.WriteLine(line);
        }

        await WriteReportAsync(report, "vatp-bench-raw.txt");
    }

    [Fact]
    public async Task Ingress_enqueue_when_bench_enabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("VECTORNNTP_STORAGE_BENCH"), "1", StringComparison.Ordinal))
        {
            return;
        }

        ThreadPool.GetMinThreads(out var workerThreads, out var ioThreads);
        var minThreads = Math.Max(workerThreads, Environment.ProcessorCount * 8);
        ThreadPool.SetMinThreads(minThreads, Math.Max(ioThreads, minThreads));
        var sessions = ParseSessions(Environment.GetEnvironmentVariable("VECTORNNTP_STORAGE_BENCH_SESSIONS"));
        var measureSeconds = ParsePositive(Environment.GetEnvironmentVariable("VECTORNNTP_STORAGE_BENCH_SECONDS"), 8);
        var warmupSeconds = ParsePositive(Environment.GetEnvironmentVariable("VECTORNNTP_STORAGE_BENCH_WARMUP"), 2);
        var pool = BuildPool(16);
        var report = new StringBuilder();
        report.AppendLine("mode=ingress-enqueue boundary=AcceptedArticleQueue.TryEnqueue pathWorker=false");
        report.AppendLine($"threadPoolMin={minThreads}");
        report.AppendLine($"bodyBytes={BodyBytes} artSize={pool[0].ArtSize} pool={pool.Length} measureSeconds={measureSeconds} warmupSeconds={warmupSeconds}");
        report.AppendLine($"runtime={Environment.Version} os={Environment.OSVersion} processors={Environment.ProcessorCount} serverGc={GCSettings.IsServerGC}");

        foreach (var sessionCount in sessions)
        {
            var line = await RunLevelAsync(pool, sessionCount, warmupSeconds, measureSeconds, ingressOnly: true);
            report.AppendLine(line);
            _output.WriteLine(line);
        }

        await WriteReportAsync(report, "vatp-ingress-raw.txt");
    }

    private async Task WriteReportAsync(StringBuilder report, string fileName)
    {
        var directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            ".artifacts", "experiments"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, report.ToString());
        _output.WriteLine(path);
    }

    private async Task<string> RunLevelAsync(
        Prepared[] pool,
        int sessionCount,
        int warmupSeconds,
        int measureSeconds,
        bool ingressOnly = false)
    {
        await using var host = await BenchHost.StartAsync(rewritten: null, ingressOnly: ingressOnly);
        var sessions = new VatpLoadSession[sessionCount];
        for (var i = 0; i < sessions.Length; i++)
        {
            sessions[i] = await VatpLoadSession.ConnectAsync(host.Port);
        }

        using var sendCts = new CancellationTokenSource();
        var senders = sessions.Select(session => session.SendArticlesAsync(pool, count: null, sendCts.Token)).ToArray();
        var started = Stopwatch.StartNew();
        var origin = Sample.Take(host, sessions, TimeSpan.Zero);
        var samples = new List<Sample>();
        var capped = false;
        var warmup = (Sample?)null;
        var measureEnd = (Sample?)null;

        while (started.Elapsed < TimeSpan.FromSeconds(warmupSeconds + measureSeconds))
        {
            if (host.Queue.QueuedBytes > QueueByteCap)
            {
                capped = true;
                break;
            }

            samples.Add(Sample.Take(host, sessions, started.Elapsed));
            var delay = TimeSpan.FromMilliseconds(250);
            var remaining = TimeSpan.FromSeconds(warmupSeconds + measureSeconds) - started.Elapsed;
            if (remaining < delay)
            {
                delay = remaining;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            if (warmup is null && started.Elapsed >= TimeSpan.FromSeconds(warmupSeconds))
            {
                warmup = Sample.Take(host, sessions, started.Elapsed);
            }
        }

        measureEnd = Sample.Take(host, sessions, started.Elapsed);
        await sendCts.CancelAsync();
        try
        {
            await Task.WhenAll(senders);
        }
        catch (OperationCanceledException)
        {
        }

        var drained = ingressOnly
            ? await WaitForDequeuedAsync(host, host.Queue.EnqueuedCount, TimeSpan.FromSeconds(30))
            : await WaitForProcessedAsync(host, host.Queue.EnqueuedCount, TimeSpan.FromSeconds(60));
        var drainedAt = Sample.Take(host, sessions, started.Elapsed);
        double creditWait = 0;
        double resultWait = 0;
        foreach (var session in sessions)
        {
            creditWait += session.CreditWaitSeconds;
            resultWait += session.ResultWaitSeconds;
            await session.DisposeAsync();
        }

        var cold = samples.Count >= 2 ? Rate(samples[0], samples[Math.Min(4, samples.Count - 1)], pool[0].ArtSize) : "n/a";
        var steady = warmup is null
            ? "n/a"
            : Rate(warmup.Value, measureEnd.Value, pool[0].ArtSize);
        var depthMax = measureEnd.Value.Depth;
        foreach (var sample in samples)
        {
            if (sample.Depth > depthMax)
            {
                depthMax = sample.Depth;
            }
        }

        return
            $"sessions={sessionCount} capped={capped} drained={drained} cold=[{cold}] steady=[{steady}] " +
            $"depthMax={depthMax} depthAtMeasureEnd={measureEnd.Value.Depth} depthAfterDrain={drainedAt.Depth} " +
            $"enqueued={drainedAt.Enqueued} dequeued={drainedAt.Dequeued} processed={drainedAt.Processed} rejectedPath={drainedAt.PathRejected} " +
            $"accepted={drainedAt.Accepted} rejected={drainedAt.Rejected} " +
            $"gen0={drainedAt.Gen0 - origin.Gen0} gen1={drainedAt.Gen1 - origin.Gen1} gen2={drainedAt.Gen2 - origin.Gen2} " +
            $"heapBytes={drainedAt.HeapBytes} workingSet={drainedAt.WorkingSet} " +
            $"cpuMs={(drainedAt.Cpu - origin.Cpu).TotalMilliseconds:F0} workerSec={host.Processor?.WorkerSeconds ?? 0:F3} ingressOnly={ingressOnly} " +
            $"creditWaitSec={creditWait:F3} resultWaitSec={resultWait:F3}";
    }

    private static string Rate(Sample start, Sample end, int artSize)
    {
        var seconds = (end.Elapsed - start.Elapsed).TotalSeconds;
        if (seconds <= 0)
        {
            return "seconds=0";
        }

        var accepted = end.Accepted - start.Accepted;
        var enqueued = end.Enqueued - start.Enqueued;
        var processed = end.Processed - start.Processed;
        var wire = end.WireBytes - start.WireBytes;
        var payload = enqueued * (double)BodyBytes;
        var canonical = enqueued * (double)artSize;
        var processedPayload = processed * (double)BodyBytes;
        return
            $"seconds={seconds:F3} enqueuedPerSec={enqueued / seconds:F1} acceptedPerSec={accepted / seconds:F1} processedPerSec={processed / seconds:F1} " +
            $"enqueueBodyGbps={Gbps(payload, seconds):F3} enqueueArtGbps={Gbps(canonical, seconds):F3} " +
            $"processedBodyGbps={Gbps(processedPayload, seconds):F3} " +
            $"wireGbps={Gbps(wire, seconds):F3} " +
            $"depthStart={start.Depth} depthEnd={end.Depth} queuedBytesEnd={end.QueuedBytes}";
    }

    private static double Gbps(double bytes, double seconds) => bytes * 8d / seconds / 1_000_000_000d;

    private static async Task<bool> WaitForProcessedAsync(BenchHost host, long expected, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        var processor = host.Processor ?? throw new InvalidOperationException("Path worker was not started.");
        while (processor.Processed + processor.Rejected < expected)
        {
            if (deadline.Elapsed > timeout)
            {
                return false;
            }

            await Task.Delay(20);
        }

        return true;
    }

    private static async Task<bool> WaitForDequeuedAsync(BenchHost host, long expected, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (host.Queue.DequeuedCount < expected)
        {
            if (deadline.Elapsed > timeout)
            {
                return false;
            }

            await Task.Delay(20);
        }

        return true;
    }

    private static int[] ParseSessions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [1, 2, 4, 8, 16];
        }

        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static int ParsePositive(string? text, int fallback) =>
        int.TryParse(text, out var value) && value > 0 ? value : fallback;

    private static Prepared[] BuildPool(int count)
    {
        var pool = new Prepared[count];
        var body = new byte[BodyBytes];
        for (var article = 0; article < count; article++)
        {
            Array.Fill(body, (byte)'x');
            for (var line = 0; line < BodyBytes / 100; line++)
            {
                var offset = line * 100;
                body[offset] = (byte)('A' + (article % 26));
                body[offset + 1] = (byte)('0' + (line % 10));
                body[offset + 98] = (byte)'\r';
                body[offset + 99] = (byte)'\n';
            }

            var id = $"<b{article:0000}@bench.test>";
            pool[article] = Prepare(body, id, streamId: 1);
        }

        return pool;
    }

    private static Prepared Prepare(ReadOnlySpan<byte> body, string messageId, uint streamId)
    {
        var header = Encoding.ASCII.GetBytes(
            "Path: peer.example\r\n" +
            "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n" +
            "Message-ID: " + messageId + "\r\n" +
            "Newsgroups: alt.test\r\n" +
            "From: user@example.test\r\n" +
            "Subject: vatp-bench\r\n" +
            "\r\n");
        var text = new byte[header.Length + body.Length];
        header.CopyTo(text);
        body.CopyTo(text.AsSpan(header.Length));
        var parser = new NntpArticleParser("nntpd01.usenet.ninja");
        var created = ArticleRecordFactory.TryCreate(parser, text);
        if (!created.IsAccepted)
        {
            throw new InvalidOperationException($"Article build failed: {created.MaterializeFailure}");
        }

        var art = created.Record.ArtData;
        var frames = new List<byte[]>();
        var lengths = new List<int>();
        var offset = 0;
        while (offset < art.Length)
        {
            var length = Math.Min(MaxFramePayload, art.Length - offset);
            var fin = offset + length == art.Length;
            frames.Add(VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeData(streamId, art.Slice(offset, length), fin)));
            lengths.Add(length);
            offset += length;
        }

        var id = new byte[ArticleId.Length];
        created.Record.ArtId.CopyTo(id);
        var store = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeStore(streamId, id));
        var meta = ArticleCanonicalTransferMeta.FromRecord(created.Record, created.SelectedDateHeaderName);
        var metaFrame = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeMeta(streamId, VatpMetaCodec.Encode(in meta)));
        var end = VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeEnd(streamId));
        var wire = store.Length + metaFrame.Length + end.Length + frames.Sum(static frame => frame.Length);
        return new Prepared(store, metaFrame, frames.ToArray(), lengths.ToArray(), end, created.Record.ArtSize, wire);
    }

    private readonly record struct Sample(
        TimeSpan Elapsed,
        long Enqueued,
        long Dequeued,
        long QueuedBytes,
        long Processed,
        long PathRejected,
        long Accepted,
        long Rejected,
        long WireBytes,
        int Gen0,
        int Gen1,
        int Gen2,
        long HeapBytes,
        long WorkingSet,
        TimeSpan Cpu)
    {
        public long Depth => Enqueued - Dequeued;

        public static Sample Take(BenchHost host, IReadOnlyList<VatpLoadSession> sessions, TimeSpan elapsed)
        {
            long accepted = 0;
            long rejected = 0;
            long wire = 0;
            foreach (var session in sessions)
            {
                accepted += session.Accepted;
                rejected += session.Rejected;
                wire += session.WireBytes;
            }

            using var process = Process.GetCurrentProcess();
            return new Sample(
                elapsed,
                host.Queue.EnqueuedCount,
                host.Queue.DequeuedCount,
                host.Queue.QueuedBytes,
                host.Processor?.Processed ?? 0,
                host.Processor?.Rejected ?? 0,
                accepted,
                rejected,
                wire,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                GC.GetTotalMemory(forceFullCollection: false),
                process.WorkingSet64,
                process.TotalProcessorTime);
        }
    }

    private sealed record Prepared(
        byte[] Store,
        byte[] Meta,
        byte[][] Data,
        int[] DataLengths,
        byte[] End,
        int ArtSize,
        int WireBytes);

    private sealed class CountingProcessor : IAcceptedArticleProcessor
    {
        private readonly AcceptedArticlePathProcessor _inner;
        private long _processed;
        private long _rejected;
        private long _ticks;

        public CountingProcessor(Action<ReadOnlyMemory<byte>>? rewritten)
        {
            _inner = new AcceptedArticlePathProcessor(PathIdentity, NullLogger.Instance, rewritten);
        }

        public long Processed => Volatile.Read(ref _processed);

        public long Rejected => Volatile.Read(ref _rejected);

        public double WorkerSeconds => Volatile.Read(ref _ticks) / (double)Stopwatch.Frequency;

        public void EnsureReady() => _inner.EnsureReady();

        public bool TryProcess(AcceptedArticle article)
        {
            var started = Stopwatch.GetTimestamp();
            var accepted = _inner.TryProcess(article);
            Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - started);
            if (!accepted)
            {
                Interlocked.Increment(ref _rejected);
                return false;
            }

            Interlocked.Increment(ref _processed);
            return true;
        }
    }

    private sealed class BenchHost : IAsyncDisposable
    {
        private readonly StorageVatpListenerService _listener;
        private readonly AcceptedArticleWorker? _worker;
        private readonly CancellationTokenSource? _drainCts;
        private readonly Task? _drain;

        private BenchHost(
            StorageVatpListenerService listener,
            AcceptedArticleWorker? worker,
            AcceptedArticleQueue queue,
            CountingProcessor? processor,
            int port,
            CancellationTokenSource? drainCts,
            Task? drain)
        {
            _listener = listener;
            _worker = worker;
            _drainCts = drainCts;
            _drain = drain;
            Queue = queue;
            Processor = processor;
            Port = port;
        }

        public AcceptedArticleQueue Queue { get; }

        public CountingProcessor? Processor { get; }

        public int Port { get; }

        public static async Task<BenchHost> StartAsync(Action<ReadOnlyMemory<byte>>? rewritten, bool ingressOnly = false)
        {
            var port = StorageServerTestOptions.GetFreeTcpPort();
            var readiness = new AcmeCertificateReadiness();
            readiness.MarkReady();
            var queue = new AcceptedArticleQueue();
            CountingProcessor? processor = ingressOnly ? null : new CountingProcessor(rewritten);
            AcceptedArticleWorker? worker = processor is null
                ? null
                : new AcceptedArticleWorker(queue, processor, NullLogger<AcceptedArticleWorker>.Instance);
            var listener = new StorageVatpListenerService(
                CreateRuntime(port),
                TestListenerCertificates.CreatePublishedProvider(),
                NullStorageArticleOpenBoundary.Instance,
                readiness,
                NullLogger<StorageVatpListenerService>.Instance,
                queue,
                new StoreAssemblyAdmission(1024));
            await listener.StartAsync(CancellationToken.None);
            if (worker is not null)
            {
                await worker.StartAsync(CancellationToken.None);
                return new BenchHost(listener, worker, queue, processor, port, drainCts: null, drain: null);
            }

            var drainCts = new CancellationTokenSource();
            var drain = Task.Run(() => DrainAsync(queue, drainCts.Token));
            return new BenchHost(listener, worker: null, queue, processor: null, port, drainCts, drain);
        }

        public async ValueTask DisposeAsync()
        {
            await _listener.DisposeAsync();
            if (_drainCts is not null)
            {
                await _drainCts.CancelAsync();
                if (_drain is not null)
                {
                    try
                    {
                        await _drain.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (TimeoutException)
                    {
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                _drainCts.Dispose();
            }

            if (_worker is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _worker.StopAsync(stop.Token);
            }
        }

        private static async Task DrainAsync(AcceptedArticleQueue queue, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                AcceptedArticleDelivery delivery;
                try
                {
                    delivery = await queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!delivery.HasArticle)
                {
                    break;
                }
            }
        }

        private static StorageServerRuntimeOptions CreateRuntime(int tlsPort)
        {
            var identity = StorageServerTestOptions.CreateValid();
            identity.BindAddress = ["127.0.0.1"];
            identity.BindPortTls = tlsPort;
            var acme = StorageServerTestOptions.CreateValidAcme(identity);
            acme.BindAddress = ["127.0.0.1"];
            acme.BindPortTls = tlsPort;
            return StorageServerRuntimeOptionsFactory.Create(identity, acme);
        }
    }

    private sealed class VatpLoadSession : IAsyncDisposable
    {
        private readonly SslStream _ssl;
        private readonly TcpClient _tcp;
        private readonly Task _reader;
        private readonly SemaphoreSlim _creditGate = new(0);
        private readonly SemaphoreSlim _resultGate = new(0);
        private readonly CancellationTokenSource _dispose = new();
        private long _credit = InitialWindowBytes;
        private readonly byte[] _window = new byte[VatpProtocol.WindowPayloadLength];
        private readonly byte[] _outcome = new byte[1];
        private long _accepted;
        private long _rejected;
        private long _wireBytes;
        private long _creditWaitTicks;
        private long _resultWaitTicks;
        private Exception? _fault;

        private VatpLoadSession(TcpClient tcp, SslStream ssl)
        {
            _tcp = tcp;
            _ssl = ssl;
            _reader = ReadLoopAsync();
        }

        public long Accepted => Volatile.Read(ref _accepted);

        public long Rejected => Volatile.Read(ref _rejected);

        public long WireBytes => Volatile.Read(ref _wireBytes);

        public double CreditWaitSeconds => Volatile.Read(ref _creditWaitTicks) / (double)Stopwatch.Frequency;

        public double ResultWaitSeconds => Volatile.Read(ref _resultWaitTicks) / (double)Stopwatch.Frequency;

        public static async Task<VatpLoadSession> ConnectAsync(int port)
        {
            var tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.Client.SendBufferSize = 1 << 20;
            tcp.Client.ReceiveBufferSize = 1 << 20;
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
                CancellationToken.None);
            var session = new VatpLoadSession(tcp, ssl);
            await session.WriteAsync(VatpFrameEncoder.ToSingleBuffer(
                VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload)));
            if (!await session._resultGate.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("StorageServer did not answer HELLO.");
            }

            if (session._fault is not null)
            {
                throw session._fault;
            }

            return session;
        }

        public async Task SendArticlesAsync(IReadOnlyList<Prepared> pool, int? count, CancellationToken cancellationToken)
        {
            var sent = 0;
            var index = 0;
            while (count is null || sent < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_fault is not null)
                {
                    throw _fault;
                }

                var article = pool[index];
                index = (index + 1) % pool.Count;
                Volatile.Write(ref _credit, InitialWindowBytes);
                await WriteAsync(article.Store);
                await WriteAsync(article.Meta);
                for (var frame = 0; frame < article.Data.Length; frame++)
                {
                    await WaitForCreditAsync(article.DataLengths[frame], cancellationToken);
                    await WriteAsync(article.Data[frame]);
                }

                await WriteAsync(article.End);
                var resultWait = Stopwatch.GetTimestamp();
                await _resultGate.WaitAsync(cancellationToken);
                Interlocked.Add(ref _resultWaitTicks, Stopwatch.GetTimestamp() - resultWait);
                if (_fault is not null)
                {
                    throw _fault;
                }

                sent++;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _dispose.CancelAsync();
            try
            {
                await _reader;
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }

            await _ssl.DisposeAsync();
            _tcp.Dispose();
            _dispose.Dispose();
            _creditGate.Dispose();
            _resultGate.Dispose();
        }

        private async Task WaitForCreditAsync(int bytes, CancellationToken cancellationToken)
        {
            var waiting = false;
            var started = 0L;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_fault is not null)
                {
                    throw _fault;
                }

                var current = Volatile.Read(ref _credit);
                if (current >= bytes && Interlocked.CompareExchange(ref _credit, current - bytes, current) == current)
                {
                    if (waiting)
                    {
                        Interlocked.Add(ref _creditWaitTicks, Stopwatch.GetTimestamp() - started);
                    }

                    return;
                }

                if (!waiting)
                {
                    waiting = true;
                    started = Stopwatch.GetTimestamp();
                }

                await _creditGate.WaitAsync(cancellationToken);
            }
        }

        private async Task WriteAsync(byte[] frame)
        {
            await _ssl.WriteAsync(frame);
            Interlocked.Add(ref _wireBytes, frame.Length);
        }

        private async Task ReadLoopAsync()
        {
            var buffer = new byte[256 * 1024];
            var filled = 0;
            try
            {
                while (!_dispose.IsCancellationRequested)
                {
                    var read = await _ssl.ReadAsync(buffer.AsMemory(filled), _dispose.Token);
                    if (read == 0)
                    {
                        Fault(new IOException("VATP peer closed the connection."));
                        return;
                    }

                    filled += read;
                    var offset = 0;
                    Span<byte> window = _window;
                    Span<byte> outcome = _outcome;
                    while (true)
                    {
                        var parsed = VatpFrameParser.ParseOneFrame(
                            new ReadOnlySequence<byte>(buffer, offset, filled - offset),
                            VatpProtocol.DefaultMaxFramePayload);
                        if (parsed.Status == VatpFrameParseStatus.Incomplete)
                        {
                            break;
                        }

                        if (parsed.Status != VatpFrameParseStatus.Success || parsed.Frame is null)
                        {
                            Fault(new InvalidOperationException($"VATP parse failed: {parsed.Error}"));
                            return;
                        }

                        var frame = parsed.Frame.Value;
                        switch (frame.Header.Type)
                        {
                            case VatpFrameType.Hello:
                                _resultGate.Release();
                                break;
                            case VatpFrameType.Window:
                                frame.Payload.CopyTo(window);
                                if (!VatpControlPayload.TryDecodeWindow(window, out var add))
                                {
                                    Fault(new InvalidOperationException("Invalid WINDOW."));
                                    return;
                                }

                                Interlocked.Add(ref _credit, add);
                                _creditGate.Release();
                                break;
                            case VatpFrameType.Result:
                                frame.Payload.CopyTo(outcome);
                                if (outcome[0] == VatpStoreOutcome.Accepted)
                                {
                                    Interlocked.Increment(ref _accepted);
                                }
                                else
                                {
                                    Interlocked.Increment(ref _rejected);
                                }

                                _resultGate.Release();
                                break;
                            case VatpFrameType.Fail:
                                Interlocked.Increment(ref _rejected);
                                Fault(new InvalidOperationException("VATP FAIL during transfer."));
                                _resultGate.Release();
                                return;
                        }

                        offset += checked((int)parsed.ConsumedBytes);
                    }

                    if (offset > 0)
                    {
                        Buffer.BlockCopy(buffer, offset, buffer, 0, filled - offset);
                        filled -= offset;
                    }
                }
            }
            catch (OperationCanceledException) when (_dispose.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Fault(ex);
                _resultGate.Release();
            }
        }

        private void Fault(Exception exception) =>
            Interlocked.CompareExchange(ref _fault, exception, null);
    }
}
