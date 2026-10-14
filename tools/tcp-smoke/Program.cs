using System.Security.Cryptography;
using VectorNNTP.TcpSmoke.Benchmark;
using VectorNNTP.TcpSmoke.Protocol;
using VectorNNTP.TcpSmoke.Transfer;

namespace VectorNNTP.TcpSmoke;

/// <summary>Entry point for the isolated TCP article prototype.</summary>
public static class Program
{
    /// <summary>Runs one command. 0 is success, 1 is a failed transfer or benchmark, 2 is a bad argument.</summary>
    public static async Task<int> Main(string[] args)
    {
        var parsed = CommandLine.Parse(args, Console.Error);
        if (parsed.ShowHelp)
        {
            Console.WriteLine(CommandLine.HelpText);
            return 0;
        }

        if (parsed.Command.Length == 0)
        {
            return parsed.ExitCode == 0 ? 2 : parsed.ExitCode;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return parsed.Command switch
            {
                "server" => await RunServerAsync(parsed.Server!, cancellation.Token).ConfigureAwait(false),
                "put" => await RunPutAsync(parsed.Transfer!, cancellation.Token).ConfigureAwait(false),
                "get" => await RunGetAsync(parsed.Transfer!, cancellation.Token).ConfigureAwait(false),
                "benchmark" => await RunBenchmarkAsync(parsed.Benchmark!, cancellation.Token).ConfigureAwait(false),
                _ => 2,
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunServerAsync(ServerOptions options, CancellationToken cancellationToken)
    {
        var server = new ArticleServer(new ArticleServerOptions
        {
            BindAddress = options.BindAddress,
            Port = options.Port,
            AllowRemote = options.AllowRemote,
            StoreFiles = options.StoreFiles,
            RootDirectory = options.StoreFiles ? options.Root : null,
            MaxConcurrentClients = options.MaxClients,
            MaxArticleBytes = options.MaxArticleBytes,
            OperationTimeout = TimeSpan.FromMilliseconds(options.TimeoutMilliseconds),
            RereadBeforeResult = options.Reread,
        });
        await using var _ = server;
        try
        {
            await server.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    private static async Task<int> RunPutAsync(TransferOptions options, CancellationToken cancellationToken)
    {
        var payload = await File.ReadAllBytesAsync(options.File, cancellationToken).ConfigureAwait(false);
        if (payload.Length > options.MaxArticleBytes)
        {
            Console.Error.WriteLine("File exceeds --max-article-bytes.");
            return 1;
        }

        var hash = SHA256.HashData(payload);
        var client = new ArticleClient(options.Address, options.Port, TimeSpan.FromMilliseconds(options.TimeoutMilliseconds));
        await using var connection = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var result = await connection.PutAsync(options.ArticleId, 1, payload, hash, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"kind={result.Kind} status={result.Status} bytes={result.PayloadBytes} elapsed_ms={result.Elapsed.TotalMilliseconds:F1}");
        return result.Kind == ArticleTransferKind.Succeeded ? 0 : 1;
    }

    private static async Task<int> RunGetAsync(TransferOptions options, CancellationToken cancellationToken)
    {
        var client = new ArticleClient(options.Address, options.Port, TimeSpan.FromMilliseconds(options.TimeoutMilliseconds));
        await using var connection = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var result = await connection.GetAsync(options.ArticleId, 1, cancellationToken).ConfigureAwait(false);
        if (result.Kind == ArticleTransferKind.Succeeded)
        {
            var actual = SHA256.HashData(result.Payload);
            if (!CryptographicOperations.FixedTimeEquals(actual, result.Sha256))
            {
                Console.Error.WriteLine("Downloaded bytes do not match the header SHA-256.");
                return 1;
            }

            await File.WriteAllBytesAsync(options.File, result.Payload, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"kind={result.Kind} bytes={result.PayloadBytes} sha256={Convert.ToHexString(actual).ToLowerInvariant()}");
            return 0;
        }

        Console.WriteLine($"kind={result.Kind} status={result.Status}");
        return 1;
    }

    private static async Task<int> RunBenchmarkAsync(BenchmarkPlan plan, CancellationToken cancellationToken)
    {
        var report = await BenchmarkRunner.RunAsync(plan, Console.Out, cancellationToken).ConfigureAwait(false);
        if (report.WarmupFailures != 0 || report.FailedTransfers != 0 || report.IncompleteTransfers != 0 || report.VerificationFailures != 0)
        {
            return 1;
        }

        return 0;
    }
}
