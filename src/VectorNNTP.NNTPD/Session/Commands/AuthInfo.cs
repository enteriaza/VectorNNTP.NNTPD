using VectorNNTP.NNTPD.Session.Authentication;
using VectorNNTP.NNTPD.Session.CommandProcessor;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// AUTHINFO USER / PASS (RFC 4643, Section 2.3) and AUTHINFO SASL (RFC 4643, Section 2.4).
/// </summary>
/// <remarks>
/// USER caches a username; PASS authenticates via <see cref="INntpAuthenticationProvider"/>
/// unless the session is already an identified Transit peer, in which case only that peer's
/// Username and Password are compared (no global peer search). AUTHINFO is publicly callable
/// and never creates Transit identity. SASL is registered but deliberately not implemented yet.
/// Passwords and SASL material must never appear in command logs (RX redaction is applied at
/// the session boundary). After a successful COMPRESS (RFC 8054 §2.2.2 / §7), AUTHINFO
/// commands are rejected with <c>502</c>.
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
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.AlreadyAuthenticated,
                    NntpResponseStatus.AlreadyAuthenticated,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // RFC 8054 §2.2.2 / §7: authentication MUST NOT be attempted after successful COMPRESS.
        if (context.Connection.IsCompressed)
        {
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.DeflateAlreadyActive,
                    NntpResponseStatus.DeflateAlreadyActive,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!context.Session.IsAuthinfoPassPermitted)
        {
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.PrivacyRequired,
                    NntpResponseStatus.PrivacyRequired,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Username string is the session-state ownership boundary.
        var username = System.Text.Encoding.ASCII.GetString(context.ArgumentSpan);

        // Cache only — does not authenticate or grant authorization (RFC 4643).
        context.Session.SetPendingAuthUsername(username);
        await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.PasswordRequired,
                    NntpResponseStatus.PasswordRequired,
                    cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask ExecutePassAsync(
        NntpCommandContext context,
        INntpAuthenticationProvider authenticationProvider,
        CancellationToken cancellationToken)
    {
        if (context.Session.Authentication.IsAuthenticated)
        {
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.AlreadyAuthenticated,
                    NntpResponseStatus.AlreadyAuthenticated,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // RFC 8054 §2.2.2 / §7: authentication MUST NOT be attempted after successful COMPRESS.
        if (context.Connection.IsCompressed)
        {
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.DeflateAlreadyActive,
                    NntpResponseStatus.DeflateAlreadyActive,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!context.Session.IsAuthinfoPassPermitted)
        {
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.PrivacyRequired,
                    NntpResponseStatus.PrivacyRequired,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var pending = context.Session.PendingAuthUsername;
        if (pending is null)
        {
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.AuthenticationOutOfSequence,
                    NntpResponseStatus.AuthenticationOutOfSequence,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Password string is the authentication-API ownership boundary.
        var password = System.Text.Encoding.ASCII.GetString(context.ArgumentSpan);

        NntpAuthenticationResult result;
        try
        {
            var peerPolicy = context.Session.Authorization.TransitPeerPolicy;
            if (peerPolicy is not null)
            {
                // Identified Transit peer: only that peer's Username+Password may authenticate.
                // Incomplete or mismatched credentials never fall through to the ordinary provider.
                result = peerPolicy.CredentialsMatch(pending, password)
                    ? NntpAuthenticationResult.Success(pending, context.Session.Authorization)
                    : NntpAuthenticationResult.Failed;
            }
            else
            {
                result = await authenticationProvider
                    .AuthenticateAsync(pending, password, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Drop local reference; do not retain plaintext password on the session.
            password = null!;
        }

        if (!result.Succeeded || result.Username is null || result.Authorization is null)
        {
            context.Session.ApplyFailedAuthentication();
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.AuthenticationFailed,
                    NntpResponseStatus.AuthenticationFailed,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        context.Session.ApplySuccessfulAuthentication(result.Username, result.Authorization);
        await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.AuthenticationAccepted,
                    NntpResponseStatus.AuthenticationAccepted,
                    cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask ExecuteSaslAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        if (context.Session.Authentication.IsAuthenticated)
        {
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.AlreadyAuthenticated,
                    NntpResponseStatus.AlreadyAuthenticated,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // RFC 8054 §2.2.2 / §7: authentication MUST NOT be attempted after successful COMPRESS.
        if (context.Connection.IsCompressed)
        {
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.DeflateAlreadyActive,
                    NntpResponseStatus.DeflateAlreadyActive,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.AuthinfoSaslNotImplemented,
                    NntpResponseStatus.AuthinfoSaslNotImplemented,
                    cancellationToken)
            .ConfigureAwait(false);
    }
}
