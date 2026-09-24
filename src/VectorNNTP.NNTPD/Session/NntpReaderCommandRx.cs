using VectorNNTP.NNTPD.Session.Commands;

namespace VectorNNTP.NNTPD.Session;

/// <summary>
/// MODE READER RX: one command line at a time via <see cref="NntpCommandLineReader"/>.
/// </summary>
/// <remarks>
/// Does not use the continuous TAKETHIS scanner. TAKETHIS on this path is dispatched as a
/// normal command; the handler reads the article after authorization gates.
/// </remarks>
internal sealed class NntpReaderCommandRx
{
    private readonly NntpSession _session;
    private readonly NntpCommandDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly byte[] _commandScratch = new byte[2048];

    /// <summary>Initializes a new instance of the <see cref="NntpReaderCommandRx"/> class.</summary>
    public NntpReaderCommandRx(NntpSession session, NntpCommandDispatcher dispatcher, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        _session = session;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>
    /// Reads and dispatches one command. Returns <see langword="false"/> on EOF.
    /// </summary>
    public async ValueTask<bool> ProcessOneAsync(NntpResponseWriter response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var length = await NntpCommandLineReader
            .ReadLineBytesAsync(_session.Connection.Input, _commandScratch, cancellationToken)
            .ConfigureAwait(false);
        if (length < 0)
        {
            return false;
        }

        var line = _commandScratch.AsMemory(0, length);
        var command = NntpCommandParser.Parse(line.Span);
        await _session
            .DispatchCommandAsync(
                _dispatcher,
                response,
                _logger,
                command,
                line,
                preReadArticle: null,
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
}
