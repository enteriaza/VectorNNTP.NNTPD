using System.Globalization;
using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Authentication;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>Builds the default NNTP command registry for the session layer.</summary>
/// <remarks>
/// Registers public/pre-auth commands, AUTHINFO USER/PASS, AUTHINFO SASL placeholder,
/// and representative reader/transit verbs so authorization gates can be tested
/// before full handlers exist.
/// </remarks>
public static class DefaultNntpCommandCatalog
{
    /// <summary>Creates the default registry.</summary>
    public static NntpCommandRegistry Create(
        ITlsCertificateContextProvider? certificateProvider = null,
        INntpAuthenticationProvider? authenticationProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        authenticationProvider ??= DenyAllNntpAuthenticationProvider.Instance;
        var registry = new NntpCommandRegistry();

        registry.Register(new NntpCommandDescriptor("CAPABILITIES", NntpCommandAccess.Public, HandleCapabilitiesAsync));
        registry.Register(new NntpCommandDescriptor("MODE", NntpCommandAccess.Public, HandleModeReaderAsync, "READER"));
        registry.Register(new NntpCommandDescriptor(
            "MODE",
            NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresTransit | NntpCommandAccess.RequiresStreaming,
            HandleModeStreamNotImplementedAsync,
            "STREAM"));
        registry.Register(new NntpCommandDescriptor("HELP", NntpCommandAccess.Public, HandleHelpAsync));
        registry.Register(new NntpCommandDescriptor("DATE", NntpCommandAccess.Public, HandleDateAsync));
        registry.Register(new NntpCommandDescriptor("QUIT", NntpCommandAccess.Public, HandleQuitAsync));
        registry.Register(new NntpCommandDescriptor(
            "STARTTLS",
            NntpCommandAccess.Public,
            (ctx, ct) => HandleStartTlsAsync(ctx, certificateProvider, ct)));

        registry.Register(new NntpCommandDescriptor(
            "AUTHINFO",
            NntpCommandAccess.Public,
            (ctx, ct) => HandleAuthinfoUserAsync(ctx, authenticationProvider, ct),
            "USER"));
        registry.Register(new NntpCommandDescriptor(
            "AUTHINFO",
            NntpCommandAccess.Public,
            (ctx, ct) => HandleAuthinfoPassAsync(ctx, authenticationProvider, ct),
            "PASS"));
        // SASL intentionally not implemented — remain registered so unknown variants vs SASL are distinct.
        registry.Register(new NntpCommandDescriptor("AUTHINFO", NntpCommandAccess.Public, HandleAuthinfoSaslNotImplementedAsync, "SASL"));

        foreach (var readerCommand in new[]
                 {
                     "LIST", "GROUP", "LISTGROUP", "ARTICLE", "HEAD", "BODY", "STAT",
                     "LAST", "NEXT", "OVER", "HDR", "POST",
                 })
        {
            var access = readerCommand == "POST"
                ? NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresReader | NntpCommandAccess.RequiresPosting
                : NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresReader;
            registry.Register(new NntpCommandDescriptor(readerCommand, access, HandleNotImplementedAsync));
        }

        foreach (var transitCommand in new[] { "IHAVE", "CHECK", "TAKETHIS" })
        {
            registry.Register(new NntpCommandDescriptor(
                transitCommand,
                NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresTransit,
                HandleNotImplementedAsync));
        }

        _ = loggerFactory;
        return registry;
    }

    private static async ValueTask HandleCapabilitiesAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        await context.Response
            .WriteMultilineStartAsync(NntpReplyCodes.CapabilityListFollows, "Capability list:", cancellationToken)
            .ConfigureAwait(false);
        await context.Response.WriteMultilineDataAsync("VERSION 2", cancellationToken).ConfigureAwait(false);
        await context.Response.WriteMultilineDataAsync("IMPLEMENTATION VectorNNTP.NNTPD", cancellationToken)
            .ConfigureAwait(false);

        var authenticated = context.Session.Authentication.IsAuthenticated;

        if (context.Session.Mode is NntpSessionMode.Unspecified or NntpSessionMode.Reader)
        {
            await context.Response.WriteMultilineDataAsync("READER", cancellationToken).ConfigureAwait(false);
            // RFC 4643: MUST NOT advertise MODE-READER after authentication.
            if (!authenticated)
            {
                await context.Response.WriteMultilineDataAsync("MODE-READER", cancellationToken).ConfigureAwait(false);
            }
        }

        if (context.Session.Authorization.PostingPermitted &&
            context.Session.Mode is NntpSessionMode.Unspecified or NntpSessionMode.Reader)
        {
            await context.Response.WriteMultilineDataAsync("POST", cancellationToken).ConfigureAwait(false);
        }

        // RFC 4643: MUST NOT return AUTHINFO after successful authentication.
        if (!authenticated)
        {
            if (context.Session.IsAuthinfoPassPermitted)
            {
                await context.Response.WriteMultilineDataAsync("AUTHINFO USER", cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // Policy forbids cleartext AUTHINFO and TLS is inactive — do not advertise USER.
                await context.Response.WriteMultilineDataAsync("AUTHINFO", cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (!context.Connection.IsTls)
        {
            await context.Response.WriteMultilineDataAsync("STARTTLS", cancellationToken).ConfigureAwait(false);
        }

        await context.Response.WriteMultilineEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask HandleModeReaderAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // RFC 4643: client MUST NOT issue MODE READER after authentication; reject consistently.
        if (context.Session.Authentication.IsAuthenticated)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Command unavailable after authentication", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        context.Session.SetMode(NntpSessionMode.Reader);
        if (context.Session.Authorization.PostingPermitted)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.PostingAllowed, "Reader mode, posting permitted", cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.PostingProhibited, "Reader mode, posting prohibited", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static ValueTask HandleModeStreamNotImplementedAsync(
        NntpCommandContext context,
        CancellationToken cancellationToken) =>
        context.Response.WriteLineAsync(NntpReplyCodes.SyntaxError, "MODE STREAM not implemented", cancellationToken);

    private static async ValueTask HandleHelpAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        await context.Response
            .WriteMultilineStartAsync(NntpReplyCodes.HelpTextFollows, "Help text follows", cancellationToken)
            .ConfigureAwait(false);
        await context.Response.WriteMultilineDataAsync("VectorNNTP.NNTPD session foundation", cancellationToken)
            .ConfigureAwait(false);
        await context.Response
            .WriteMultilineDataAsync(
                "Public: CAPABILITIES MODE READER HELP DATE QUIT STARTTLS AUTHINFO USER PASS",
                cancellationToken)
            .ConfigureAwait(false);
        await context.Response.WriteMultilineEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask HandleDateAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return context.Response.WriteLineAsync(NntpReplyCodes.ServerDate, stamp, cancellationToken);
    }

    private static async ValueTask HandleQuitAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        await context.Response
            .WriteLineAsync(NntpReplyCodes.ConnectionClosing, "Connection closing", cancellationToken)
            .ConfigureAwait(false);
        context.Session.RequestClose();
    }

    private static async ValueTask HandleStartTlsAsync(
        NntpCommandContext context,
        ITlsCertificateContextProvider? certificateProvider,
        CancellationToken cancellationToken)
    {
        if (context.Connection.IsTls)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "TLS already active", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (certificateProvider is null)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "TLS provider unavailable", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await context.Response
            .WriteLineAsync(NntpReplyCodes.ContinueWithTls, "Continue with TLS negotiation", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await context.Connection.UpgradeToTlsAsync(certificateProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            context.Session.RequestClose();
            throw;
        }
    }

    private static async ValueTask HandleAuthinfoUserAsync(
        NntpCommandContext context,
        INntpAuthenticationProvider authenticationProvider,
        CancellationToken cancellationToken)
    {
        if (context.Session.Authentication.IsAuthenticated)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Already authenticated", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!context.Session.IsAuthinfoPassPermitted)
        {
            await context.Response
                .WriteLineAsync(
                    NntpReplyCodes.PrivacyRequired,
                    "Encryption or stronger authentication required",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!AuthinfoArgument.TryGet(context.RawLine, "USER", out var username))
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.SyntaxError, "AUTHINFO USER requires a username", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Cache only — does not authenticate or grant authorization (RFC 4643).
        context.Session.SetPendingAuthUsername(username);
        await context.Response
            .WriteLineAsync(NntpReplyCodes.PasswordRequired, "Password required", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask HandleAuthinfoPassAsync(
        NntpCommandContext context,
        INntpAuthenticationProvider authenticationProvider,
        CancellationToken cancellationToken)
    {
        if (context.Session.Authentication.IsAuthenticated)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Already authenticated", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!context.Session.IsAuthinfoPassPermitted)
        {
            await context.Response
                .WriteLineAsync(
                    NntpReplyCodes.PrivacyRequired,
                    "Encryption or stronger authentication required",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var pending = context.Session.PendingAuthUsername;
        if (pending is null)
        {
            await context.Response
                .WriteLineAsync(
                    NntpReplyCodes.AuthenticationOutOfSequence,
                    "Authentication commands issued out of sequence",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!AuthinfoArgument.TryGet(context.RawLine, "PASS", out var password))
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.SyntaxError, "AUTHINFO PASS requires a password", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        NntpAuthenticationResult result;
        try
        {
            result = await authenticationProvider
                .AuthenticateAsync(pending, password, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Drop local reference; do not retain plaintext password on the session.
            password = null!;
        }

        if (!result.Succeeded || result.Username is null || result.Authorization is null)
        {
            context.Session.ApplyFailedAuthentication();
            await context.Response
                .WriteLineAsync(NntpReplyCodes.AuthenticationRejected, "Authentication failed", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        context.Session.ApplySuccessfulAuthentication(result.Username, result.Authorization);
        await context.Response
            .WriteLineAsync(NntpReplyCodes.AuthenticationAccepted, "Authentication accepted", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask HandleAuthinfoSaslNotImplementedAsync(
        NntpCommandContext context,
        CancellationToken cancellationToken)
    {
        if (context.Session.Authentication.IsAuthenticated)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Already authenticated", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await context.Response
            .WriteLineAsync(NntpReplyCodes.SyntaxError, "AUTHINFO SASL not implemented", cancellationToken)
            .ConfigureAwait(false);
    }

    private static ValueTask HandleNotImplementedAsync(
        NntpCommandContext context,
        CancellationToken cancellationToken) =>
        context.Response.WriteLineAsync(NntpReplyCodes.UnknownCommand, "Command not implemented", cancellationToken);
}
