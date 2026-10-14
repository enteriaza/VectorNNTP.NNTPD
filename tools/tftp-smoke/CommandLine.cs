using System.Globalization;
using System.Net;
using System.Net.Sockets;
using VectorNNTP.TftpSmoke.Benchmark;
using VectorNNTP.TftpSmoke.Payload;
using VectorNNTP.TftpSmoke.Protocol;

namespace VectorNNTP.TftpSmoke;

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

    public required string Root { get; init; }

    public required long MaxArticleBytes { get; init; }

    public required int MaxBlockSize { get; init; }

    public required int MaxWindowSize { get; init; }

    public required int MaxTransfers { get; init; }

    public required int TimeoutMilliseconds { get; init; }

    public required int MaxTimeouts { get; init; }

    public required bool Verbose { get; init; }
}

/// <summary>Arguments for one upload or download.</summary>
internal sealed class TransferOptions
{
    public required bool Download { get; init; }

    public required IPAddress Address { get; init; }

    public required int Port { get; init; }

    public required string File { get; init; }

    public required string RemoteName { get; init; }

    public required string Output { get; init; }

    public required int BlockSize { get; init; }

    public required int WindowSize { get; init; }

    public required int TimeoutMilliseconds { get; init; }

    public required int MaxTimeouts { get; init; }

    public required long MaxArticleBytes { get; init; }
}

/// <summary>Parses the four commands. Unknown options fail instead of being ignored.</summary>
internal static class CommandLine
{
    internal const string HelpText =
        """
        VectorNNTP.TftpSmoke — isolated TFTP client, server, and article benchmark.

        This is a standards prototype for RRQ, WRQ, RFC 2347 options, RFC 2348 blksize,
        and RFC 7440 windowsize. It is for a trusted lab network. It is not an
        Internet-facing file server, and a completed transfer is not durable article storage.

        Commands
          help
          server --root <dir> [--bind 127.0.0.1] [--port 6969]
          upload --host <ip> --port <n> --file <path> [--remote-name <name>]
          download --host <ip> --port <n> --file <remote-name> [--output <path>]
          benchmark [--self-host] [--host 127.0.0.1] [--port 0] [--size 768000]
                    [--concurrency 1] [--transfers 16] [--block-size 1428] [--window-size 16]

        Benchmark
          Self-host is the default. The process starts a loopback server, uploads, verifies
          SHA-256 and length, publishes each file atomically, then deletes it unless
          --keep-files is set. --no-self-host uploads to an existing server and cannot
          verify the receiver's bytes.
          --duration-seconds measures a timed interval after warmup. Otherwise --transfers
          is the measured count. Warmup failures abort the run before the timed interval.
          --store file is the end-to-end result. --store none hashes in memory and creates
          no article file, so it is a transport measurement only. --diagnose adds summed
          socket, file, hash, and acknowledgement-wait times. Those sums overlap.
          Payload MiB/s and Gbit/s count article bytes over measured wall time. They are
          not NIC line rate. Wire counters are TFTP datagram bytes and exclude IP and UDP headers.

        Comparable VATP workload
          The same 768000-byte size is the storage-smoke default. That client talks to a
          live StorageServer and counts VATP queue admission, not a verified file write.
          Do not point this tool at a production VATP port.

          dotnet run -c Release --project tools/tftp-smoke -- benchmark --size 768000 --concurrency 16 --transfers 1000 --block-size 1428 --window-size 16
          dotnet run -c Release --project tools/storage-smoke -- smoke --body-bytes 768000 --sessions 4 --results <dir-outside-spool>
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
                "upload" => ParseTransfer(args, error, download: false),
                "download" => ParseTransfer(args, error, download: true),
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

        var root = Required(map, "root");
        return new ParseResult
        {
            Command = "server",
            Server = new ServerOptions
            {
                BindAddress = ParseAddress(Optional(map, "bind", "127.0.0.1")),
                Port = ParseInt(Optional(map, "port", "6969"), 1, 65535, "port"),
                Root = root,
                MaxArticleBytes = ParseLong(Optional(map, "max-article-bytes", "8388608"), 0, 64L * 1024 * 1024, "max-article-bytes"),
                MaxBlockSize = ParseInt(Optional(map, "max-block-size", TftpLimits.MaxBlockSize.ToString(CultureInfo.InvariantCulture)), TftpLimits.MinBlockSize, TftpLimits.MaxBlockSize, "max-block-size"),
                MaxWindowSize = ParseInt(Optional(map, "max-window-size", "64"), TftpLimits.MinWindowSize, TftpLimits.MaxWindowSize, "max-window-size"),
                MaxTransfers = ParseInt(Optional(map, "max-transfers", "128"), 1, 4096, "max-transfers"),
                TimeoutMilliseconds = ParseInt(Optional(map, "timeout-ms", "250"), 10, 60000, "timeout-ms"),
                MaxTimeouts = ParseInt(Optional(map, "retries", "6"), 0, 30, "retries"),
                Verbose = Flag(map, "verbose"),
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

        var file = Required(map, "file");
        var remote = Optional(map, "remote-name", download ? file : Path.GetFileName(file));
        if (!TftpCodec.IsSafeFileName(remote))
        {
            throw new InvalidOperationException("Remote name must be one ASCII segment of letters, digits, '.', '_' or '-'.");
        }

        return new ParseResult
        {
            Command = download ? "download" : "upload",
            Transfer = new TransferOptions
            {
                Download = download,
                Address = ParseAddress(Required(map, "host")),
                Port = ParseInt(Required(map, "port"), 1, 65535, "port"),
                File = file,
                RemoteName = remote,
                Output = Optional(map, "output", file),
                BlockSize = ParseInt(Optional(map, "block-size", TftpLimits.EthernetSafeBlockSize.ToString(CultureInfo.InvariantCulture)), TftpLimits.MinBlockSize, TftpLimits.MaxBlockSize, "block-size"),
                WindowSize = ParseInt(Optional(map, "window-size", "16"), TftpLimits.MinWindowSize, TftpLimits.MaxWindowSize, "window-size"),
                TimeoutMilliseconds = ParseInt(Optional(map, "timeout-ms", "250"), 10, 60000, "timeout-ms"),
                MaxTimeouts = ParseInt(Optional(map, "retries", "6"), 0, 30, "retries"),
                MaxArticleBytes = ParseLong(Optional(map, "max-article-bytes", "8388608"), 0, 64L * 1024 * 1024, "max-article-bytes"),
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

        var kind = Optional(map, "payload", "random");
        if (kind is not ("random" or "article"))
        {
            throw new InvalidOperationException("--payload must be random or article.");
        }

        var store = Optional(map, "store", "file");
        if (store is not ("file" or "none"))
        {
            throw new InvalidOperationException("--store must be file or none.");
        }

        var duration = ParseInt(Optional(map, "duration-seconds", "0"), 0, 3600, "duration-seconds");
        var transfers = ParseInt(Optional(map, "transfers", "16"), 1, 1_000_000, "transfers");
        return new ParseResult
        {
            Command = "benchmark",
            Benchmark = new BenchmarkPlan
            {
                Host = ParseAddress(Optional(map, "host", "127.0.0.1")).ToString(),
                Port = ParseInt(portText, 0, 65535, "port"),
                SelfHost = selfHost,
                PayloadBytes = ParseInt(Optional(map, "size", ArticlePayload.DefaultBytes.ToString(CultureInfo.InvariantCulture)), 0, 8_388_608, "size"),
                PayloadKind = kind,
                Concurrency = ParseInt(Optional(map, "concurrency", "1"), 1, 256, "concurrency"),
                Transfers = transfers,
                WarmupTransfers = ParseInt(Optional(map, "warmup-transfers", "4"), 0, 100_000, "warmup-transfers"),
                DurationSeconds = duration,
                BlockSize = ParseInt(Optional(map, "block-size", TftpLimits.EthernetSafeBlockSize.ToString(CultureInfo.InvariantCulture)), TftpLimits.MinBlockSize, TftpLimits.MaxBlockSize, "block-size"),
                WindowSize = ParseInt(Optional(map, "window-size", "16"), TftpLimits.MinWindowSize, 1024, "window-size"),
                TimeoutMilliseconds = ParseInt(Optional(map, "timeout-ms", "250"), 10, 60000, "timeout-ms"),
                MaxTimeouts = ParseInt(Optional(map, "retries", "6"), 0, 30, "retries"),
                ResultsDirectory = Optional(map, "results", ""),
                KeepFiles = Flag(map, "keep-files"),
                Seed = ParseULong(Optional(map, "seed", "1")),
                MaxArticleBytes = ParseLong(Optional(map, "max-article-bytes", "8388608"), 0, 64L * 1024 * 1024, "max-article-bytes"),
                ServerMaxTransfers = ParseInt(Optional(map, "server-max-transfers", "256"), 1, 4096, "server-max-transfers"),
                StoreFiles = store == "file",
                Diagnose = Flag(map, "diagnose"),
            },
        };
    }

    private static Dictionary<string, string>? ReadOptions(string[] args, TextWriter error)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length < 3)
            {
                error.WriteLine($"Unexpected argument '{token}'.");
                return null;
            }

            var name = token[2..];
            if (name is "verbose" or "keep-files" or "no-self-host" or "diagnose")
            {
                map[name] = "true";
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine($"Missing value for --{name}.");
                return null;
            }

            map[name] = args[++i];
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

    private static long ParseLong(string text, long min, long max, string name)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new InvalidOperationException($"--{name} must be an integer from {min} to {max}.");
        }

        return value;
    }

    private static ulong ParseULong(string text)
    {
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value == 0)
        {
            throw new InvalidOperationException("--seed must be an integer from 1 to 18446744073709551615.");
        }

        return value;
    }

    private static ParseResult Reject(TextWriter error, string message)
    {
        error.WriteLine(message);
        return new ParseResult { ExitCode = 2 };
    }
}
