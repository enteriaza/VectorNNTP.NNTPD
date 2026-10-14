using System.Net;
using System.Security.Cryptography;
using VectorNNTP.TftpSmoke.Protocol;
using VectorNNTP.TftpSmoke.Transfer;
using VectorNNTP.TftpSmoke.Transport;
using Xunit;

namespace VectorNNTP.TftpSmoke.Tests;

public sealed class TransferTests
{
    [Theory]
    [InlineData(1000, 512, 1, 2)]
    [InlineData(1024, 512, 1, 3)]
    [InlineData(0, 512, 1, 1)]
    [InlineData(2000, 512, 4, 4)]
    public async Task Upload_FinalBlockShape_MatchesLengthAndHash(int size, int blockSize, int windowSize, int dataPackets)
    {
        await using var pair = await TftpPair.StartAsync();
        var payload = size == 0 ? [] : Enumerable.Range(0, size).Select(value => (byte)(value * 17)).ToArray();
        var result = await pair.Client.UploadAsync(pair.ServerEndPoint, "article.bin", payload, blockSize, windowSize, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Succeeded, result.Status);
        Assert.Equal(dataPackets, pair.Network.DataPackets);
        var stored = await File.ReadAllBytesAsync(Path.Combine(pair.Root, "article.bin"));
        Assert.Equal(payload, stored);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(stored));
        Assert.Empty(Directory.GetFiles(Path.Combine(pair.Root, ".incoming")));
    }

    [Fact]
    public async Task Upload_ExactMultiple_IsNotFollowedByExtraBytes()
    {
        await using var pair = await TftpPair.StartAsync();
        var payload = new byte[1024];
        payload.AsSpan().Fill(0x11);
        var result = await pair.Client.UploadAsync(pair.ServerEndPoint, "exact.bin", payload, 512, 1, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Succeeded, result.Status);
        Assert.Equal(3, pair.Network.DataPackets);
        Assert.Equal(1024, new FileInfo(Path.Combine(pair.Root, "exact.bin")).Length);
    }

    [Fact]
    public async Task Upload_DuplicateDataAndAck_DoesNotCorruptTheFile()
    {
        await using var pair = await TftpPair.StartAsync(network =>
        {
            network.DuplicateDataRemaining = 1;
            network.DuplicateAckRemaining = 1;
        });
        var payload = Random(5000, 3);
        var result = await pair.Client.UploadAsync(pair.ServerEndPoint, "dup.bin", payload, 512, 4, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Succeeded, result.Status);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(pair.Root, "dup.bin"))));
    }

    [Fact]
    public async Task Upload_RetransmitsADroppedDataBlock()
    {
        await using var pair = await TftpPair.StartAsync(network => network.DataDropsRemaining = 1);
        var payload = Random(3000, 9);
        var result = await pair.Client.UploadAsync(pair.ServerEndPoint, "retry.bin", payload, 512, 4, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Succeeded, result.Status);
        Assert.True(result.Retries > 0);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(pair.Root, "retry.bin")));
    }

    [Fact]
    public async Task Upload_RetransmittedRequest_DoesNotCreateASecondFile()
    {
        await using var pair = await TftpPair.StartAsync(network => network.OackDropsRemaining = 1);
        var payload = Random(800, 1);
        var result = await pair.Client.UploadAsync(pair.ServerEndPoint, "once.bin", payload, 512, 1, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Succeeded, result.Status);
        Assert.True(result.Retries > 0 || result.Timeouts > 0);
        Assert.Equal(1, pair.Server.Counters.AcceptedWrites);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(pair.Root, "once.bin")));
    }

    [Fact]
    public async Task Upload_ReorderedWindow_IsReassembled()
    {
        await using var pair = await TftpPair.StartAsync(network => network.ReorderDataPairsRemaining = 1);
        var payload = Random(2000, 4);
        var result = await pair.Client.UploadAsync(pair.ServerEndPoint, "order.bin", payload, 512, 4, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Succeeded, result.Status);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(pair.Root, "order.bin")));
    }

    [Fact]
    public async Task Upload_DroppedData_FailsAfterTheRetryBudget()
    {
        await using var pair = await TftpPair.StartAsync(network => network.DropAllData = true);
        var result = await pair.Client.UploadAsync(pair.ServerEndPoint, "lost.bin", Random(200, 1), 128, 1, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Failed, result.Status);
        Assert.True(result.Timeouts > 0);
        Assert.False(File.Exists(Path.Combine(pair.Root, "lost.bin")));
        await WaitUntilAsync(() => pair.Server.Counters.FailedTransfers >= 1);
        Assert.Empty(Directory.GetFiles(Path.Combine(pair.Root, ".incoming")));
    }

    [Fact]
    public async Task Upload_ExistingFile_IsNotOverwritten()
    {
        await using var pair = await TftpPair.StartAsync();
        var first = Random(300, 2);
        var second = Random(300, 3);
        Assert.Equal(TftpTransferStatus.Succeeded, (await pair.Client.UploadAsync(pair.ServerEndPoint, "same.bin", first, 512, 1, CancellationToken.None)).Status);
        var again = await pair.Client.UploadAsync(pair.ServerEndPoint, "same.bin", second, 512, 1, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Failed, again.Status);
        Assert.Equal(TftpErrorCode.FileExists, again.ErrorCode);
        Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(pair.Root, "same.bin")));
    }

    [Fact]
    public async Task Upload_AboveTheConfiguredMaximum_IsRejected()
    {
        await using var pair = await TftpPair.StartAsync(maxArticleBytes: 100);
        var result = await pair.Client.UploadAsync(pair.ServerEndPoint, "big.bin", new byte[101], 64, 1, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Failed, result.Status);
        Assert.Equal(TftpErrorCode.DiskFull, result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(pair.Root, "big.bin")));
    }

    [Fact]
    public async Task Upload_MalformedData_AbortsWithoutPublishing()
    {
        await using var pair = await TftpPair.StartAsync(network => network.CorruptDataRemaining = 1);
        var result = await pair.Client.UploadAsync(pair.ServerEndPoint, "bad.bin", Random(200, 5), 64, 1, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Failed, result.Status);
        Assert.False(File.Exists(Path.Combine(pair.Root, "bad.bin")));
    }

    [Fact]
    public async Task Upload_UnexpectedTransferId_DoesNotAbortTheRealTransfer()
    {
        await using var pair = await TftpPair.StartAsync();
        MemorySocket? rogue = null;
        var binds = 0;
        pair.Network.OnBound = endpoint =>
        {
            var number = Interlocked.Increment(ref binds);
            if (number != 2)
            {
                return;
            }

            rogue = (MemorySocket)pair.Network.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var ack = new byte[4];
            TftpCodec.EncodeAck(ack, 99);
            rogue.SendToAsync(ack, endpoint, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        };
        var payload = Random(1500, 6);
        var result = await pair.Client.UploadAsync(pair.ServerEndPoint, "tid.bin", payload, 512, 1, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Succeeded, result.Status);
        Assert.NotNull(rogue);
        var buffer = new byte[128];
        var received = await rogue!.ReceiveAsync(buffer, CancellationToken.None);
        Assert.True(TftpCodec.TryRead(buffer.AsSpan(0, received.Count), out var packet, out _));
        Assert.Equal(TftpOpcode.Error, packet.Opcode);
        Assert.Equal(TftpErrorCode.UnknownTransferId, packet.ErrorCode);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(pair.Root, "tid.bin")));
        await rogue.DisposeAsync();
    }

    [Fact]
    public async Task Upload_Cancellation_StopsAndLeavesNoPartial()
    {
        await using var pair = await TftpPair.StartAsync(network => network.DropAllData = true);
        using var cancellation = new CancellationTokenSource();
        var upload = pair.Client.UploadAsync(pair.ServerEndPoint, "stop.bin", Random(400, 1), 128, 1, cancellation.Token);
        await WaitUntilAsync(() => pair.Server.Counters.AcceptedWrites >= 1);
        await cancellation.CancelAsync();
        var result = await upload;
        Assert.Equal(TftpTransferStatus.Cancelled, result.Status);
        Assert.False(File.Exists(Path.Combine(pair.Root, "stop.bin")));
    }

    [Fact]
    public async Task Upload_ConcurrentNames_StayDistinct()
    {
        await using var pair = await TftpPair.StartAsync();
        var payloads = Enumerable.Range(0, 8).Select(index => Random(1200, (ulong)index + 1)).ToArray();
        var results = await Task.WhenAll(payloads.Select((payload, index) =>
            pair.Client.UploadAsync(pair.ServerEndPoint, $"c{index}.bin", payload, 512, 4, CancellationToken.None)));
        Assert.All(results, result => Assert.Equal(TftpTransferStatus.Succeeded, result.Status));
        for (var index = 0; index < payloads.Length; index++)
        {
            Assert.Equal(payloads[index], await File.ReadAllBytesAsync(Path.Combine(pair.Root, $"c{index}.bin")));
        }
    }

    [Fact]
    public async Task Server_SecondTransferWhileBusy_IsRejected()
    {
        await using var pair = await TftpPair.StartAsync(network => network.DropAllData = true, maxTransfers: 1);
        using var cancellation = new CancellationTokenSource();
        var first = pair.Client.UploadAsync(pair.ServerEndPoint, "hold.bin", Random(200, 1), 64, 1, cancellation.Token);
        await WaitUntilAsync(() => pair.Server.Counters.AcceptedWrites >= 1);
        var second = await pair.Client.UploadAsync(pair.ServerEndPoint, "next.bin", Random(200, 2), 64, 1, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Failed, second.Status);
        Assert.Contains("busy", second.ErrorMessage, StringComparison.Ordinal);
        await cancellation.CancelAsync();
        await first;
        Assert.False(File.Exists(Path.Combine(pair.Root, "next.bin")));
    }

    [Fact]
    public async Task Download_RoundTrip_MatchesTheUploadedBytes()
    {
        await using var pair = await TftpPair.StartAsync();
        var payload = Random(4096, 11);
        Assert.Equal(TftpTransferStatus.Succeeded, (await pair.Client.UploadAsync(pair.ServerEndPoint, "pull.bin", payload, 512, 4, CancellationToken.None)).Status);
        var destination = Path.Combine(pair.Root, "pulled.bin");
        var result = await pair.Client.DownloadAsync(pair.ServerEndPoint, "pull.bin", destination, 512, 4, 8 * 1024 * 1024, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Succeeded, result.Status);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(SHA256.HashData(payload), result.Sha256);
    }

    [Fact]
    public async Task Upload_BlockNumberWrap_PreservesTheHash()
    {
        await using var pair = await TftpPair.StartAsync(maxWindowSize: 32);
        var payload = Random((8 * 65536) + 3, 21);
        var result = await pair.Client.UploadAsync(pair.ServerEndPoint, "wrap.bin", payload, 8, 32, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Succeeded, result.Status);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(pair.Root, "wrap.bin"))));
    }

    [Fact]
    public async Task RawRequest_IllegalBlockSizeAndPath_AreRejected()
    {
        await using var pair = await TftpPair.StartAsync();
        var illegal = await ExchangeAsync(pair, Request("nope.bin", ("blksize", "4")));
        Assert.Equal(TftpErrorCode.OptionNegotiationFailed, illegal.ErrorCode);
        var escaped = await ExchangeAsync(pair, Request("a..b.bin"));
        Assert.Equal(TftpErrorCode.AccessViolation, escaped.ErrorCode);
    }

    [Fact]
    public async Task RawRequest_UnknownOptionIsOmittedFromOack()
    {
        await using var pair = await TftpPair.StartAsync();
        await using var client = (MemorySocket)pair.Network.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var request = Request("opt.bin", ("blksize", "512"), ("windowsize", "1"), ("tsize", "5"));
        await client.SendToAsync(request, pair.ServerEndPoint, CancellationToken.None);
        var buffer = new byte[512];
        var received = await client.ReceiveAsync(buffer, CancellationToken.None);
        ushort oackOpcode;
        List<TftpOption> oackOptions;
        if (!TftpCodec.TryRead(buffer.AsSpan(0, received.Count), out var oack, out var error))
        {
            throw new InvalidOperationException(error);
        }

        oackOpcode = oack.Opcode;
        oackOptions = oack.Options.ToList();
        Assert.Equal(TftpOpcode.OptionAcknowledgement, oackOpcode);
        Assert.DoesNotContain(oackOptions, option => option.Name.Equals("tsize", StringComparison.OrdinalIgnoreCase));
        var body = "hello"u8.ToArray();
        var data = new byte[4 + body.Length];
        var length = TftpCodec.EncodeData(data, 1, body);
        await client.SendToAsync(data.AsMemory(0, length), received.Remote, CancellationToken.None);
        var ackBuffer = new byte[16];
        var ack = await client.ReceiveAsync(ackBuffer, CancellationToken.None);
        ushort ackOpcode;
        ushort ackBlock;
        if (!TftpCodec.TryRead(ackBuffer.AsSpan(0, ack.Count), out var ackPacket, out _))
        {
            throw new InvalidOperationException("ACK did not decode.");
        }

        ackOpcode = ackPacket.Opcode;
        ackBlock = ackPacket.Block;
        Assert.Equal(TftpOpcode.Acknowledgement, ackOpcode);
        Assert.Equal((ushort)1, ackBlock);
        Assert.Equal(body, await File.ReadAllBytesAsync(Path.Combine(pair.Root, "opt.bin")));
    }

    [Fact]
    public async Task Loopback_UploadsPartialExactEmptyAndArticleSizedPayloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "tftp-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var timing = new TftpTiming
        {
            AcknowledgementTimeout = TimeSpan.FromMilliseconds(200),
            MaxTimeouts = 6,
            FinalAckLinger = TimeSpan.FromMilliseconds(20),
        };
        var factory = new UdpDatagramTransportFactory();
        var server = new TftpServer(new TftpServerOptions
        {
            RootDirectory = root,
            BindAddress = IPAddress.Loopback,
            Port = 0,
            Timing = timing,
            OnReceived = (article, _) =>
            {
                article.Publish();
                return ValueTask.CompletedTask;
            },
        }, factory);
        using var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        await server.Started;
        try
        {
            var client = new TftpClient(factory, IPAddress.Loopback, timing);
            var remote = server.LocalEndPoint!;
            await AssertUploadAsync(client, remote, root, "partial.bin", 1000, 512, 1);
            await AssertUploadAsync(client, remote, root, "exact.bin", 1024, 512, 2);
            await AssertUploadAsync(client, remote, root, "empty.bin", 0, 512, 1);
            var article = Random(768_000, 42);
            var tasks = Enumerable.Range(0, 4).Select(index => client.UploadAsync(remote, $"a{index}.bin", article, 1428, 16, CancellationToken.None)).ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.All(results, result => Assert.Equal(TftpTransferStatus.Succeeded, result.Status));
            var stored = await File.ReadAllBytesAsync(Path.Combine(root, "a0.bin"));
            Assert.Equal(768_000, stored.Length);
            Assert.Equal(SHA256.HashData(article), SHA256.HashData(stored));
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
            }

            await server.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertUploadAsync(TftpClient client, IPEndPoint remote, string root, string name, int size, int blockSize, int windowSize)
    {
        var payload = size == 0 ? [] : Random(size, 3);
        var result = await client.UploadAsync(remote, name, payload, blockSize, windowSize, CancellationToken.None);
        Assert.Equal(TftpTransferStatus.Succeeded, result.Status);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(root, name)));
    }

    private static async Task<(ushort Opcode, ushort ErrorCode)> ExchangeAsync(TftpPair pair, byte[] request)
    {
        await using var client = (MemorySocket)pair.Network.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendToAsync(request, pair.ServerEndPoint, CancellationToken.None);
        var buffer = new byte[512];
        var received = await client.ReceiveAsync(buffer, CancellationToken.None);
        if (!TftpCodec.TryRead(buffer.AsSpan(0, received.Count), out var packet, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return (packet.Opcode, packet.ErrorCode);
    }

    private static byte[] Request(string fileName, params (string Name, string Value)[] options)
    {
        var encoded = options.Select(option => new TftpOption(option.Name, option.Value)).ToArray();
        var buffer = new byte[512];
        var length = TftpCodec.EncodeRequest(buffer, TftpOpcode.WriteRequest, fileName, "octet", encoded);
        return buffer[..length];
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed > TimeSpan.FromSeconds(2))
            {
                throw new TimeoutException("Condition was not observed.");
            }

            await Task.Yield();
        }
    }

    private static byte[] Random(int size, ulong seed)
    {
        var data = new byte[size];
        var state = seed == 0 ? 1UL : seed;
        for (var i = 0; i < size; i++)
        {
            state = (state * 6364136223846793005UL) + 1;
            data[i] = (byte)(state >> 33);
        }

        return data;
    }
}
