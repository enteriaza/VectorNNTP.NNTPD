using VectorNNTP.NNTPD.Session.Authentication;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// AUTHINFO USER / PASS (RFC 4643, Section 2.3) and AUTHINFO SASL (RFC 4643, Section 2.4).
/// </summary>
/// <remarks>
/// USER caches a username; PASS authenticates via <see cref="INntpAuthenticationProvider"/>.
/// SASL is registered but deliberately not implemented yet. Passwords and SASL material must never
/// appear in command logs (RX redaction is applied at the session boundary). After a successful
/// COMPRESS (RFC 8054 §2.2.2 / §7), AUTHINFO commands are rejected with <c>502</c>.
/// </remarks>
internal static class AuthInfo
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(AuthInfo));

    /// <summary>Handles <c>AUTHINFO USER</c>.</summary>
    public static ValueTask HandleUserAsync(
        NntpCommandContext context,
        INntpAuthenticationProvider authenticationProvider,
        CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "AUTHINFO USER",
            (ctx, ct) => ExecuteUserAsync(ctx, authenticationProvider, ct),
            cancellationToken);

    /// <summary>Handles <c>AUTHINFO PASS</c>.</summary>
    public static ValueTask HandlePassAsync(
        NntpCommandContext context,
        INntpAuthenticationProvider authenticationProvider,
        CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "AUTHINFO PASS",
            (ctx, ct) => ExecutePassAsync(ctx, authenticationProvider, ct),
            cancellationToken);

    /// <summary>Handles <c>AUTHINFO SASL</c>.</summary>
    public static ValueTask HandleSaslAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "AUTHINFO SASL",
            ExecuteSaslAsync,
            cancellationToken,
            successDetail: "not implemented");

    private static async ValueTask ExecuteUserAsync(
        NntpCommandContext context,
        INntpAuthenticationProvider authenticationProvider,
        CancellationToken cancellationToken)
    {
        _ = authenticationProvider;
        if (context.Session.Authentication.IsAuthenticated)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Already authenticated", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // RFC 8054 §2.2.2 / §7: authentication MUST NOT be attempted after successful COMPRESS.
        if (context.Connection.IsCompressed)
        {
            await context.Response
                .WriteLineAsync(
                    NntpReplyCodes.CommandUnavailable,
                    "DEFLATE compression already active",
                    cancellationToken)
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

    private static async ValueTask ExecutePassAsync(
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

        // RFC 8054 §2.2.2 / §7: authentication MUST NOT be attempted after successful COMPRESS.
        if (context.Connection.IsCompressed)
        {
            await context.Response
                .WriteLineAsync(
                    NntpReplyCodes.CommandUnavailable,
                    "DEFLATE compression already active",
                    cancellationToken)
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

    private static async ValueTask ExecuteSaslAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        if (context.Session.Authentication.IsAuthenticated)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Already authenticated", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // RFC 8054 §2.2.2 / §7: authentication MUST NOT be attempted after successful COMPRESS.
        if (context.Connection.IsCompressed)
        {
            await context.Response
                .WriteLineAsync(
                    NntpReplyCodes.CommandUnavailable,
                    "DEFLATE compression already active",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await context.Response
            .WriteLineAsync(NntpReplyCodes.SyntaxError, "AUTHINFO SASL not implemented", cancellationToken)
            .ConfigureAwait(false);
    }
}
