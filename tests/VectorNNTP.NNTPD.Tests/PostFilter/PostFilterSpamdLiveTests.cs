using System.Net;
using System.Net.Sockets;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.PostFilter;

namespace VectorNNTP.NNTPD.Tests.PostFilter;

/// <summary>Real SPAMD CHECK against the lab scanner. Not a mock.</summary>
public sealed class PostFilterSpamdLiveTests
{
    private const string Host = "198.18.0.70";
    private const int Port = 783;
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RealSpamd_Check_AcceptsEmailLikeScanAsHam()
    {
        var original = Encoding.ASCII.GetBytes(
            "From: ham@example.com\r\n" +
            "Newsgroups: misc.test\r\n" +
            "Subject: lab ham\r\n" +
            "Message-ID: <ham-vectornntp-lab@example.com>\r\n" +
            "Date: 1 Jan 2026 00:00:00 +0000\r\n" +
            "\r\n" +
            "This is a short ham body.\r\n");
        var copy = original.ToArray();
        var article = new ArticleRecord(
            ArticleId.FromMessageId("<ham-vectornntp-lab@example.com>"u8),
            artHash: 1,
            artType: ArticleType.Default,
            artLines: 1,
            canonicalUtc: Now.UtcDateTime,
            parseStatus: ArticleParseStatus.CanonicalV1,
            artData: original,
            fields: default);
        var context = new SpamdScanContext(IPAddress.Parse("203.0.113.8"), "nntpd01.usenet.ninja", Now);
        var scan = SpamdScanArticleBuilder.Build(article, context);
        var scanText = Encoding.ASCII.GetString(scan);
        Assert.Contains("Received: from [203.0.113.8]", scanText, StringComparison.Ordinal);
        Assert.Contains("To: usenet@nntpd01.usenet.ninja", scanText, StringComparison.Ordinal);
        Assert.Contains("X-Usenet-Newsgroups: misc.test", scanText, StringComparison.Ordinal);
        Assert.Contains("Message-ID: <ham-vectornntp-lab@example.com>", scanText, StringComparison.Ordinal);
        Assert.True(copy.AsSpan().SequenceEqual(article.ArtData.Span));

        var target = new PostFilterSpamAssassinTarget(
            [Host],
            Port,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            PostFilterSpamAssassinOptions.DefaultProtocolVersion,
            1,
            PostFilterSpamAssassinHostSelection.RoundRobin);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        var result = await client.CheckAsync(article, "poster", target, context, CancellationToken.None);
        Assert.Equal(PostFilterSpamAssassinStatus.Ham, result.Status);
        Assert.True(copy.AsSpan().SequenceEqual(article.ArtData.Span));

        var symbols = await RequestSymbolsAsync(scan);
        Assert.Contains("SPAMD/", symbols, StringComparison.Ordinal);
        Assert.Contains("EX_OK", symbols, StringComparison.Ordinal);
        Assert.DoesNotContain("NO_RECEIVED", symbols, StringComparison.Ordinal);
        Assert.DoesNotContain("MISSING_HEADERS", symbols, StringComparison.Ordinal);
        Assert.Equal(1, metrics.CheckRequests);
        Assert.Equal(1, metrics.ConnectionsEstablished);
    }

    [Fact]
    public async Task RealSpamd_SequentialChecks_ReportConnectionReuse()
    {
        var bytes = Encoding.ASCII.GetBytes(
            "From: ham@example.com\r\n" +
            "Newsgroups: misc.test\r\n" +
            "Subject: reuse\r\n" +
            "Message-ID: <ham-reuse-lab@example.com>\r\n" +
            "Date: 1 Jan 2026 00:00:00 +0000\r\n" +
            "\r\n" +
            "short\r\n");
        var article = new ArticleRecord(
            ArticleId.FromMessageId("<ham-reuse-lab@example.com>"u8),
            artHash: 2,
            artType: ArticleType.Default,
            artLines: 1,
            canonicalUtc: Now.UtcDateTime,
            parseStatus: ArticleParseStatus.CanonicalV1,
            artData: bytes,
            fields: default);
        var context = new SpamdScanContext(IPAddress.Loopback, "nntpd01.usenet.ninja", Now);
        var target = new PostFilterSpamAssassinTarget(
            [Host],
            Port,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            PostFilterSpamAssassinOptions.DefaultProtocolVersion,
            1,
            PostFilterSpamAssassinHostSelection.RoundRobin);
        var metrics = new SpamdTransportMetrics();
        await using var client = new SpamdCheckClient(metrics);
        for (var i = 0; i < 20; i++)
        {
            var result = await client.CheckAsync(article, "poster", target, context);
            Assert.Equal(PostFilterSpamAssassinStatus.Ham, result.Status);
        }

        Assert.Equal(20, metrics.CheckRequests);
        Assert.True(metrics.ConnectionsEstablished >= 1);
        Assert.True(metrics.ConnectionsEstablished <= 20);
        Console.WriteLine(
            $"SPAMD transport: connects={metrics.ConnectionsEstablished} checks={metrics.CheckRequests} reuses={metrics.ConnectionReuses} reconnects={metrics.Reconnects} evictions={metrics.Evictions} avgCheck={metrics.AverageCheckLatency.TotalMilliseconds:F1}ms");
    }

    private static async Task<string> RequestSymbolsAsync(byte[] scan)
    {
        using var tcp = new TcpClient();
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await tcp.ConnectAsync(Host, Port, connectCts.Token);
        await using var stream = tcp.GetStream();
        var header = Encoding.ASCII.GetBytes(
            $"SYMBOLS SPAMC/1.5\r\nContent-length: {scan.Length}\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(scan);
        await stream.FlushAsync();
        tcp.Client.Shutdown(SocketShutdown.Send);
        var buffer = new byte[4096];
        var total = 0;
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), readCts.Token);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return Encoding.ASCII.GetString(buffer.AsSpan(0, total));
    }
}
