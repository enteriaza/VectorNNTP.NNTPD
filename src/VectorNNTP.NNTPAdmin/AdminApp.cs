using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Configuration;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Session.Commands.Posting;

namespace VectorNNTP.NNTPAdmin;

/// <summary>Orchestrates inspect / optional cancel for one Message-ID.</summary>
internal static class AdminApp
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IConfiguration? configuration = null,
        INntpAdminTransport? transport = null,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default)
    {
        if (!AdminCliParser.TryParse(args, out var arguments, out var parseError))
        {
            stderr.WriteLine(parseError);
            stderr.WriteLine();
            stderr.WriteLine(AdminCliParser.Usage);
            return AdminExitCode.Usage;
        }

        if (arguments.Help)
        {
            stdout.WriteLine(AdminCliParser.Usage);
            return AdminExitCode.Success;
        }

        configuration ??= BuildConfiguration();
        var admin = new NntpAdminOptions();
        configuration.GetSection(NntpAdminOptions.SectionName).Bind(admin);
        var nntpd = new NntpdOptions();
        configuration.GetSection(NntpdOptions.SectionName).Bind(nntpd);

        if (!AdminSettings.TryCreate(arguments, admin, nntpd, out var settings, out var settingsError))
        {
            stderr.WriteLine(settingsError);
            return AdminExitCode.Configuration;
        }

        using var pgp = settings.PgpSigner;
        AdminReport.WriteTransport(stdout, settings);
        stdout.WriteLine($"Message-ID: {arguments.MessageId}");

        await using var session = transport ?? new TcpNntpAdminTransport();
        try
        {
            await session.ConnectAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException)
        {
            stderr.WriteLine("Connection failed.");
            stderr.WriteLine(Sanitize(ex.Message, settings));
            return AdminExitCode.Connection;
        }

        if (settings.HasCredentials)
        {
            try
            {
                await session.AuthenticateAsync(settings.NewsmasterUser!, settings.NewsmasterPassword!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (NntpAdminAuthenticationException ex)
            {
                stderr.WriteLine("AUTHINFO failed.");
                stderr.WriteLine(Sanitize(ex.Message, settings));
                return AdminExitCode.AuthenticationFailed;
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                stderr.WriteLine("AUTHINFO failed.");
                stderr.WriteLine(Sanitize(ex.Message, settings));
                return AdminExitCode.AuthenticationFailed;
            }

            AdminReport.WriteAuthenticated(stdout, settings.NewsmasterUser!);
        }
        else if (arguments.Cancel)
        {
            stderr.WriteLine("Cancel refused: authentication is required.");
            return AdminExitCode.AuthenticationFailed;
        }

        HeadExchange head;
        try
        {
            head = await session.HeadAsync(arguments.MessageId, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            stderr.WriteLine("HEAD failed.");
            stderr.WriteLine(Sanitize(ex.Message, settings));
            return AdminExitCode.HeadFailed;
        }

        if (!head.Success)
        {
            AdminReport.WriteArticleHeaders(stdout, head.Headers);
            stderr.WriteLine(head.NotFound
                ? $"Article not found (430): {head.StatusLine}"
                : $"HEAD failed: {head.StatusLine}");
            return AdminExitCode.HeadFailed;
        }

        AdminReport.WriteArticleHeaders(stdout, head.Headers);

        var hasTrace = AdminReport.TryFindHeader(head.Headers, "X-Trace", out var token);
        PostingTracePayload payload = default;
        var decrypted = hasTrace && settings.TraceProtector.TryUnprotect(token, out payload);
        AdminReport.WriteTrace(stdout, token, hasTrace, decrypted, payload);

        if (!arguments.Cancel)
        {
            return decrypted ? AdminExitCode.Success : AdminExitCode.TraceFailed;
        }

        if (!decrypted)
        {
            stderr.WriteLine("Cancel refused: X-Trace must decrypt successfully before mutation.");
            return AdminExitCode.TraceFailed;
        }

        if (!AdminReport.TryFindHeader(head.Headers, "Newsgroups", out var newsgroups))
        {
            stderr.WriteLine("Cancel refused: HEAD response has no Newsgroups header.");
            return AdminExitCode.CancelFailed;
        }

        AdminReport.WriteCancelRequested(stdout, arguments.MessageId, settings.NewsmasterUser);
        if (!CancelArticleBuilder.TryBuild(
                arguments.MessageId,
                newsgroups,
                settings.From,
                (time ?? TimeProvider.System).GetUtcNow(),
                out var unsigned,
                out var cancelId,
                out var buildError))
        {
            stderr.WriteLine(buildError);
            return AdminExitCode.CancelFailed;
        }

        if (settings.PgpSigner is null)
        {
            stderr.WriteLine("CANCEL requires PGP signing to be configured.");
            return AdminExitCode.Configuration;
        }

        if (!settings.PgpSigner.TrySignArticle(unsigned, out var article, out var signError))
        {
            stderr.WriteLine(signError);
            return AdminExitCode.CancelFailed;
        }

        if (!article.Contains("Control: cancel " + arguments.MessageId, StringComparison.Ordinal)
            || article.Contains("Message-ID: " + arguments.MessageId + "\r\n", StringComparison.Ordinal)
            || !article.Contains("X-PGP-Sig:", StringComparison.Ordinal))
        {
            stderr.WriteLine("Cancel refused: constructed article did not target the requested Message-ID.");
            return AdminExitCode.CancelFailed;
        }

        AdminReport.WriteCancelArticle(stdout, cancelId, arguments.MessageId, newsgroups);
        AdminReport.WritePgpVerify(stdout, settings.PgpSigner.Identity);

        PostExchange posted;
        try
        {
            posted = await session.PostAsync(article, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            stderr.WriteLine("Cancel POST failed.");
            stderr.WriteLine(Sanitize(ex.Message, settings));
            return AdminExitCode.CancelFailed;
        }

        AdminReport.WriteCancelResult(stdout, posted.StatusLine, posted.Success);
        return posted.Success ? AdminExitCode.Success : AdminExitCode.CancelFailed;
    }

    internal static IConfiguration BuildConfiguration()
    {
        var basePath = AppContext.BaseDirectory;
        return new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
    }

    private static string Sanitize(string message, AdminSettings settings)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        var redacted = message;
        if (!string.IsNullOrEmpty(settings.NewsmasterPassword))
        {
            redacted = redacted.Replace(settings.NewsmasterPassword, "<redacted>", StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(settings.PgpPassphrase))
        {
            redacted = redacted.Replace(settings.PgpPassphrase, "<redacted>", StringComparison.Ordinal);
        }

        return redacted;
    }
}
