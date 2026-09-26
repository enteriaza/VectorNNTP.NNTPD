using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Nntp;

public sealed class NntpProviderSessionTests
{
    [Fact]
    public async Task Connect_accepts_200_greeting_without_authentication()
    {
        var factory = new ScriptedNntpTransportFactory();
        factory.Enqueue(new ScriptedNntpServer("200 ready"));
        var session = CreateSession();

        var failure = await session.ConnectAsync(factory, CancellationToken.None);

        Assert.Null(failure);
        Assert.Equal(NntpSessionState.Ready, session.State);
        Assert.False(factory.ConnectAttempts[0].UseTls);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Connect_accepts_201_greeting()
    {
        var factory = new ScriptedNntpTransportFactory();
        factory.Enqueue(new ScriptedNntpServer("201 posting prohibited"));
        var session = CreateSession();

        Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Connect_rejects_malformed_and_non_ready_greetings()
    {
        var malformed = new ScriptedNntpTransportFactory();
        malformed.Enqueue(new ScriptedNntpServer("hello"));
        var bad = new ScriptedNntpTransportFactory();
        bad.Enqueue(new ScriptedNntpServer("400 service unavailable"));

        var first = CreateSession();
        var second = CreateSession();
        var malformedResult = await first.ConnectAsync(malformed, CancellationToken.None);
        var badResult = await second.ConnectAsync(bad, CancellationToken.None);

        Assert.Equal(ArticleRetrievalKind.ProviderFailure, malformedResult!.Kind);
        Assert.False(malformedResult.SessionReusable);
        Assert.Equal(ArticleRetrievalKind.ProviderFailure, badResult!.Kind);
        Assert.Equal(400, badResult.StatusCode);
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Connect_failure_and_timeout_are_provider_failures()
    {
        var factory = new ScriptedNntpTransportFactory
        {
            ConnectException = new IOException("refused"),
        };
        var session = CreateSession();

        var result = await session.ConnectAsync(factory, CancellationToken.None);
        Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Authentication_success_and_failure()
    {
        var okFactory = new ScriptedNntpTransportFactory();
        var okServer = new ScriptedNntpServer();
        okServer.Respond(static command => command.StartsWith("AUTHINFO USER", StringComparison.Ordinal)
            ? "381 password required\r\n"
            : "281 ok\r\n");
        okFactory.Enqueue(okServer);

        var failFactory = new ScriptedNntpTransportFactory();
        var failServer = new ScriptedNntpServer();
        failServer.Respond(static _ => "481 rejected\r\n");
        failFactory.Enqueue(failServer);

        var ok = CreateSession(user: "user", password: "secret");
        var fail = CreateSession(user: "user", password: "secret");
        Assert.Null(await ok.ConnectAsync(okFactory, CancellationToken.None));
        Assert.Equal("AUTHINFO USER user", okServer.Commands[0]);
        Assert.Equal("AUTHINFO PASS secret", okServer.Commands[1]);
        var failed = await fail.ConnectAsync(failFactory, CancellationToken.None);
        Assert.Equal(ArticleRetrievalKind.AuthenticationFailure, failed!.Kind);
        Assert.DoesNotContain("secret", failed.Reason, StringComparison.Ordinal);
        await ok.DisposeAsync();
        await fail.DisposeAsync();
    }

    [Fact]
    public async Task Half_configured_credentials_fail_without_sending_authinfo()
    {
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        factory.Enqueue(server);
        var session = CreateSession(user: "only-user", password: null);

        var result = await session.ConnectAsync(factory, CancellationToken.None);
        Assert.Equal(ArticleRetrievalKind.AuthenticationFailure, result!.Kind);
        Assert.DoesNotContain(server.Commands, static command => command.StartsWith("AUTHINFO", StringComparison.Ordinal));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Article_preserves_exact_message_id_and_destuffs_payload()
    {
        const string messageId = "<AbC@Example.INVALID>";
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        server.RespondBytes(static command =>
        {
            if (!command.StartsWith("ARTICLE ", StringComparison.Ordinal))
            {
                return "500 unknown\r\n"u8.ToArray();
            }

            return "220 0 <AbC@Example.INVALID> article follows\r\nFrom: a@b\r\n\r\n..hidden\r\nbody\r\n.\r\n"u8.ToArray();
        });
        factory.Enqueue(server);
        var session = CreateSession();
        Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

        using var result = await session.DownloadArticleAsync(messageId, CancellationToken.None);

        Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
        Assert.Equal($"ARTICLE {messageId}", Assert.Single(server.Commands));
        Assert.True(server.LastCommandUsedCrlf);
        var text = Encoding.ASCII.GetString(result.Article!.Memory.Span);
        Assert.Contains(".hidden", text, StringComparison.Ordinal);
        Assert.DoesNotContain("..hidden", text, StringComparison.Ordinal);
        Assert.False(text.EndsWith(".\r\n", StringComparison.Ordinal));
        Assert.True(session.IsReusable);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Article_not_found_is_reusable()
    {
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        server.Respond(static _ => "430 No such article\r\n");
        factory.Enqueue(server);
        var session = CreateSession();
        Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

        using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
        Assert.Equal(ArticleRetrievalKind.ArticleNotFound, result.Kind);
        Assert.True(result.SessionReusable);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Missing_terminator_and_malformed_status_retire_the_session()
    {
        var truncatedFactory = new ScriptedNntpTransportFactory();
        var truncated = new ScriptedNntpServer { CompleteAfterResponse = true };
        truncated.Respond(static _ => "220 article follows\r\nFrom: a@b\r\n\r\nbody\r\n");
        truncatedFactory.Enqueue(truncated);

        var malformedFactory = new ScriptedNntpTransportFactory();
        var malformed = new ScriptedNntpServer();
        malformed.Respond(static _ => "not-a-status\r\n");
        malformedFactory.Enqueue(malformed);

        var first = CreateSession();
        var second = CreateSession();
        Assert.Null(await first.ConnectAsync(truncatedFactory, CancellationToken.None));
        Assert.Null(await second.ConnectAsync(malformedFactory, CancellationToken.None));

        using var truncatedResult = await first.DownloadArticleAsync("<a@b>", CancellationToken.None);
        using var malformedResult = await second.DownloadArticleAsync("<a@b>", CancellationToken.None);
        Assert.Equal(ArticleRetrievalKind.ProviderFailure, truncatedResult.Kind);
        Assert.False(truncatedResult.SessionReusable);
        Assert.Equal(ArticleRetrievalKind.ProviderFailure, malformedResult.Kind);
        Assert.False(malformedResult.SessionReusable);
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Empty_article_without_separator_is_invalid_and_reusable()
    {
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        server.Respond(static _ => "220 follows\r\njust-a-line\r\n.\r\n");
        factory.Enqueue(server);
        var session = CreateSession();
        Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

        using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
        Assert.Equal(ArticleRetrievalKind.InvalidArticle, result.Kind);
        Assert.True(result.SessionReusable);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Payload_remains_readable_after_session_dispose()
    {
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        server.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\nbody\r\n.\r\n");
        factory.Enqueue(server);
        var session = CreateSession();
        Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
        using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
        await session.DisposeAsync();

        Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
        Assert.Contains("body"u8, result.Article!.Memory.Span);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_article_not_found()
    {
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RespondBytes(_ =>
        {
            gate.TrySetResult();
            return "220 follows\r\nFrom: a@b\r\n\r\n"u8.ToArray();
        });
        factory.Enqueue(server);
        var session = CreateSession();
        Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));
        using var cts = new CancellationTokenSource();
        var download = session.DownloadArticleAsync("<a@b>", cts.Token);
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        using var result = await download;
        Assert.Equal(ArticleRetrievalKind.Cancelled, result.Kind);
        Assert.NotEqual(ArticleRetrievalKind.ArticleNotFound, result.Kind);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Eof_before_greeting_is_provider_failure()
    {
        var factory = new ScriptedNntpTransportFactory();
        factory.Enqueue(new ScriptedNntpServer { CompleteWithoutGreeting = true });
        var session = CreateSession();

        var result = await session.ConnectAsync(factory, CancellationToken.None);
        Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
        Assert.False(result.SessionReusable);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Connect_timeout_is_provider_failure_not_not_found()
    {
        var factory = new ScriptedNntpTransportFactory
        {
            ConnectDelay = TimeSpan.FromSeconds(5),
        };
        var session = new NntpProviderSession(
            new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
            NntpSessionOptions.Default with
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(40),
                CommandTimeout = TimeSpan.FromMilliseconds(40),
                ReceiveTimeout = TimeSpan.FromMilliseconds(40),
            },
            NullLogger.Instance);

        var result = await session.ConnectAsync(factory, CancellationToken.None);
        Assert.Equal(ArticleRetrievalKind.ProviderFailure, result!.Kind);
        Assert.NotEqual(ArticleRetrievalKind.ArticleNotFound, result.Kind);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Oversized_article_is_provider_failure_and_retires_the_session()
    {
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        server.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\n" + new string('x', 64) + "\r\n.\r\n");
        factory.Enqueue(server);
        var session = new NntpProviderSession(
            new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
            NntpSessionOptions.Default with
            {
                MaxArticleBytes = 16,
                CommandTimeout = TimeSpan.FromSeconds(2),
                ReceiveTimeout = TimeSpan.FromSeconds(2),
                ConnectTimeout = TimeSpan.FromSeconds(2),
            },
            NullLogger.Instance);
        Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

        using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
        Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
        Assert.False(result.SessionReusable);
        await session.DisposeAsync();
    }

    [Theory]
    [InlineData("400 service unavailable")]
    [InlineData("503 program fault")]
    [InlineData("502 not permitted")]
    [InlineData("500 unknown command")]
    public async Task Provider_error_status_is_not_article_not_found(string status)
    {
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        server.Respond(_ => status + "\r\n");
        factory.Enqueue(server);
        var session = CreateSession();
        Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

        using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
        Assert.Equal(ArticleRetrievalKind.ProviderFailure, result.Kind);
        Assert.NotEqual(ArticleRetrievalKind.ArticleNotFound, result.Kind);
        Assert.False(result.SessionReusable);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Legitimate_leading_dot_that_is_not_stuffed_is_preserved()
    {
        var factory = new ScriptedNntpTransportFactory();
        var server = new ScriptedNntpServer();
        server.Respond(static _ => "220 follows\r\nFrom: a@b\r\n\r\n.hidden\r\nbody\r\n.\r\n");
        factory.Enqueue(server);
        var session = CreateSession();
        Assert.Null(await session.ConnectAsync(factory, CancellationToken.None));

        using var result = await session.DownloadArticleAsync("<a@b>", CancellationToken.None);
        Assert.Equal(ArticleRetrievalKind.ArticleRetrieved, result.Kind);
        var text = Encoding.ASCII.GetString(result.Article!.Memory.Span);
        Assert.Contains(".hidden", text, StringComparison.Ordinal);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Tls_flag_is_passed_to_the_transport_factory()
    {
        var factory = new ScriptedNntpTransportFactory();
        factory.Enqueue(new ScriptedNntpServer());
        var session = CreateSession(useTls: true);
        _ = await session.ConnectAsync(factory, CancellationToken.None);
        Assert.True(factory.ConnectAttempts[0].UseTls);
        await session.DisposeAsync();
    }

    [Fact]
    public void Tcp_factory_does_not_install_an_accept_all_certificate_callback()
    {
        var source = File.ReadAllText(FindSource("TcpNntpTransportFactory.cs"));
        Assert.DoesNotContain("RemoteCertificateValidationCallback", source, StringComparison.Ordinal);
        Assert.Contains("SslProtocols.Tls12", source, StringComparison.Ordinal);
        Assert.Contains("SslProtocols.Tls13", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_nntp_code_does_not_block_synchronously()
    {
        var directory = FindSourceDirectory();
        foreach (var file in Directory.GetFiles(directory, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Password={", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Username={", text, StringComparison.Ordinal);
        }
    }

    internal static NntpProviderSession CreateSession(
        bool useTls = false,
        string? user = null,
        string? password = null) =>
        new(
            new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, useTls, user, password, 0, 1),
            NntpSessionOptions.Default with { CommandTimeout = TimeSpan.FromSeconds(2), ReceiveTimeout = TimeSpan.FromSeconds(2), ConnectTimeout = TimeSpan.FromSeconds(2) },
            NullLogger.Instance);

    private static string FindSource(string fileName) => Path.Combine(FindSourceDirectory(), fileName);

    private static string FindSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "VectorNNTP.BackFiller", "Nntp");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/VectorNNTP.BackFiller/Nntp.");
    }
}
