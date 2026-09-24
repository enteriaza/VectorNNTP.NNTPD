using System.Globalization;
using System.Net;
using System.Net.Sockets;
using VectorNNTP.StandaloneTxPoc;

var options = PocOptions.Parse(args);
if (options.ListenOnly)
{
    await using var listener = DrainListener.Start(IPAddress.Parse(options.Host), options.Port);
    Console.WriteLine($"STANDALONE TX DRAIN listening {listener.LocalEndPoint}");
    Console.WriteLine("Not NNTPD. Raw TCP drain only. Ctrl+C to stop.");
    var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        exit.TrySetResult();
    };
    await exit.Task.ConfigureAwait(false);
    return 0;
}

DrainListener? owned = null;
if (!options.ClientOnly)
{
    try
    {
        owned = DrainListener.Start(IPAddress.Parse(options.Host), options.Port);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

await using (owned)
{
    var chunk = new byte[Constants.ChunkBytes];
    for (var i = 0; i < chunk.Length; i++)
    {
        chunk[i] = (byte)(i * 31);
    }

    Console.WriteLine("STANDALONE TX POC — DIAGNOSTIC");
    Console.WriteLine("Does not speak NNTP. Does not use SPEEDTEST. Does not modify NNTPD.");
    Console.WriteLine($"Host={options.Host} Port={options.Port} Bytes={options.TotalBytes} Chunk={Constants.ChunkBytes} Runs={options.Runs}");
    Console.WriteLine($"Receiver: {(options.ClientOnly ? "external drain on " + options.Host + ":" + options.Port : "in-process raw TCP drain (not NNTPD)")}");
    Console.WriteLine();

    var modes = PocOptions.ModesFor(options.Mode);
    foreach (var mode in modes)
    {
        Console.WriteLine($"======== MODE {mode} ========");
        var results = new RunResult[options.Runs];
        SocketSnapshot? snapshot = null;
        for (var run = 0; run < options.Runs; run++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(options.Host, options.Port).ConfigureAwait(false);
            snapshot ??= SocketSnapshot.Capture(client.Client);
            await using var stream = client.GetStream();
            results[run] = mode switch
            {
                "raw" => await TxModes.RunRawAsync(stream, chunk, options.TotalBytes, CancellationToken.None)
                    .ConfigureAwait(false),
                "pipe" => await TxModes.RunPipeAsync(stream, chunk, options.TotalBytes, CancellationToken.None)
                    .ConfigureAwait(false),
                "production-loop" => await TxModes.RunProductionLoopAsync(stream, chunk, options.TotalBytes, CancellationToken.None)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException(mode),
            };
            var r = results[run];
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  run {run + 1}/{options.Runs}  {r.ElapsedMs:F3} ms  {r.GbitPerSecond:F6} Gbit/s  {r.MbitPerSecond:F3} Mbit/s  writes={r.WriteAsyncCount} avg={r.AverageWriteBytes:F1} sync={r.SyncWrites} async={r.AsyncWrites}"));
        }

        Console.WriteLine("Socket (first connection of this mode; defaults not changed):");
        Console.Write(snapshot!.Value.Format());
        var gbits = results.Select(static x => x.GbitPerSecond).ToArray();
        var (min, max) = RunStats.MinMax(gbits);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  median={RunStats.Median(gbits):F6}  min={min:F6}  max={max:F6}  mean={RunStats.Mean(gbits):F6}"));
        Console.WriteLine();
    }
}

return 0;
