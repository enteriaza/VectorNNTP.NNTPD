using System.Net;
using VectorNNTP.TcpSmoke.Transfer;

namespace VectorNNTP.TcpSmoke.Tests;

internal sealed class LoopbackHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _run;

    private LoopbackHost(ArticleServer server, string root, Task run, CancellationTokenSource stop)
    {
        Server = server;
        Root = root;
        _run = run;
        _stop = stop;
    }

    public ArticleServer Server { get; }

    public string Root { get; }

    public IPEndPoint EndPoint => Server.LocalEndPoint ?? throw new InvalidOperationException("The listener is not bound.");

    public static async Task<LoopbackHost> StartAsync(ArticleServerOptions options)
    {
        var stop = new CancellationTokenSource();
        var server = new ArticleServer(options);
        var run = server.RunAsync(stop.Token);
        var host = new LoopbackHost(server, options.RootDirectory ?? "", run, stop);
        await server.Started.WaitAsync(TimeSpan.FromSeconds(5));
        return host;
    }

    public ArticleClient Client(TimeSpan? timeout = null)
    {
        return new ArticleClient(EndPoint.Address, EndPoint.Port, timeout ?? TimeSpan.FromSeconds(5));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        await Server.DisposeAsync();
        if (Root.Length > 0 && Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
