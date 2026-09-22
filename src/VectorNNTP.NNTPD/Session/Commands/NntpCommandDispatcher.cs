using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Session;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// Applies session/authorization gates then invokes the registered handler.
/// </summary>
/// <remarks>
/// Validation order: parse (caller) → resolve → authentication → authorization → mode → handler.
/// </remarks>
public sealed class NntpCommandDispatcher
{
    private readonly NntpCommandRegistry _registry;
    private readonly ILogger<NntpCommandDispatcher> _logger;

    /// <summary>Initializes a new instance of the <see cref="NntpCommandDispatcher"/> class.</summary>
    public NntpCommandDispatcher(NntpCommandRegistry registry, ILogger<NntpCommandDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Dispatches one already-parsed command line.</summary>
    public async ValueTask DispatchAsync(
        NntpSession session,
        NntpParsedCommand parsed,
        NntpResponseWriter response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(response);

        var status = _registry.Resolve(parsed, out var descriptor, out var arguments);
        switch (status)
        {
            case NntpCommandResolveStatus.UnknownCommand:
                await response
                    .WriteLineAsync(NntpReplyCodes.UnknownCommand, "Unknown command", cancellationToken)
                    .ConfigureAwait(false);
                return;
            case NntpCommandResolveStatus.UnknownSubcommand:
                await response
                    .WriteLineAsync(NntpReplyCodes.SyntaxError, "Unknown command variant", cancellationToken)
                    .ConfigureAwait(false);
                return;
            case NntpCommandResolveStatus.Found when descriptor is not null:
                break;
            default:
                await response
                    .WriteLineAsync(NntpReplyCodes.UnknownCommand, "Unknown command", cancellationToken)
                    .ConfigureAwait(false);
                return;
        }

        var access = descriptor.Access;
        var authz = session.Authorization;

        if (access.HasFlag(NntpCommandAccess.RequiresAuthentication) && !authz.IsAuthenticated)
        {
            await response
                .WriteLineAsync(NntpReplyCodes.AuthenticationRequired, "Authentication required", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresReader) && !authz.AuthorizedReader)
        {
            var code = authz.IsAuthenticated ? NntpReplyCodes.CommandUnavailable : NntpReplyCodes.AuthenticationRequired;
            var text = authz.IsAuthenticated ? "Permission denied" : "Authentication required";
            await response.WriteLineAsync(code, text, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresTransit) && !authz.AuthorizedTransit)
        {
            var code = authz.IsAuthenticated ? NntpReplyCodes.CommandUnavailable : NntpReplyCodes.AuthenticationRequired;
            var text = authz.IsAuthenticated ? "Permission denied" : "Authentication required";
            await response.WriteLineAsync(code, text, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresPosting) && !authz.PostingPermitted)
        {
            await response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Posting not permitted", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresStreaming) && !authz.StreamingPermitted)
        {
            await response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Streaming not permitted", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresReaderMode) && session.Mode != NntpSessionMode.Reader)
        {
            await response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Not in reader mode", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresStreamMode) && session.Mode != NntpSessionMode.Stream)
        {
            await response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Not in stream mode", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var context = new NntpCommandContext(session, descriptor, parsed.RawLine, arguments, response);
        try
        {
            await descriptor.Handler(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Log registry key only — never the raw command line (may contain AUTHINFO PASS secrets).
            _logger.LogError(ex, "NNTP command {Command} failed for {Client}.", descriptor.RegistryKey, session.ClientAddress);
            await response
                .WriteLineAsync(NntpReplyCodes.CommandFailed, "Command failed", cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
