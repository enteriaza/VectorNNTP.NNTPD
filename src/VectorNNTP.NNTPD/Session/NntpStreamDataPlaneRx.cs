using VectorNNTP.NNTPD.Session.Commands;
using VectorNNTP.NNTPD.Session.Framing;

namespace VectorNNTP.NNTPD.Session;

/// <summary>
/// Default / transit STREAM RX: continuous command/article units from the connection input pipe.
/// </summary>
/// <remarks>
/// TAKETHIS article bytes are pre-read only when <see cref="NntpAuthorization.AuthorizedTransit"/>
/// is already true so 480/502 gates do not consume a following QUIT. Authorization still runs in
/// the dispatcher before the handler uses <c>PreReadArticle</c>.
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
        var consumeTakeThisArticle = _session.Authorization.AuthorizedTransit;
        var unit = await NntpContinuousRxReader
            .ReadUnitAsync(
                _session.Connection.Input,
                _parser,
                consumeTakeThisArticle,
                _session.ArticleIngestion.MaxArticleBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (unit.Kind == NntpContinuousRxKind.NeedMore)
        {
            return false;
        }

        if (unit is { Kind: NntpContinuousRxKind.TakeThis, Article.Status: NntpMultilineReadStatus.Incomplete })
        {
            return false;
        }

        if (unit.Kind == NntpContinuousRxKind.NeedMore)
        {
            return false;
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
