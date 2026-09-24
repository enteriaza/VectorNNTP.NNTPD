namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// CAPABILITIES command as defined by RFC 3977, Section 5.2.
/// </summary>
/// <remarks>
/// Returns the server's capability list (VERSION, READER, AUTHINFO, STARTTLS, COMPRESS, STREAMING, and related labels).
/// Advertisement for AUTHINFO and MODE-READER follows RFC 4643 rules after authentication.
/// COMPRESS / STARTTLS / MODE-READER / AUTHINFO arguments follow RFC 8054 once a compression layer is active.
/// STREAMING (RFC 4644) is advertised when TAKETHIS/CHECK streaming transfer is implemented.
/// Individual lines are pre-encoded and immortal. Which lines appear depends on session state,
/// so the complete response is composed into one owned buffer and written once.
/// </remarks>
internal static class Capabilities
{
    private const int MaxCapabilityParts = 11;

    private static ILogger Logger => NntpCommandLoggers.For(typeof(Capabilities));

    /// <summary>Handles <c>CAPABILITIES</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "CAPABILITIES", ExecuteAsync, cancellationToken);

    private static ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        var parts = new ReadOnlyMemory<byte>[MaxCapabilityParts];
        var count = CollectLines(context, parts);
        var owned = NntpResponseCompose.Concatenate(parts.AsSpan(0, count));
        return context.Response.WriteLineAsync(owned, cancellationToken);
    }

    private static int CollectLines(NntpCommandContext context, Span<ReadOnlyMemory<byte>> parts)
    {
        var n = 0;
        parts[n++] = NntpResponses.CapabilityListFollows;
        parts[n++] = NntpResponses.CapabilityVersion2;
        parts[n++] = NntpResponses.CapabilityImplementation;

        var authenticated = context.Session.Authentication.IsAuthenticated;
        var compressed = context.Connection.IsCompressed;

        if (context.Session.Mode is NntpSessionMode.Unspecified or NntpSessionMode.Reader)
        {
            parts[n++] = NntpResponses.CapabilityReader;
            // RFC 4643: MUST NOT advertise MODE-READER after authentication.
            // RFC 8054 §2.2.2: MUST NOT advertise MODE-READER once a compression layer is active.
            if (!authenticated && !compressed)
            {
                parts[n++] = NntpResponses.CapabilityModeReader;
            }
        }

        if (context.Session.Authorization.PostingPermitted &&
            context.Session.Mode is NntpSessionMode.Unspecified or NntpSessionMode.Reader)
        {
            parts[n++] = NntpResponses.CapabilityPost;
        }

        // RFC 4643: MUST NOT return AUTHINFO after successful authentication.
        // RFC 8054 §2.2.2 / §7: after COMPRESS, advertise AUTHINFO with no arguments (or omit).
        if (!authenticated)
        {
            if (compressed)
            {
                parts[n++] = NntpResponses.CapabilityAuthinfo;
            }
            else if (context.Session.IsAuthinfoPassPermitted)
            {
                parts[n++] = NntpResponses.CapabilityAuthinfoUser;
            }
            else
            {
                // Policy forbids cleartext AUTHINFO and TLS is inactive — do not advertise USER.
                parts[n++] = NntpResponses.CapabilityAuthinfo;
            }
        }

        // RFC 8054 §2.2.2: MUST NOT advertise STARTTLS once a compression layer is active.
        if (!context.Connection.IsTls && !compressed)
        {
            parts[n++] = NntpResponses.CapabilityStartTls;
        }

        // RFC 8054 §2.1: advertise COMPRESS DEFLATE when available; MUST NOT once compression is active.
        if (!compressed)
        {
            parts[n++] = NntpResponses.CapabilityCompressDeflate;
        }

        // RFC 4644 §2.2: STREAMING capability for CHECK/TAKETHIS (MODE STREAM is legacy discovery).
        parts[n++] = NntpResponses.CapabilityStreaming;
        parts[n++] = NntpResponses.MultilineTerminator;
        return n;
    }
}
