using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Commands;
using VectorNNTP.NNTPD.Tests.Networking.Transport;

namespace VectorNNTP.NNTPD.Tests.Session;

/// <summary>IHAVE over the production accept/transport path.</summary>
[Collection(nameof(TransportTestHostCollection))]
public sealed class IHaveTcpTests
{
    [Fact]
    public async Task Tcp_SegmentedArticle_ThenNextCommandInSameReceive_LeavesCommandIntact()
    {
        var queue = new ArticleIngestionQueue(new ArticleIngestionOptions { QueueCapacity = 4 });
        await using var host = await TransportTestHost.StartPlainAsync();
        using var client = await host.ConnectPlainClientAsync();
        await using var server = await host.AcceptAsync();
        var session = new NntpSession(
            server,
            NullLogger<NntpSession>.Instance,
            articleIngestion: queue);
        session.SetAuthorization(NntpAuthorization.TrustedTransitPeer);
        var run = session.RunAsync();
        var rx = new SocketLineBuffer();

        Assert.StartsWith("201 ", await rx.ReadLineAsync(client), StringComparison.Ordinal);

        await client.SendAsync("IHAVE <tcp-ihave@example.com>\r\n"u8.ToArray());
        Assert.Equal("335 Send article to be transferred", await rx.ReadLineAsync(client));

        await client.SendAsync("Subject: split\r\n"u8.ToArray());
        await Task.Yield();
        await client.SendAsync("\r\n"u8.ToArray());
        await Task.Yield();
        var body = Encoding.ASCII.GetBytes(new string('T', 4096) + "\r\n.\r\nDATE\r\n");
        await client.SendAsync(body);

        Assert.Equal("235 Article transferred OK", await rx.ReadLineAsync(client));
        Assert.StartsWith("111 ", await rx.ReadLineAsync(client), StringComparison.Ordinal);

        using var dequeueCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var inbound = await queue.DequeueAsync(dequeueCts.Token);
        Assert.NotNull(inbound);
        Assert.Equal(InboundArticleProducer.IHave, inbound!.Producer);
        Assert.Null(inbound.Structured);
        Assert.True(inbound.Payload.Length >= 4096);
        Assert.DoesNotContain("DATE"u8.ToArray(), inbound.Payload.ToArray());

        var interpreted = IhaveArticleInterpreter.Interpret(inbound, 8 * 1024 * 1024);
        Assert.NotNull(interpreted.Structured);
        Assert.True(interpreted.Structured!.Value.Body.Length >= 4096);
        Assert.Equal(interpreted.Structured.Value.Size, interpreted.Payload.Length);

        await client.SendAsync("QUIT\r\n"u8.ToArray());
        Assert.Equal("205 Connection closing", await rx.ReadLineAsync(client));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        client.Shutdown(SocketShutdown.Both);
    }

    private sealed class SocketLineBuffer
    {
        private readonly List<byte> _bytes = [];

        public async Task<string> ReadLineAsync(Socket socket)
        {
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                if (TryReadLine(out var line))
                {
                    return line;
                }

                var scratch = new byte[512];
                var n = await socket.ReceiveAsync(scratch, safety.Token);
                Assert.True(n > 0, "Socket closed while reading IHAVE response.");
                _bytes.AddRange(scratch.AsSpan(0, n));
            }
        }

        private bool TryReadLine(out string line)
        {
            line = string.Empty;
            for (var i = 0; i + 1 < _bytes.Count; i++)
            {
                if (_bytes[i] != (byte)'\r' || _bytes[i + 1] != (byte)'\n')
                {
                    continue;
                }

                line = Encoding.ASCII.GetString(_bytes.GetRange(0, i).ToArray());
                _bytes.RemoveRange(0, i + 2);
                return true;
            }

            return false;
        }
    }
}
