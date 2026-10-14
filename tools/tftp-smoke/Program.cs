using VectorNNTP.TftpSmoke.Benchmark;
using VectorNNTP.TftpSmoke.Transfer;
using VectorNNTP.TftpSmoke.Transport;

namespace VectorNNTP.TftpSmoke;

/// <summary>Entry point for the isolated TFTP prototype.</summary>
public static class Program
{
    /// <summary>Runs one command. 0 is success, 1 is a failed transfer or benchmark, 2 is a bad argument.</summary>
    /// <param name="args">Command line after the process name.</param>
    /// <returns>The process exit code.</returns>
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
                "upload" => await RunUploadAsync(parsed.Transfer!, cancellation.Token).ConfigureAwait(false),
                "download" => await RunDownloadAsync(parsed.Transfer!, cancellation.Token).ConfigureAwait(false),
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
        var server = new TftpServer(new TftpServerOptions
        {
            RootDirectory = options.Root,
            BindAddress = options.BindAddress,
            Port = options.Port,
            MaxArticleBytes = options.MaxArticleBytes,
            MaxBlockSize = options.MaxBlockSize,
            MaxWindowSize = options.MaxWindowSize,
            MaxConcurrentTransfers = options.MaxTransfers,
            Timing = Timing(options.TimeoutMilliseconds, options.MaxTimeouts),
            Log = new ConsoleTftpLog { Verbose = options.Verbose },
        }, new UdpDatagramTransportFactory());
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

    private static async Task<int> RunUploadAsync(TransferOptions options, CancellationToken cancellationToken)
    {
        var payload = await File.ReadAllBytesAsync(options.File, cancellationToken).ConfigureAwait(false);
        if (payload.LongLength > options.MaxArticleBytes)
        {
            Console.Error.WriteLine("File exceeds --max-article-bytes.");
            return 1;
        }

        var client = new TftpClient(new UdpDatagramTransportFactory(), options.Address, Timing(options.TimeoutMilliseconds, options.MaxTimeouts));
        var result = await client.UploadAsync(
            new System.Net.IPEndPoint(options.Address, options.Port),
            options.RemoteName,
            payload,
            options.BlockSize,
            options.WindowSize,
            cancellationToken).ConfigureAwait(false);
        WriteResult(result);
        return result.Status == TftpTransferStatus.Succeeded ? 0 : 1;
    }

    private static async Task<int> RunDownloadAsync(TransferOptions options, CancellationToken cancellationToken)
    {
        var client = new TftpClient(new UdpDatagramTransportFactory(), options.Address, Timing(options.TimeoutMilliseconds, options.MaxTimeouts));
        var result = await client.DownloadAsync(
            new System.Net.IPEndPoint(options.Address, options.Port),
            options.RemoteName,
            options.Output,
            options.BlockSize,
            options.WindowSize,
            options.MaxArticleBytes,
            cancellationToken).ConfigureAwait(false);
        WriteResult(result);
        if (result.Status == TftpTransferStatus.Succeeded)
        {
            Console.WriteLine($"sha256={Convert.ToHexString(result.Sha256).ToLowerInvariant()} bytes={result.PayloadBytes}");
        }

        return result.Status == TftpTransferStatus.Succeeded ? 0 : 1;
    }

    private static async Task<int> RunBenchmarkAsync(BenchmarkPlan plan, CancellationToken cancellationToken)
    {
        var report = await BenchmarkRunner.RunAsync(plan, Console.Out, cancellationToken).ConfigureAwait(false);
        return report.FailedTransfers == 0 && report.HashMismatches == 0 && report.WarmupFailures == 0 ? 0 : 1;
    }

    private static TftpTiming Timing(int timeoutMilliseconds, int maxTimeouts)
    {
        return new TftpTiming
        {
            AcknowledgementTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds),
            MaxTimeouts = maxTimeouts,
            FinalAckLinger = TimeSpan.FromMilliseconds(timeoutMilliseconds),
        };
    }

    private static void WriteResult(TftpTransferResult result)
    {
        Console.WriteLine($"status={result.Status} bytes={result.PayloadBytes} retries={result.Retries} timeouts={result.Timeouts} block={result.BlockSize} window={result.WindowSize} elapsed_ms={result.Elapsed.TotalMilliseconds:F1}");
        if (result.Status != TftpTransferStatus.Succeeded)
        {
            Console.Error.WriteLine(result.ErrorMessage);
        }
    }
}
