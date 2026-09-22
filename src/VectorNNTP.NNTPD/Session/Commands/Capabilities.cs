using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Session;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// CAPABILITIES command as defined by RFC 3977, Section 5.2.
/// </summary>
/// <remarks>
/// Returns the server's capability list (VERSION, READER, AUTHINFO, STARTTLS, COMPRESS, and related labels).
/// Advertisement of AUTHINFO and MODE-READER follows RFC 4643 rules after authentication.
/// COMPRESS / STARTTLS / MODE-READER / AUTHINFO arguments follow RFC 8054 once a compression layer is active.
/// </remarks>
internal static class Capabilities
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Capabilities));

    /// <summary>Handles <c>CAPABILITIES</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "CAPABILITIES", ExecuteAsync, cancellationToken);

    private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        await context.Response
            .WriteMultilineStartAsync(NntpReplyCodes.CapabilityListFollows, "Capability list:", cancellationToken)
            .ConfigureAwait(false);
        await context.Response.WriteMultilineDataAsync("VERSION 2", cancellationToken).ConfigureAwait(false);
        await context.Response.WriteMultilineDataAsync("IMPLEMENTATION VectorNNTP.NNTPD", cancellationToken)
            .ConfigureAwait(false);

        var authenticated = context.Session.Authentication.IsAuthenticated;
        var compressed = context.Connection.IsCompressed;

        if (context.Session.Mode is NntpSessionMode.Unspecified or NntpSessionMode.Reader)
        {
            await context.Response.WriteMultilineDataAsync("READER", cancellationToken).ConfigureAwait(false);
            // RFC 4643: MUST NOT advertise MODE-READER after authentication.
            // RFC 8054 §2.2.2: MUST NOT advertise MODE-READER once a compression layer is active.
            if (!authenticated && !compressed)
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
        // RFC 8054 §2.2.2 / §7: after COMPRESS, advertise AUTHINFO with no arguments (or omit).
        if (!authenticated)
        {
            if (compressed)
            {
                await context.Response.WriteMultilineDataAsync("AUTHINFO", cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (context.Session.IsAuthinfoPassPermitted)
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

        // RFC 8054 §2.2.2: MUST NOT advertise STARTTLS once a compression layer is active.
        if (!context.Connection.IsTls && !compressed)
        {
            await context.Response.WriteMultilineDataAsync("STARTTLS", cancellationToken).ConfigureAwait(false);
        }

        // RFC 8054 §2.1: advertise COMPRESS DEFLATE when available; MUST NOT once compression is active.
        if (!compressed)
        {
            await context.Response.WriteMultilineDataAsync("COMPRESS DEFLATE", cancellationToken)
                .ConfigureAwait(false);
        }

        await context.Response.WriteMultilineEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
