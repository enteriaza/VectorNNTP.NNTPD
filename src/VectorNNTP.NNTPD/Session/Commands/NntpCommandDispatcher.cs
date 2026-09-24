using System.Diagnostics;
using VectorNNTP.NNTPD.Session.Framing;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// Rejects invalid syntax, applies session/authorization gates, then switch-dispatches the handler.
/// </summary>
/// <remarks>
/// Validation order: parse (caller) → syntax reject → authentication → authorization → mode → handler.
/// Command RX/TX completion records are owned by the session (RX) and command modules (TX), not this type.
/// </remarks>
public sealed class NntpCommandDispatcher
{
    private readonly ILogger _unknownCommandLogger;

    /// <summary>Initializes a new instance of the <see cref="NntpCommandDispatcher"/> class.</summary>
    public NntpCommandDispatcher(ILoggerFactory? loggerFactory = null)
    {
        NntpCommandLoggers.Configure(loggerFactory);
        _unknownCommandLogger = NntpCommandLoggers.For(typeof(NntpCommandExecution));
    }

    /// <summary>Dispatches one already-parsed command. Invalid syntax never reaches a handler.</summary>
    public async ValueTask DispatchAsync(
        NntpSession session,
        NntpCommand command,
        ReadOnlyMemory<byte> line,
        NntpResponseWriter response,
        CancellationToken cancellationToken,
        NntpMultilineReadResult? preReadArticle = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(response);

        if (!command.IsValid)
        {
            await RejectInvalidAsync(session, command, response, preReadArticle, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var access = DefaultNntpCommandCatalog.GetAccess(command.Verb, command.Qualifier);
        var display = DefaultNntpCommandCatalog.DisplayName(command.Verb, command.Qualifier);
        var logger = LoggerFor(command);
        var authz = session.Authorization;
        var gateStarted = Stopwatch.GetTimestamp();

        if (access.HasFlag(NntpCommandAccess.RequiresAuthentication) && !authz.IsAuthenticated)
        {
            await response
                .WriteLineAsync(NntpResponses.AuthenticationRequired, cancellationToken)
                .ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                logger, session, display, Stopwatch.GetElapsedTime(gateStarted), "authentication required");
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresReader) && !authz.AuthorizedReader)
        {
            var wire = authz.IsAuthenticated
                ? NntpResponses.PermissionDenied
                : NntpResponses.AuthenticationRequired;
            await response.WriteLineAsync(wire, cancellationToken).ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                logger,
                session,
                display,
                Stopwatch.GetElapsedTime(gateStarted),
                authz.IsAuthenticated ? "permission denied" : "authentication required");
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresTransit) && !authz.AuthorizedTransit)
        {
            var wire = authz.IsAuthenticated
                ? NntpResponses.PermissionDenied
                : NntpResponses.AuthenticationRequired;
            await response.WriteLineAsync(wire, cancellationToken).ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                logger,
                session,
                display,
                Stopwatch.GetElapsedTime(gateStarted),
                authz.IsAuthenticated ? "permission denied" : "authentication required");
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresPosting) && !authz.PostingPermitted)
        {
            await response
                .WriteLineAsync(NntpResponses.PostingNotPermitted, cancellationToken)
                .ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                logger, session, display, Stopwatch.GetElapsedTime(gateStarted), "posting not permitted");
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresStreaming) && !authz.StreamingPermitted)
        {
            var wire = authz.IsAuthenticated
                ? NntpResponses.StreamingNotPermitted
                : NntpResponses.AuthenticationRequired;
            await response.WriteLineAsync(wire, cancellationToken).ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                logger,
                session,
                display,
                Stopwatch.GetElapsedTime(gateStarted),
                authz.IsAuthenticated ? "streaming not permitted" : "authentication required");
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresReaderMode) && session.Mode != NntpSessionMode.Reader)
        {
            await response
                .WriteLineAsync(NntpResponses.NotInReaderMode, cancellationToken)
                .ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                logger, session, display, Stopwatch.GetElapsedTime(gateStarted), "not in reader mode");
            return;
        }

        if (access.HasFlag(NntpCommandAccess.RequiresStreamMode) && session.Mode != NntpSessionMode.Stream)
        {
            await response
                .WriteLineAsync(NntpResponses.NotInStreamMode, cancellationToken)
                .ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                logger, session, display, Stopwatch.GetElapsedTime(gateStarted), "not in stream mode");
            return;
        }

        var context = new NntpCommandContext(session, command, line, response, preReadArticle);
        try
        {
            await InvokeHandlerAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            if (session.Connection.IsCompleted || session.Connection.ConnectionClosed.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await response
                    .WriteLineAsync(NntpResponses.CommandFailed, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Output writer completed between the check and the write.
            }
        }
    }

    private async ValueTask RejectInvalidAsync(
        NntpSession session,
        NntpCommand command,
        NntpResponseWriter response,
        NntpMultilineReadResult? preReadArticle,
        CancellationToken cancellationToken)
    {
        if (command.Verb == NntpVerb.TakeThis && preReadArticle is null)
        {
            try
            {
                _ = await NntpMultilineDataReader
                    .ReadArticleAsync(session.Connection.Input, session.ArticleIngestion.MaxArticleBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested
                || session.Connection.ConnectionClosed.IsCancellationRequested)
            {
                return;
            }
        }

        var started = Stopwatch.GetTimestamp();
        var display = command.Status is NntpParseStatus.Empty or NntpParseStatus.UnknownVerb
            ? "INVALID"
            : DefaultNntpCommandCatalog.DisplayName(command.Verb, command.Qualifier);
        var (wire, detail) = Rejection(command);
        await response.WriteLineAsync(wire, cancellationToken).ConfigureAwait(false);
        session.LogCommandRejected(command, detail);
        NntpCommandExecution.WriteCompletion(
            _unknownCommandLogger,
            session,
            display,
            Stopwatch.GetElapsedTime(started),
            detail);
    }

    private static (ReadOnlyMemory<byte> Wire, string Detail) Rejection(NntpCommand command)
    {
        return command.Status switch
        {
            NntpParseStatus.UnknownVerb => (NntpResponses.UnknownCommand, "unknown command"),
            NntpParseStatus.UnknownQualifier =>
                (NntpResponses.UnknownCommandVariant, "unknown variant"),
            NntpParseStatus.MissingArgument when command.Verb == NntpVerb.AuthInfo
                && command.Qualifier == NntpVerb.User =>
                (NntpResponses.AuthinfoUserRequiresUsername, "syntax error"),
            NntpParseStatus.MissingArgument when command.Verb == NntpVerb.AuthInfo
                && command.Qualifier == NntpVerb.Pass =>
                (NntpResponses.AuthinfoPassRequiresPassword, "syntax error"),
            NntpParseStatus.MissingArgument when command.Verb == NntpVerb.Compress =>
                (NntpResponses.CompressRequiresAlgorithm, "syntax error"),
            NntpParseStatus.InvalidArgument when command.Verb == NntpVerb.Compress =>
                (NntpResponses.CompressAlgorithmSyntaxInvalid, "syntax error"),
            NntpParseStatus.ExtraArgument when command.Verb == NntpVerb.Compress =>
                (NntpResponses.CompressRequiresAlgorithm, "syntax error"),
            _ => (NntpResponses.SyntaxError, "syntax error"),
        };
    }

    private static ILogger LoggerFor(NntpCommand command)
    {
        var type = command.Verb switch
        {
            NntpVerb.Check => typeof(Check),
            NntpVerb.TakeThis => typeof(TakeThis),
            NntpVerb.Mode => typeof(Mode),
            NntpVerb.AuthInfo => typeof(AuthInfo),
            NntpVerb.Quit => typeof(Quit),
            NntpVerb.Help => typeof(Help),
            NntpVerb.Date => typeof(Date),
            NntpVerb.Capabilities => typeof(Capabilities),
            NntpVerb.StartTls => typeof(StartTls),
            NntpVerb.Compress => typeof(Compress),
            NntpVerb.List => typeof(List),
            NntpVerb.ListGroup => typeof(ListGroup),
            NntpVerb.Group => typeof(Group),
            NntpVerb.Article => typeof(Article),
            NntpVerb.Head => typeof(Article),
            NntpVerb.Body => typeof(Article),
            NntpVerb.Stat => typeof(Article),
            NntpVerb.Last => typeof(Last),
            NntpVerb.Next => typeof(Next),
            NntpVerb.Over => typeof(Over),
            NntpVerb.Hdr => typeof(Hdr),
            NntpVerb.Post => typeof(Post),
            NntpVerb.Ihave => typeof(IHave),
            NntpVerb.Newgroups => typeof(NewGroups),
            NntpVerb.Newnews => typeof(NewNews),
            NntpVerb.BenchIt => typeof(BenchIt),
            NntpVerb.SpeedTest => typeof(SpeedTest),
            _ => typeof(NntpCommandExecution),
        };

        return NntpCommandLoggers.For(type);
    }

    private static ValueTask InvokeHandlerAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        return (context.Command.Verb, context.Command.Qualifier) switch
        {
            (NntpVerb.Capabilities, _) => Capabilities.HandleAsync(context, cancellationToken),
            (NntpVerb.Mode, NntpVerb.Reader) => Mode.HandleReaderAsync(context, cancellationToken),
            (NntpVerb.Mode, NntpVerb.Stream) => Mode.HandleStreamAsync(context, cancellationToken),
            (NntpVerb.Help, _) => Help.HandleAsync(context, cancellationToken),
            (NntpVerb.Date, _) => Date.HandleAsync(context, cancellationToken),
            (NntpVerb.Quit, _) => Quit.HandleAsync(context, cancellationToken),
            (NntpVerb.StartTls, _) => StartTls.HandleAsync(context, context.Session.CertificateProvider, cancellationToken),
            (NntpVerb.Compress, _) => Compress.HandleAsync(context, cancellationToken),
            (NntpVerb.BenchIt, _) => BenchIt.HandleAsync(context, cancellationToken),
            (NntpVerb.SpeedTest, _) => SpeedTest.HandleAsync(context, cancellationToken),
            (NntpVerb.AuthInfo, NntpVerb.User) => AuthInfo.HandleUserAsync(
                context, context.Session.AuthenticationProvider, cancellationToken),
            (NntpVerb.AuthInfo, NntpVerb.Pass) => AuthInfo.HandlePassAsync(
                context, context.Session.AuthenticationProvider, cancellationToken),
            (NntpVerb.AuthInfo, NntpVerb.Sasl) => AuthInfo.HandleSaslAsync(context, cancellationToken),
            (NntpVerb.List, _) => List.HandleAsync(context, cancellationToken),
            (NntpVerb.Group, _) => Group.HandleAsync(context, cancellationToken),
            (NntpVerb.ListGroup, _) => ListGroup.HandleAsync(context, cancellationToken),
            (NntpVerb.Newgroups, _) => NewGroups.HandleAsync(context, cancellationToken),
            (NntpVerb.Newnews, _) => NewNews.HandleAsync(context, cancellationToken),
            (NntpVerb.Article, _) => Article.HandleArticleAsync(context, cancellationToken),
            (NntpVerb.Head, _) => Article.HandleHeadAsync(context, cancellationToken),
            (NntpVerb.Body, _) => Article.HandleBodyAsync(context, cancellationToken),
            (NntpVerb.Stat, _) => Article.HandleStatAsync(context, cancellationToken),
            (NntpVerb.Last, _) => Last.HandleAsync(context, cancellationToken),
            (NntpVerb.Next, _) => Next.HandleAsync(context, cancellationToken),
            (NntpVerb.Over, _) => Over.HandleAsync(context, cancellationToken),
            (NntpVerb.Hdr, _) => Hdr.HandleAsync(context, cancellationToken),
            (NntpVerb.Post, _) => Post.HandleAsync(context, cancellationToken),
            (NntpVerb.Ihave, _) => IHave.HandleAsync(context, cancellationToken),
            (NntpVerb.Check, _) => Check.HandleAsync(context, cancellationToken),
            (NntpVerb.TakeThis, _) => TakeThis.HandleAsync(context, cancellationToken),
            _ => NntpCommandNotImplemented.HandleAsync(context, cancellationToken),
        };
    }
}
