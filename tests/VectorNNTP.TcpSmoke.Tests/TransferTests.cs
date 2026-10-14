using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VectorNNTP.TcpSmoke.Protocol;
using VectorNNTP.TcpSmoke.Transfer;
using Xunit;

namespace VectorNNTP.TcpSmoke.Tests;

public sealed class TransferTests
{
    [Fact]
    public async Task PutAndGet_RoundTripOnLoopback()
    {
        var payload = Bytes(768, 3);
        await using var host = await FileHostAsync();
        var client = host.Client();
        await using var connection = await client.ConnectAsync(CancellationToken.None);
        var hash = SHA256.HashData(payload);
        var put = await connection.PutAsync(42, 1, payload, hash, CancellationToken.None);
        Assert.Equal(ArticleTransferKind.Succeeded, put.Kind);
        Assert.Equal(payload.Length, put.PayloadBytes);
        Assert.Equal(hash, put.Sha256);

        var get = await connection.GetAsync(42, 2, CancellationToken.None);
        Assert.Equal(ArticleTransferKind.Succeeded, get.Kind);
        Assert.Equal(payload, get.Payload);
        Assert.Equal(hash, SHA256.HashData(get.Payload));
        Assert.Equal(payload.Length, new FileInfo(Published(host, 42)).Length);
    }

    [Fact]
    public async Task ZeroLengthPut_PublishesAnEmptyFileAndDownloadsIt()
    {
        await using var host = await FileHostAsync();
        var client = host.Client();
        await using var connection = await client.ConnectAsync(CancellationToken.None);
        var empty = SHA256.HashData([]);
        var put = await connection.PutAsync(7, 1, ReadOnlyMemory<byte>.Empty, empty, CancellationToken.None);
        Assert.Equal(ArticleTransferKind.Succeeded, put.Kind);
        Assert.Equal(0, new FileInfo(Published(host, 7)).Length);
        var get = await connection.GetAsync(7, 2, CancellationToken.None);
        Assert.Equal(ArticleTransferKind.Succeeded, get.Kind);
        Assert.Empty(get.Payload);
        Assert.Equal(empty, get.Sha256);
    }

    [Fact]
    public async Task TransportMode_HashesWithoutCreatingAFile()
    {
        var payload = Bytes(128, 9);
        await using var host = await StartAsync(storeFiles: false);
        var client = host.Client();
        await using var connection = await client.ConnectAsync(CancellationToken.None);
        var put = await connection.PutAsync(3, 1, payload, SHA256.HashData(payload), CancellationToken.None);
        Assert.Equal(ArticleTransferKind.Succeeded, put.Kind);
        var get = await connection.GetAsync(3, 2, CancellationToken.None);
        Assert.Equal(ArticleTransferKind.Failed, get.Kind);
        Assert.Equal(ArticleStatus.NotFound, get.Status);
        Assert.False(Directory.Exists(host.Root));
    }

    [Fact]
    public async Task PersistentConnection_CarriesTwoSequentialPuts()
    {
        await using var host = await FileHostAsync();
        var client = host.Client();
        await using var connection = await client.ConnectAsync(CancellationToken.None);
        foreach (var id in new ulong[] { 10, 11 })
        {
            var payload = Bytes(64, (int)id);
            var result = await connection.PutAsync(id, id, payload, SHA256.HashData(payload), CancellationToken.None);
            Assert.Equal(ArticleTransferKind.Succeeded, result.Kind);
        }

        Assert.True(connection.IsOpen);
        Assert.Equal(2, host.Server.CompletedOperations);
    }

    [Fact]
    public async Task ConcurrentPuts_PublishDistinctArticles()
    {
        for (var pass = 0; pass < 8; pass++)
        {
            await using var host = await FileHostAsync(maxClients: 8);
            var tasks = Enumerable.Range(0, 8).Select(async index =>
            {
                var id = (ulong)(100 + (pass * 10) + index);
                var payload = Bytes(256, index + pass);
                var client = host.Client();
                await using var connection = await client.ConnectAsync(CancellationToken.None);
                return await connection.PutAsync(id, id, payload, SHA256.HashData(payload), CancellationToken.None);
            });
            var results = await Task.WhenAll(tasks);
            Assert.All(results, result => Assert.Equal(ArticleTransferKind.Succeeded, result.Kind));
            Assert.Equal(8, Directory.GetFiles(host.Root, "*.bin").Length);
        }
    }

    [Fact]
    public async Task ExistingArticle_IsNotOverwritten()
    {
        var first = Bytes(32, 1);
        var second = Bytes(32, 2);
        await using var host = await FileHostAsync();
        var client = host.Client();
        await using var connection = await client.ConnectAsync(CancellationToken.None);
        Assert.Equal(ArticleTransferKind.Succeeded, (await connection.PutAsync(5, 1, first, SHA256.HashData(first), CancellationToken.None)).Kind);
        var again = await connection.PutAsync(5, 2, second, SHA256.HashData(second), CancellationToken.None);
        Assert.Equal(ArticleTransferKind.Failed, again.Kind);
        Assert.Equal(ArticleStatus.Exists, again.Status);
        Assert.Equal(first, await File.ReadAllBytesAsync(Published(host, 5)));
        Assert.True(connection.IsOpen);
    }

    [Fact]
    public async Task HashMismatch_DoesNotPublish()
    {
        var payload = Bytes(40, 4);
        await using var host = await FileHostAsync();
        var client = host.Client();
        await using var connection = await client.ConnectAsync(CancellationToken.None);
        var result = await connection.PutAsync(8, 1, payload, SHA256.HashData([1, 2, 3]), CancellationToken.None);
        Assert.Equal(ArticleStatus.HashMismatch, result.Status);
        Assert.False(File.Exists(Published(host, 8)));
        Assert.Empty(Directory.GetFiles(Path.Combine(host.Root, ".incoming")));
    }

    [Fact]
    public async Task UnknownArticle_ReturnsNotFound()
    {
        await using var host = await FileHostAsync();
        var client = host.Client();
        await using var connection = await client.ConnectAsync(CancellationToken.None);
        var result = await connection.GetAsync(123456, 1, CancellationToken.None);
        Assert.Equal(ArticleStatus.NotFound, result.Status);
        Assert.Equal(0, result.PayloadBytes);
    }

    [Fact]
    public async Task MaximumPayload_IsAcceptedAndOneExtraByteIsRejected()
    {
        await using var host = await FileHostAsync(maxArticleBytes: 64);
        var client = host.Client();
        await using var connection = await client.ConnectAsync(CancellationToken.None);
        var exact = Bytes(64, 6);
        var accepted = await connection.PutAsync(1, 1, exact, SHA256.HashData(exact), CancellationToken.None);
        Assert.Equal(ArticleTransferKind.Succeeded, accepted.Kind);

        var oversized = Bytes(65, 6);
        var rejected = await connection.PutAsync(2, 2, oversized, SHA256.HashData(oversized), CancellationToken.None);
        Assert.NotEqual(ArticleTransferKind.Succeeded, rejected.Kind);
        Assert.False(File.Exists(Published(host, 2)));
        if (rejected.Kind == ArticleTransferKind.Failed)
        {
            Assert.Equal(ArticleStatus.TooLarge, rejected.Status);
        }
    }

    [Fact]
    public async Task DisconnectMidPayload_DoesNotPublish()
    {
        await using var host = await FileHostAsync(timeout: TimeSpan.FromSeconds(2));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host.EndPoint.Address, host.EndPoint.Port);
        var stream = tcp.GetStream();
        var header = new ArticleHeader(ArticleOperation.Put, ArticleStatus.Ok, 1000, 77, 1, SHA256.HashData(new byte[1000]));
        await ArticleIo.WriteHeaderAsync(stream, header, CancellationToken.None);
        await stream.WriteAsync(new byte[40]);
        tcp.Dispose();

        var published = Published(host, 77);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && host.Server.RejectedOperations == 0 && !File.Exists(published))
        {
            await Task.Delay(20);
        }

        Assert.False(File.Exists(published));
        Assert.Empty(Directory.GetFiles(Path.Combine(host.Root, ".incoming")));
        Assert.Equal(0, host.Server.CompletedOperations);
    }

    [Fact]
    public async Task BadVersion_IsRejected()
    {
        await using var host = await FileHostAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host.EndPoint.Address, host.EndPoint.Port);
        var stream = tcp.GetStream();
        var bytes = new byte[ArticleProtocol.HeaderLength];
        new ArticleHeader(ArticleOperation.Put, ArticleStatus.Ok, 0, 1, 1, new byte[32]).Write(bytes);
        bytes[0] = 9;
        await stream.WriteAsync(bytes);
        var response = new byte[ArticleProtocol.HeaderLength];
        Assert.True(await ArticleIo.ReadExactOrCleanEndAsync(stream, response, CancellationToken.None));
        Assert.True(ArticleHeader.TryRead(response, out var result, out _));
        Assert.Equal(ArticleOperation.Result, result.Operation);
        Assert.Equal(ArticleStatus.BadVersion, result.Status);
        Assert.Equal(0, host.Server.CompletedOperations);
    }

    [Fact]
    public async Task PartialHeaderThenClose_DoesNotComplete()
    {
        await using var host = await FileHostAsync(timeout: TimeSpan.FromMilliseconds(300));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host.EndPoint.Address, host.EndPoint.Port);
        await tcp.GetStream().WriteAsync(new byte[10]);
        tcp.Dispose();
        await Task.Delay(100);
        Assert.Equal(0, host.Server.CompletedOperations);
    }

    [Fact]
    public async Task OneClientLimit_RejectsASecondConnectionAndKeepsTheFirst()
    {
        for (var pass = 0; pass < 5; pass++)
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await LoopbackHost.StartAsync(new ArticleServerOptions
            {
                BindAddress = IPAddress.Loopback,
                Port = 0,
                StoreFiles = true,
                RootDirectory = NewRoot(),
                MaxConcurrentClients = 1,
                BeforePublish = async (_, token) =>
                {
                    hold.TrySetResult();
                    await release.Task.WaitAsync(token);
                },
            });
            var client = host.Client();
            await using var occupant = await client.ConnectAsync(CancellationToken.None);
            var payload = Bytes(8, pass + 1);
            var occupantTask = occupant.PutAsync(50, 1, payload, SHA256.HashData(payload), CancellationToken.None);
            await hold.Task.WaitAsync(TimeSpan.FromSeconds(2));

            ArticleTransferKind secondKind;
            try
            {
                await using var second = await client.ConnectAsync(CancellationToken.None);
                var extra = Bytes(8, 2);
                var secondResult = await second.PutAsync(51, 1, extra, SHA256.HashData(extra), CancellationToken.None);
                secondKind = secondResult.Kind;
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                secondKind = ArticleTransferKind.Incomplete;
            }

            Assert.NotEqual(ArticleTransferKind.Succeeded, secondKind);
            release.TrySetResult();
            var occupantResult = await occupantTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ArticleTransferKind.Succeeded, occupantResult.Kind);
            Assert.False(File.Exists(Published(host, 51)));
        }
    }

    [Fact]
    public async Task Cancellation_StopsTheListener()
    {
        var root = NewRoot();
        using var stop = new CancellationTokenSource();
        var server = new ArticleServer(new ArticleServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            StoreFiles = true,
            RootDirectory = root,
            OperationTimeout = TimeSpan.FromSeconds(30),
        });
        var run = server.RunAsync(stop.Token);
        await server.Started.WaitAsync(TimeSpan.FromSeconds(2));
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        await server.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task CancellationDuringPublish_LeavesNoArticle()
    {
        var root = NewRoot();
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ArticleServer(new ArticleServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            StoreFiles = true,
            RootDirectory = root,
            OperationTimeout = TimeSpan.FromSeconds(5),
            BeforePublish = async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            },
        });
        var run = server.RunAsync(stop.Token);
        await server.Started;
        var endpoint = server.LocalEndPoint!;
        var client = new ArticleClient(endpoint.Address, endpoint.Port, TimeSpan.FromSeconds(5));
        await using var connection = await client.ConnectAsync(CancellationToken.None);
        var payload = Bytes(32, 1);
        var put = connection.PutAsync(9, 1, payload, SHA256.HashData(payload), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await put.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEqual(ArticleTransferKind.Succeeded, result.Kind);
        Assert.False(File.Exists(Path.Combine(root, "0000000000000009.bin")));
        await server.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void NonLoopbackBind_RequiresAllowRemote()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new ArticleServer(new ArticleServerOptions
        {
            BindAddress = IPAddress.Parse("192.0.2.1"),
            AllowRemote = false,
        }));
        Assert.Contains("allow-remote", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reread_PublishesAfterTheSecondHash()
    {
        var payload = Bytes(100, 12);
        var root = NewRoot();
        await using var host = await LoopbackHost.StartAsync(new ArticleServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            StoreFiles = true,
            RootDirectory = root,
            RereadBeforeResult = true,
            MaxConcurrentClients = 2,
        });
        var client = host.Client();
        await using var connection = await client.ConnectAsync(CancellationToken.None);
        var result = await connection.PutAsync(15, 1, payload, SHA256.HashData(payload), CancellationToken.None);
        Assert.Equal(ArticleTransferKind.Succeeded, result.Kind);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(Published(host, 15))));
    }

    private static async Task<LoopbackHost> FileHostAsync(int maxClients = 4, int maxArticleBytes = 1024 * 1024, TimeSpan? timeout = null)
    {
        return await StartAsync(storeFiles: true, maxClients, maxArticleBytes, timeout);
    }

    private static Task<LoopbackHost> StartAsync(bool storeFiles, int maxClients = 4, int maxArticleBytes = 1024 * 1024, TimeSpan? timeout = null)
    {
        return LoopbackHost.StartAsync(new ArticleServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            StoreFiles = storeFiles,
            RootDirectory = storeFiles ? NewRoot() : null,
            MaxConcurrentClients = maxClients,
            MaxArticleBytes = maxArticleBytes,
            OperationTimeout = timeout ?? TimeSpan.FromSeconds(5),
        });
    }

    private static string Published(LoopbackHost host, ulong articleId)
    {
        return Path.Combine(host.Root, articleId.ToString("x16") + ".bin");
    }

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "vectornntp-tcp-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] Bytes(int size, int seed)
    {
        var data = new byte[size];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)(index + seed + 1);
        }

        return data;
    }
}
