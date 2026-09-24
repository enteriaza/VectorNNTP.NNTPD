namespace VectorNNTP.NNTPD.Bench;

/// <summary>Shared command-line options for all benchmark workloads.</summary>
internal sealed class BenchOptions
{
    public const string DefaultBenchmark = "BENCHIT";
    public const int DefaultArticleSize = 750 * 1024;
    public const int DefaultPipelineDepth = 256;
    public const int DefaultTimingSamples = 4000;

    public string Benchmark { get; init; } = DefaultBenchmark;
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
    public int ArticleSize { get; init; } = DefaultArticleSize;
    public int PipelineDepth { get; init; } = DefaultPipelineDepth;
    public bool Timing { get; init; }
    public int TimingSamples { get; init; } = DefaultTimingSamples;

    /// <summary>
    /// How many article <c>SendAsync</c> operations may be in flight on one connection.
    /// Default 1 is the canonical serial sender. Values &gt; 1 are a temporary control
    /// experiment and are not the established benchmark.
    /// </summary>
    public int SenderDepth { get; init; } = 1;

    /// <summary>
    /// Configured Transit identifier for <c>--benchmark SPEEDTEST</c>.
    /// Not PeerName, a hostname, IP, or port.
    /// </summary>
    public string SpeedTestPeer { get; init; } = string.Empty;

    /// <summary>
    /// SPEEDTEST payload receive mode. Default <see cref="SpeedTestReceiveMode.Byte"/>
    /// is the framed socket drain. <see cref="SpeedTestReceiveMode.Line"/> is the
    /// diagnostic <c>StreamReader</c> path. <see cref="SpeedTestReceiveMode.Raw"/> is
    /// the count-based drain.
    /// </summary>
    public SpeedTestReceiveMode SpeedTestReceive { get; init; } = SpeedTestReceiveMode.Byte;

    /// <summary>
    /// Expected SPEEDTEST payload bytes for <see cref="SpeedTestReceiveMode.Raw"/>.
    /// Matches the server default (64 MiB). Unused by the line receiver.
    /// </summary>
    public long SpeedTestBytes { get; init; } = VectorNNTP.NNTPD.Configuration.SpeedTestOptions.DefaultMaxBytes;

    public static BenchOptions Parse(string[] args)
    {
        var benchmark = DefaultBenchmark;
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
        var articleSize = DefaultArticleSize;
        var pipelineDepth = DefaultPipelineDepth;
        var timing = false;
        var timingSamples = DefaultTimingSamples;
        var senderDepth = 1;
        var speedTestPeer = string.Empty;
        var speedTestReceive = SpeedTestReceiveMode.Byte;
        var speedTestBytes = VectorNNTP.NNTPD.Configuration.SpeedTestOptions.DefaultMaxBytes;
        var samplesSpecified = false;
        var warmupSpecified = false;
        var runsSpecified = false;
        var measureSpecified = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--benchmark":
                    benchmark = RequireValue(args, ref i);
                    break;
                case "--host":
                    host = RequireValue(args, ref i);
                    break;
                case "--plain-port":
                case "--port":
                    plain = int.Parse(RequireValue(args, ref i));
                    break;
                case "--tls-port":
                    tls = int.Parse(RequireValue(args, ref i));
                    break;
                case "--tls-host":
                    tlsHost = RequireValue(args, ref i);
                    break;
                case "--runs":
                    runs = int.Parse(RequireValue(args, ref i));
                    runsSpecified = true;
                    break;
                case "--warmup-seconds":
                    warmup = int.Parse(RequireValue(args, ref i));
                    warmupSpecified = true;
                    break;
                case "--measure-seconds":
                case "--duration":
                    measure = int.Parse(RequireValue(args, ref i));
                    measureSpecified = true;
                    break;
                case "--server-pid":
                    pid = int.Parse(RequireValue(args, ref i));
                    break;
                case "--mode":
                    mode = RequireValue(args, ref i);
                    break;
                case "--connections":
                    conn = int.Parse(RequireValue(args, ref i));
                    break;
                case "--iperf-only":
                    iperfOnly = true;
                    break;
                case "--iperf-path":
                    iperfPath = RequireValue(args, ref i);
                    break;
                case "--iperf-port":
                    iperfPort = int.Parse(RequireValue(args, ref i));
                    break;
                case "--article-size":
                    articleSize = int.Parse(RequireValue(args, ref i));
                    break;
                case "--pipeline-depth":
                    pipelineDepth = int.Parse(RequireValue(args, ref i));
                    break;
                case "--timing":
                    timing = true;
                    break;
                case "--samples":
                    timingSamples = int.Parse(RequireValue(args, ref i));
                    samplesSpecified = true;
                    break;
                case "--sender-depth":
                    senderDepth = int.Parse(RequireValue(args, ref i));
                    break;
                case "--speedtest-peer":
                    speedTestPeer = RequireValue(args, ref i);
                    break;
                case "--speedtest-receive":
                    speedTestReceive = ParseSpeedTestReceive(RequireValue(args, ref i));
                    break;
                case "--speedtest-bytes":
                    speedTestBytes = long.Parse(RequireValue(args, ref i));
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        var isTakeThis = string.Equals(benchmark, "TAKETHIS", StringComparison.OrdinalIgnoreCase);
        var isIhave = string.Equals(benchmark, "IHAVE", StringComparison.OrdinalIgnoreCase);
        var isSpeedTest = string.Equals(benchmark, "SPEEDTEST", StringComparison.OrdinalIgnoreCase);
        if ((isTakeThis || isIhave) && !warmupSpecified)
        {
            warmup = 0;
        }

        if ((isTakeThis || isIhave) && !runsSpecified)
        {
            runs = 1;
        }

        if ((isTakeThis || isIhave) && !measureSpecified)
        {
            measure = 30;
        }

        if (isSpeedTest && !warmupSpecified)
        {
            warmup = 0;
        }

        if (isSpeedTest && !runsSpecified)
        {
            runs = 1;
        }

        if (isSpeedTest && string.IsNullOrWhiteSpace(speedTestPeer))
        {
            throw new ArgumentException("SPEEDTEST requires --speedtest-peer <configured Transit peer name>.");
        }

        if (timing && !isIhave && !isTakeThis)
        {
            throw new ArgumentException("--timing is supported only with --benchmark IHAVE or TAKETHIS.");
        }

        if (samplesSpecified && !timing)
        {
            throw new ArgumentException("--samples requires --timing.");
        }

        if (timing && !warmupSpecified)
        {
            warmup = 5;
        }

        if (string.IsNullOrWhiteSpace(benchmark))
        {
            throw new ArgumentException("Benchmark name must not be empty.");
        }

        if (measure <= 0)
        {
            throw new ArgumentException("Duration / measure-seconds must be greater than zero.");
        }

        if (warmup < 0)
        {
            throw new ArgumentException("Warmup seconds must be zero or greater.");
        }

        if (runs <= 0)
        {
            throw new ArgumentException("Runs must be greater than zero.");
        }

        if (articleSize <= 0)
        {
            throw new ArgumentException("Article size must be greater than zero.");
        }

        if (pipelineDepth <= 0)
        {
            throw new ArgumentException("Pipeline depth must be greater than zero.");
        }

        if (conn is <= 0)
        {
            throw new ArgumentException("Connections must be greater than zero.");
        }

        if (timingSamples <= 0)
        {
            throw new ArgumentException("Timing samples must be greater than zero.");
        }

        if (senderDepth <= 0)
        {
            throw new ArgumentException("Sender depth must be greater than zero.");
        }

        if (speedTestBytes <= 0)
        {
            throw new ArgumentException("SPEEDTEST expected payload bytes must be greater than zero.");
        }

        return new BenchOptions
        {
            Benchmark = benchmark,
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
            ArticleSize = articleSize,
            PipelineDepth = pipelineDepth,
            Timing = timing,
            TimingSamples = timingSamples,
            SenderDepth = senderDepth,
            SpeedTestPeer = speedTestPeer,
            SpeedTestReceive = speedTestReceive,
            SpeedTestBytes = speedTestBytes,
        };
    }

    private static SpeedTestReceiveMode ParseSpeedTestReceive(string value)
    {
        if (value.Equals("byte", StringComparison.OrdinalIgnoreCase))
        {
            return SpeedTestReceiveMode.Byte;
        }

        if (value.Equals("line", StringComparison.OrdinalIgnoreCase))
        {
            return SpeedTestReceiveMode.Line;
        }

        if (value.Equals("raw", StringComparison.OrdinalIgnoreCase))
        {
            return SpeedTestReceiveMode.Raw;
        }

        throw new ArgumentException("SPEEDTEST receive mode must be 'byte', 'line', or 'raw'.");
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {args[index]}.");
        }

        return args[++index];
    }
}
