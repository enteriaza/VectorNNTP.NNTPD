using System.Net;
using VectorNNTP.TftpSmoke.Protocol;
using VectorNNTP.TftpSmoke.Transfer;

namespace VectorNNTP.TftpSmoke.Tests;

internal sealed class TftpPair : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _serverTask;

    private TftpPair(MemoryNetwork network, TftpServer server, TftpClient client, string root, TftpTiming timing, CancellationTokenSource stop, Task serverTask)
    {
        Network = network;
        Server = server;
        Client = client;
        Root = root;
        Timing = timing;
        _stop = stop;
        _serverTask = serverTask;
    }

    public MemoryNetwork Network { get; }

    public TftpServer Server { get; }

    public TftpClient Client { get; }

    public string Root { get; }

    public TftpTiming Timing { get; }

    public IPEndPoint ServerEndPoint => Server.LocalEndPoint ?? throw new InvalidOperationException("Server is not bound.");

    public static async Task<TftpPair> StartAsync(
        Action<MemoryNetwork>? configureNetwork = null,
        long maxArticleBytes = 8 * 1024 * 1024,
        int maxTransfers = 32,
        int maxBlockSize = TftpLimits.MaxBlockSize,
        int maxWindowSize = 64,
        Func<ReceivedArticle, CancellationToken, ValueTask>? onReceived = null)
    {
        var network = new MemoryNetwork();
        configureNetwork?.Invoke(network);
        var root = Path.Combine(Path.GetTempPath(), "tftp-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var timing = new TftpTiming
        {
            AcknowledgementTimeout = TimeSpan.FromMilliseconds(40),
            MaxTimeouts = 4,
            FinalAckLinger = TimeSpan.FromMilliseconds(15),
        };
        var server = new TftpServer(new TftpServerOptions
        {
            RootDirectory = root,
            BindAddress = IPAddress.Loopback,
            Port = 0,
            MaxArticleBytes = maxArticleBytes,
            MaxConcurrentTransfers = maxTransfers,
            MaxBlockSize = maxBlockSize,
            MaxWindowSize = maxWindowSize,
            Timing = timing,
            OnReceived = onReceived ?? ((article, _) =>
            {
                article.Publish();
                return ValueTask.CompletedTask;
            }),
        }, network);
        var stop = new CancellationTokenSource();
        var task = server.RunAsync(stop.Token);
        await server.Started;
        var client = new TftpClient(network, IPAddress.Loopback, timing);
        return new TftpPair(network, server, client, root, timing, stop, task);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _serverTask;
        }
        catch (OperationCanceledException)
        {
        }

        await Server.DisposeAsync();
        _stop.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
