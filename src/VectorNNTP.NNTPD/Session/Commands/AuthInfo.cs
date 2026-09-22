using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Authentication;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>AUTHINFO USER / PASS / SASL (RFC 4643).</summary>
/// <remarks>
/// USER and PASS are implemented. SASL is registered but not implemented.
/// </remarks>
internal static class AuthInfo
{
    /// <summary>Handles <c>AUTHINFO USER</c>.</summary>
    public static async ValueTask HandleUserAsync(
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

    /// <summary>Handles <c>AUTHINFO PASS</c>.</summary>
    public static async ValueTask HandlePassAsync(
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

    /// <summary>
    /// Handles <c>AUTHINFO SASL</c>.
    /// </summary>
    /// <remarks>TODO: Implement RFC-compliant AUTHINFO SASL behavior.</remarks>
    public static async ValueTask HandleSaslAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant AUTHINFO SASL behavior.
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
}
