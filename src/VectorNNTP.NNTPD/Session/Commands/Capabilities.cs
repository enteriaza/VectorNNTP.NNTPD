using VectorNNTP.NNTPD.Session;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>CAPABILITIES command (RFC 3977 / extension RFCs).</summary>
internal static class Capabilities
{
    /// <summary>Handles <c>CAPABILITIES</c>.</summary>
    public static async ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
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

        // COMPRESS is not advertised until COMPRESS DEFLATE is implemented (transport DEFLATE exists).
        await context.Response.WriteMultilineEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
