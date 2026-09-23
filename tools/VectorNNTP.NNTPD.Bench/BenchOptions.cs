namespace VectorNNTP.NNTPD.Bench;

/// <summary>Shared command-line options for all benchmark workloads.</summary>
internal sealed class BenchOptions
{
    public const string DefaultBenchmark = "BENCHIT";
    public const int DefaultArticleSize = 750 * 1024;
    public const int DefaultPipelineDepth = 256;

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
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        var isTakeThis = string.Equals(benchmark, "TAKETHIS", StringComparison.OrdinalIgnoreCase);
        if (isTakeThis && !warmupSpecified)
        {
            warmup = 0;
        }

        if (isTakeThis && !runsSpecified)
        {
            runs = 1;
        }

        if (isTakeThis && !measureSpecified)
        {
            measure = 30;
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
        };
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
