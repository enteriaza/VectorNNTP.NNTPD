namespace VectorNNTP.StandaloneTxPoc;

internal sealed class PocOptions
{
    public string Host { get; init; } = Constants.DefaultHost;
    public int Port { get; init; } = Constants.DefaultPort;
    public string Mode { get; init; } = "all";
    public int Runs { get; init; } = Constants.DefaultRuns;
    public long TotalBytes { get; init; } = Constants.DefaultTotalBytes;
    public bool ListenOnly { get; init; }
    public bool ClientOnly { get; init; }

    public static PocOptions Parse(string[] args)
    {
        var host = Constants.DefaultHost;
        var port = Constants.DefaultPort;
        var mode = "all";
        var runs = Constants.DefaultRuns;
        var totalBytes = Constants.DefaultTotalBytes;
        var listenOnly = false;
        var clientOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host":
                    host = Require(args, ref i);
                    break;
                case "--port":
                    port = int.Parse(Require(args, ref i));
                    break;
                case "--mode":
                    mode = Require(args, ref i);
                    break;
                case "--runs":
                    runs = int.Parse(Require(args, ref i));
                    break;
                case "--bytes":
                    totalBytes = long.Parse(Require(args, ref i));
                    break;
                case "--listen":
                    listenOnly = true;
                    break;
                case "--client-only":
                    clientOnly = true;
                    break;
                default:
                    throw new ArgumentException("Unknown argument: " + args[i]);
            }
        }

        if (mode is not ("all" or "raw" or "pipe" or "production-loop"))
        {
            throw new ArgumentException("Mode must be raw, pipe, production-loop, or all.");
        }

        return new PocOptions
        {
            Host = host,
            Port = port,
            Mode = mode,
            Runs = runs,
            TotalBytes = totalBytes,
            ListenOnly = listenOnly,
            ClientOnly = clientOnly,
        };
    }

    public static string[] ModesFor(string mode) =>
        mode == "all" ? ["raw", "pipe", "production-loop"] : [mode];

    private static string Require(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException("Missing value after " + args[i]);
        }

        i++;
        return args[i];
    }
}
