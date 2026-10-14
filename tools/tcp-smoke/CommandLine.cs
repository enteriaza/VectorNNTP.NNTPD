using System.Globalization;
using System.Net;
using System.Net.Sockets;
using VectorNNTP.TcpSmoke.Benchmark;
using VectorNNTP.TcpSmoke.Payload;
using VectorNNTP.TcpSmoke.Protocol;

namespace VectorNNTP.TcpSmoke;

/// <summary>Parsed invocation. <see cref="ExitCode"/> is 2 when the arguments were rejected.</summary>
internal sealed class ParseResult
{
    public bool ShowHelp { get; init; }

    public int ExitCode { get; init; }

    public string Command { get; init; } = "";

    public ServerOptions? Server { get; init; }

    public TransferOptions? Transfer { get; init; }

    public BenchmarkPlan? Benchmark { get; init; }
}

/// <summary>Arguments for the long-running server command.</summary>
internal sealed class ServerOptions
{
    public required IPAddress BindAddress { get; init; }

    public required int Port { get; init; }

    public required bool AllowRemote { get; init; }

    public required bool StoreFiles { get; init; }

    public required string Root { get; init; }

    public required int MaxClients { get; init; }

    public required int MaxArticleBytes { get; init; }

    public required int TimeoutMilliseconds { get; init; }

    public required bool Reread { get; init; }
}

/// <summary>Arguments for one put or get.</summary>
internal sealed class TransferOptions
{
    public required bool Download { get; init; }

    public required IPAddress Address { get; init; }

    public required int Port { get; init; }

    public required string File { get; init; }

    public required ulong ArticleId { get; init; }

    public required int TimeoutMilliseconds { get; init; }

    public required int MaxArticleBytes { get; init; }
}

/// <summary>Parses the commands. Unknown options fail instead of being ignored.</summary>
internal static class CommandLine
{
    internal const string HelpText =
        """
        VectorNNTP.TcpSmoke — isolated TCP article client, server, and benchmark.

        A minimal length-prefixed stream for a trusted lab network. It is not VATP,
        it does not authenticate, and a completed transfer is not durable article storage.
        The listener binds loopback unless --allow-remote is set.

        Commands
          help
          server [--bind 127.0.0.1] [--port 6970] [--mode transport|file] [--root <dir>]
          put --host <ip> --port <n> --file <path> [--article-id <n>]
          get --host <ip> --port <n> --article-id <n> --output <path>
          benchmark [--mode transport|file] [--connections persistent|fresh]
                    [--size 768000] [--concurrency 1] [--duration-seconds 5]

        Benchmark
          Self-host is the default. Transport mode hashes each payload and discards it.
          File mode writes a temp file, checks the length, renames it, then sends RESULT.
          --reread hashes that temp file a second time before the rename.
          Persistent mode keeps one connection per worker and opens it before the clock.
          Fresh mode connects inside every measured transfer.
          Payload MiB/s and Gbit/s count completed article bytes over measured wall time.
          They are not NIC line rate. Self-host CPU includes both peers.

        Comparable workloads
          TFTP:  tools/tftp-smoke/README.md
          VATP:  dotnet run -c Release --project tools/storage-smoke -- smoke --body-bytes 768000 --sessions 4 --results <dir-outside-spool>
          Do not point this tool at a production StorageServer. VATP accepted is queue admission, not a verified file.
        """;

    internal static ParseResult Parse(string[] args, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            return new ParseResult { ShowHelp = true };
        }

        try
        {
            return args[0] switch
            {
                "server" => ParseServer(args, error),
                "put" => ParseTransfer(args, error, download: false),
                "get" => ParseTransfer(args, error, download: true),
                "benchmark" => ParseBenchmark(args, error),
                _ => Reject(error, $"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or SocketException)
        {
            return Reject(error, ex.Message);
        }
    }

    private static ParseResult ParseServer(string[] args, TextWriter error)
    {
        var map = ReadOptions(args, error);
        if (map is null)
        {
            return new ParseResult { ExitCode = 2 };
        }

        var mode = Optional(map, "mode", "file");
        if (mode is not ("file" or "transport"))
        {
            throw new InvalidOperationException("--mode must be file or transport.");
        }

        var root = Optional(map, "root", "");
        if (mode == "file" && root.Length == 0)
        {
            throw new InvalidOperationException("File mode requires --root.");
        }

        return new ParseResult
        {
            Command = "server",
            Server = new ServerOptions
            {
                BindAddress = ParseAddress(Optional(map, "bind", "127.0.0.1")),
                Port = ParseInt(Optional(map, "port", "6970"), 1, 65535, "port"),
                AllowRemote = Flag(map, "allow-remote"),
                StoreFiles = mode == "file",
                Root = root,
                MaxClients = ParseInt(Optional(map, "max-clients", "128"), 1, 4096, "max-clients"),
                MaxArticleBytes = ParseInt(Optional(map, "max-article-bytes", ArticleProtocol.DefaultMaxArticleBytes.ToString(CultureInfo.InvariantCulture)), 0, 64 * 1024 * 1024, "max-article-bytes"),
                TimeoutMilliseconds = ParseInt(Optional(map, "timeout-ms", "30000"), 100, 600_000, "timeout-ms"),
                Reread = Flag(map, "reread"),
            },
        };
    }

    private static ParseResult ParseTransfer(string[] args, TextWriter error, bool download)
    {
        var map = ReadOptions(args, error);
        if (map is null)
        {
            return new ParseResult { ExitCode = 2 };
        }

        var file = download ? Optional(map, "output", "") : Required(map, "file");
        if (download && file.Length == 0)
        {
            throw new InvalidOperationException("--output is required.");
        }

        return new ParseResult
        {
            Command = download ? "get" : "put",
            Transfer = new TransferOptions
            {
                Download = download,
                Address = ParseAddress(Required(map, "host")),
                Port = ParseInt(Required(map, "port"), 1, 65535, "port"),
                File = file,
                ArticleId = ParseULong(Optional(map, "article-id", "1")),
                TimeoutMilliseconds = ParseInt(Optional(map, "timeout-ms", "30000"), 100, 600_000, "timeout-ms"),
                MaxArticleBytes = ParseInt(Optional(map, "max-article-bytes", ArticleProtocol.DefaultMaxArticleBytes.ToString(CultureInfo.InvariantCulture)), 0, 64 * 1024 * 1024, "max-article-bytes"),
            },
        };
    }

    private static ParseResult ParseBenchmark(string[] args, TextWriter error)
    {
        var map = ReadOptions(args, error);
        if (map is null)
        {
            return new ParseResult { ExitCode = 2 };
        }

        var selfHost = !Flag(map, "no-self-host");
        var portText = Optional(map, "port", selfHost ? "0" : "");
        if (!selfHost && portText.Length == 0)
        {
            throw new InvalidOperationException("--no-self-host requires --port.");
        }

        var mode = Optional(map, "mode", "transport");
        if (mode is not ("file" or "transport"))
        {
            throw new InvalidOperationException("--mode must be file or transport.");
        }

        var connections = Optional(map, "connections", "persistent");
        if (connections is not ("persistent" or "fresh"))
        {
            throw new InvalidOperationException("--connections must be persistent or fresh.");
        }

        var kind = Optional(map, "payload", "random");
        if (kind is not ("random" or "article"))
        {
            throw new InvalidOperationException("--payload must be random or article.");
        }

        return new ParseResult
        {
            Command = "benchmark",
            Benchmark = new BenchmarkPlan
            {
                Host = ParseAddress(Optional(map, "host", "127.0.0.1")).ToString(),
                Port = ParseInt(portText, 0, 65535, "port"),
                SelfHost = selfHost,
                AllowRemote = Flag(map, "allow-remote"),
                Mode = mode,
                Connections = connections,
                PayloadBytes = ParseInt(Optional(map, "size", ArticlePayload.DefaultBytes.ToString(CultureInfo.InvariantCulture)), 0, ArticleProtocol.DefaultMaxArticleBytes, "size"),
                PayloadKind = kind,
                Concurrency = ParseInt(Optional(map, "concurrency", "1"), 1, 256, "concurrency"),
                Transfers = ParseInt(Optional(map, "transfers", "16"), 1, 1_000_000, "transfers"),
                WarmupTransfers = ParseInt(Optional(map, "warmup-transfers", "4"), 0, 100_000, "warmup-transfers"),
                DurationSeconds = ParseInt(Optional(map, "duration-seconds", "0"), 0, 3600, "duration-seconds"),
                TimeoutMilliseconds = ParseInt(Optional(map, "timeout-ms", "30000"), 100, 600_000, "timeout-ms"),
                ResultsDirectory = Optional(map, "results", ""),
                KeepFiles = Flag(map, "keep-files"),
                Reread = Flag(map, "reread"),
                Seed = ParseULong(Optional(map, "seed", "1")),
                MaxArticleBytes = ParseInt(Optional(map, "max-article-bytes", ArticleProtocol.DefaultMaxArticleBytes.ToString(CultureInfo.InvariantCulture)), 0, 64 * 1024 * 1024, "max-article-bytes"),
                ServerMaxClients = ParseInt(Optional(map, "server-max-clients", "128"), 1, 4096, "server-max-clients"),
            },
        };
    }

    private static Dictionary<string, string>? ReadOptions(string[] args, TextWriter error)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length < 3)
            {
                error.WriteLine($"Unexpected argument '{token}'.");
                return null;
            }

            var name = token[2..];
            if (name is "allow-remote" or "keep-files" or "no-self-host" or "reread")
            {
                map[name] = "true";
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine($"Missing value for --{name}.");
                return null;
            }

            map[name] = args[++index];
        }

        return map;
    }

    private static string Required(Dictionary<string, string> map, string name)
    {
        if (!map.TryGetValue(name, out var value) || value.Length == 0)
        {
            throw new InvalidOperationException($"--{name} is required.");
        }

        return value;
    }

    private static string Optional(Dictionary<string, string> map, string name, string fallback)
    {
        return map.TryGetValue(name, out var value) && value.Length > 0 ? value : fallback;
    }

    private static bool Flag(Dictionary<string, string> map, string name) => map.ContainsKey(name);

    private static IPAddress ParseAddress(string text)
    {
        if (!IPAddress.TryParse(text, out var address))
        {
            throw new InvalidOperationException($"'{text}' is not an IP address. This tool does not resolve DNS names.");
        }

        return address;
    }

    private static int ParseInt(string text, int min, int max, string name)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new InvalidOperationException($"--{name} must be an integer from {min} to {max}.");
        }

        return value;
    }

    private static ulong ParseULong(string text)
    {
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value == 0)
        {
            throw new InvalidOperationException("--seed or --article-id must be an integer from 1 to 18446744073709551615.");
        }

        return value;
    }

    private static ParseResult Reject(TextWriter error, string message)
    {
        error.WriteLine(message);
        return new ParseResult { ExitCode = 2 };
    }
}
