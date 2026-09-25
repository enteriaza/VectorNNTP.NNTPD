using System.Net;
using System.Net.Sockets;
using VectorNNTP.NNTPCancelMessage;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Session.Commands.Posting;

namespace VectorNNTP.NNTPCancelMessage.Tests;

public sealed class NntpCancelMessageClientTests
{
    private const string TestKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Head_ReadsMultilineHeaders()
    {
        await using var server = await ScriptedNntpServer.StartAsync();
        await using var client = new NntpCancelMessageClient();
        await client.ConnectAsync(Settings(server.Port), CancellationToken.None);
        var head = await client.HeadAsync("<abc@example.com>", CancellationToken.None);
        Assert.True(head.Success);
        Assert.Contains("From: poster@example.com", head.Headers);
        Assert.Contains("X-Trace: token", head.Headers);
        Assert.Equal("HEAD <abc@example.com>", server.Commands[0]);
    }

    [Fact]
    public async Task Head_430_DoesNotReadBody()
    {
        await using var server = await ScriptedNntpServer.StartAsync(headStatus: "430 No such article\r\n");
        await using var client = new NntpCancelMessageClient();
        await client.ConnectAsync(Settings(server.Port), CancellationToken.None);
        var head = await client.HeadAsync("<missing@example.com>", CancellationToken.None);
        Assert.True(head.NotFound);
        Assert.Empty(head.Headers);
        Assert.Equal(0, server.PostCount);
    }

    [Fact]
    public async Task Post_SendsArticleAndReads240()
    {
        await using var server = await ScriptedNntpServer.StartAsync();
        await using var client = new NntpCancelMessageClient();
        await client.ConnectAsync(Settings(server.Port), CancellationToken.None);
        var posted = await client.PostAsync("From: a@b\r\n\r\nbody\r\n", CancellationToken.None);
        Assert.True(posted.Success);
        Assert.Equal("POST", server.Commands[0]);
        Assert.Contains("From: a@b", server.LastArticle, StringComparison.Ordinal);
        Assert.Equal(1, server.PostCount);
    }

    [Fact]
    public async Task Authenticate_ThenHead_UsesSuppliedUsername()
    {
        await using var server = await ScriptedNntpServer.StartAsync(
            expectedUsername: "newsmaster",
            expectedPassword: "unit-test-newsmaster-password",
            requireAuthentication: true);
        await using var client = new NntpCancelMessageClient();
        await client.ConnectAsync(Settings(server.Port), CancellationToken.None);
        await client.AuthenticateAsync("newsmaster", "unit-test-newsmaster-password", CancellationToken.None);
        var head = await client.HeadAsync("<abc@example.com>", CancellationToken.None);
        Assert.True(head.Success);
        Assert.True(server.Authenticated);
        Assert.Equal("newsmaster", server.LastAuthUsername);
        Assert.Contains("AUTHINFO USER newsmaster", server.Commands);
        Assert.Contains("AUTHINFO PASS <redacted>", server.Commands);
        Assert.DoesNotContain(server.Commands, static line => line.Contains("unit-test-newsmaster-password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Authenticate_WrongPassword_ThrowsWithoutEchoingSecret()
    {
        await using var server = await ScriptedNntpServer.StartAsync(
            expectedUsername: "newsmaster",
            expectedPassword: "unit-test-newsmaster-password");
        await using var client = new NntpCancelMessageClient();
        await client.ConnectAsync(Settings(server.Port), CancellationToken.None);
        var error = await Assert.ThrowsAsync<NntpCancelMessageAuthenticationException>(
            () => client.AuthenticateAsync("newsmaster", "wrong-secret", CancellationToken.None));
        Assert.Equal("AUTHINFO PASS failed.", error.Message);
        Assert.DoesNotContain("wrong-secret", error.Message, StringComparison.Ordinal);
        Assert.False(server.Authenticated);
    }

    [Fact]
    public async Task Connect_Refused_Throws()
    {
        var unused = new TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        var port = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();

        await using var client = new NntpCancelMessageClient();
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(Settings(port), CancellationToken.None));
    }

    private static AdminSettings Settings(int port)
    {
        var nntpd = new NntpdOptions { XTraceKey = TestKey };
        return new AdminSettings
        {
            Host = "127.0.0.1",
            Port = port,
            UseTls = false,
            From = "newsmaster@usenet.ninja",
            TraceProtector = AesGcmPostingTraceProtector.Create(nntpd),
        };
    }
}
