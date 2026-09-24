using VectorNNTP.NNTPD.Session.Commands;
using VectorNNTP.NNTPD.Session.Framing;

namespace VectorNNTP.NNTPD.Session;

/// <summary>
/// Default / transit STREAM RX: continuous command/article units from the connection input pipe.
/// </summary>
/// <remarks>
/// Authorized TAKETHIS is admitted into <see cref="TakeThisPipeline"/>: HistoryDB peek starts
/// at the command line, then this RX task frames one stuffed-wire copy with
/// <see cref="IHaveArticleReader"/>, attaches the owned buffer to a slot, and returns so
/// the next command can be parsed. Pipeline workers never read <c>Connection.Input</c>.
/// Unauthorized TAKETHIS is returned as a command line so 480/502 gates do not consume a
/// following QUIT.
/// </remarks>
internal sealed class NntpStreamDataPlaneRx
{
    private readonly NntpSession _session;
    private readonly NntpCommandDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly NntpContinuousRxParser _parser = new();

    /// <summary>Initializes a new instance of the <see cref="NntpStreamDataPlaneRx"/> class.</summary>
    public NntpStreamDataPlaneRx(NntpSession session, NntpCommandDispatcher dispatcher, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        _session = session;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>
    /// Reads and dispatches one protocol unit. Returns <see langword="false"/> on EOF or an
    /// incomplete TAKETHIS article.
    /// </summary>
    public async ValueTask<bool> ProcessOneAsync(NntpResponseWriter response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        await _session.WaitForCheckCapacityAsync(cancellationToken).ConfigureAwait(false);
        await _session.WaitForTakeThisCapacityAsync(cancellationToken).ConfigureAwait(false);

        // Command line only. Authorized TAKETHIS starts HistoryDB peek, frames the
        // article on this RX task, hands the owned buffer to the pipeline, and returns.
        // Unauthorized TAKETHIS stays a command so 480/502 gates do not consume a
        // following QUIT.
        var unit = await NntpContinuousRxReader
            .ReadUnitAsync(
                _session.Connection.Input,
                _parser,
                consumeTakeThisArticle: false,
                _session.ArticleIngestion.MaxArticleBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (unit.Kind == NntpContinuousRxKind.NeedMore)
        {
            return false;
        }

        if (unit.Command.IsValid
            && unit.Command.Verb == NntpVerb.TakeThis
            && _session.Authorization.AuthorizedTransit)
        {
            ArgumentNullException.ThrowIfNull(_session.TakeThisWindow);
            await _session.Pipeline!.DrainAsync(cancellationToken).ConfigureAwait(false);
            await _session.TakeThisWindow
                .AdmitAuthorizedAsync(unit.Command, _parser.CurrentCommandLine, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        var preRead = unit.Kind == NntpContinuousRxKind.TakeThis ? unit.Article : (NntpMultilineReadResult?)null;
        await _session
            .ProcessParsedCommandAsync(
                _dispatcher,
                response,
                _logger,
                unit.Command,
                _parser.CurrentCommandLine,
                preRead,
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
}
