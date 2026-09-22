using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Session;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// Applies session/authorization gates then invokes the registered handler.
/// </summary>
/// <remarks>
/// Validation order: parse (caller) → resolve → authentication → authorization → mode → handler.
/// Command RX/TX completion records are owned by the session (RX) and command modules (TX), not this type.
/// </remarks>
public sealed class NntpCommandDispatcher
{
    private readonly NntpCommandRegistry _registry;
    private readonly ILogger _unknownCommandLogger;

    /// <summary>Initializes a new instance of the <see cref="NntpCommandDispatcher"/> class.</summary>
    public NntpCommandDispatcher(NntpCommandRegistry registry, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        NntpCommandLoggers.Configure(loggerFactory);
        // Unknown/unresolved commands have no module; use the shared execution helper category.
        _unknownCommandLogger = NntpCommandLoggers.For(typeof(NntpCommandExecution));
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

        if (!_registry.TryResolve(parsed, out var descriptor, out var arguments, out var status))
        {
            var started = Stopwatch.GetTimestamp();
            var display = status == NntpCommandResolveStatus.UnknownSubcommand && parsed.Tokens.Count > 0
                ? $"{parsed.Verb} {parsed.Tokens[0]}"
                : parsed.Verb;
            switch (status)
            {
                case NntpCommandResolveStatus.UnknownSubcommand:
                    await response
                        .WriteLineAsync(NntpReplyCodes.SyntaxError, "Unknown command variant", cancellationToken)
                        .ConfigureAwait(false);
                    NntpCommandExecution.WriteCompletion(
                        _unknownCommandLogger,
                        session,
                        display.ToUpperInvariant(),
                        Stopwatch.GetElapsedTime(started),
                        "unknown variant");
                    return;
                default:
                    await response
                        .WriteLineAsync(NntpReplyCodes.UnknownCommand, "Unknown command", cancellationToken)
                        .ConfigureAwait(false);
                    NntpCommandExecution.WriteCompletion(
                        _unknownCommandLogger,
                        session,
                        display.ToUpperInvariant(),
                        Stopwatch.GetElapsedTime(started),
                        "unknown command");
                    return;
            }
        }

        var access = descriptor.Access;
        var authz = session.Authorization;
        var gateStarted = Stopwatch.GetTimestamp();

        if (access.HasFlag(NntpCommandAccess.RequiresAuthentication) && !authz.IsAuthenticated)
        {
            await response
                .WriteLineAsync(NntpReplyCodes.AuthenticationRequired, "Authentication required", cancellationToken)
                .ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                descriptor.Logger,
                session,
                descriptor.RegistryKey,
                Stopwatch.GetElapsedTime(gateStarted),
                "authentication required");
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresReader) && !authz.AuthorizedReader)
        {
            var code = authz.IsAuthenticated ? NntpReplyCodes.CommandUnavailable : NntpReplyCodes.AuthenticationRequired;
            var text = authz.IsAuthenticated ? "Permission denied" : "Authentication required";
            await response.WriteLineAsync(code, text, cancellationToken).ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                descriptor.Logger,
                session,
                descriptor.RegistryKey,
                Stopwatch.GetElapsedTime(gateStarted),
                authz.IsAuthenticated ? "permission denied" : "authentication required");
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresTransit) && !authz.AuthorizedTransit)
        {
            var code = authz.IsAuthenticated ? NntpReplyCodes.CommandUnavailable : NntpReplyCodes.AuthenticationRequired;
            var text = authz.IsAuthenticated ? "Permission denied" : "Authentication required";
            await response.WriteLineAsync(code, text, cancellationToken).ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                descriptor.Logger,
                session,
                descriptor.RegistryKey,
                Stopwatch.GetElapsedTime(gateStarted),
                authz.IsAuthenticated ? "permission denied" : "authentication required");
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresPosting) && !authz.PostingPermitted)
        {
            await response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Posting not permitted", cancellationToken)
                .ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                descriptor.Logger,
                session,
                descriptor.RegistryKey,
                Stopwatch.GetElapsedTime(gateStarted),
                "posting not permitted");
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresStreaming) && !authz.StreamingPermitted)
        {
            await response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Streaming not permitted", cancellationToken)
                .ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                descriptor.Logger,
                session,
                descriptor.RegistryKey,
                Stopwatch.GetElapsedTime(gateStarted),
                "streaming not permitted");
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresReaderMode) && session.Mode != NntpSessionMode.Reader)
        {
            await response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Not in reader mode", cancellationToken)
                .ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                descriptor.Logger,
                session,
                descriptor.RegistryKey,
                Stopwatch.GetElapsedTime(gateStarted),
                "not in reader mode");
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresStreamMode) && session.Mode != NntpSessionMode.Stream)
        {
            await response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Not in stream mode", cancellationToken)
                .ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                descriptor.Logger,
                session,
                descriptor.RegistryKey,
                Stopwatch.GetElapsedTime(gateStarted),
                "not in stream mode");
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
        catch (Exception)
        {
            // Command modules own failure TX/exception logs. Transport-terminal failures (e.g. STARTTLS)
            // complete the connection before reaching here — do not emit a secondary NNTP status line.
            if (session.Connection.IsCompleted || session.Connection.ConnectionClosed.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await response
                    .WriteLineAsync(NntpReplyCodes.CommandFailed, "Command failed", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Output writer completed between the check and the write.
            }
        }
    }
}
