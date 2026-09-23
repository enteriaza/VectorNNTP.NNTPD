using System.Net;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session.Authentication;
using VectorNNTP.NNTPD.Session.Commands;
using VectorNNTP.NNTPD.Session.Framing;

namespace VectorNNTP.NNTPD.Session;

/// <summary>
/// NNTP session boundary: greeting, command loop, mode, authentication, and authorization state over an
/// <see cref="INntpConnection"/>.
/// </summary>
/// <remarks>
/// Transport owns sockets, TLS, DEFLATE, PROXY identity, and connection lifecycle.
/// This type owns NNTP protocol sequencing and does not touch sockets or stream wrappers directly.
/// Authentication and authorization are distinct; AUTHINFO success applies only provider-returned privileges.
/// Connection-time <see cref="ITransitPeerAuthorization"/> may grant transit/streaming peer privileges
/// without authentication.
/// </remarks>
public sealed class NntpSession
{
    private readonly ILogger<NntpSession> _logger;
    private readonly NntpCommandDispatcher _dispatcher;
    private readonly NntpAuthorization _connectionAuthorization;
    private NntpResponseWriter? _response;
    private NntpAuthorization _authorization;
    private NntpAuthenticationState _authentication;
    private string? _pendingAuthUsername;
    private NntpSessionMode _mode;
    private int _closeRequested;

    /// <summary>Initializes a new instance of the <see cref="NntpSession"/> class.</summary>
    public NntpSession(
        INntpConnection connection,
        ILogger<NntpSession> logger,
        NntpCommandRegistry? registry = null,
        ITlsCertificateContextProvider? certificateProvider = null,
        INntpAuthenticationProvider? authenticationProvider = null,
        bool allowCleartextAuth = true,
        ILoggerFactory? loggerFactory = null,
        IArticleIngestionQueue? articleIngestion = null,
        ITransitPeerAuthorization? transitPeerAuthorization = null,
        int streamOutstandingArticleDepth = NntpStreamArticleTxScheduler.DefaultDepth)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);
        Connection = connection;
        ClientIdentity = connection.ClientIdentity;
        _logger = logger;
        _connectionAuthorization = (transitPeerAuthorization ?? TransitPeerAuthorization.Disabled)
            .Resolve(ClientIdentity.ClientAddress);
        _authorization = _connectionAuthorization;
        _authentication = NntpAuthenticationState.Unauthenticated;
        _mode = NntpSessionMode.Unspecified;
        AllowCleartextAuth = allowCleartextAuth;
        AuthenticationProvider = authenticationProvider ?? DenyAllNntpAuthenticationProvider.Instance;
        ArticleIngestion = articleIngestion ?? DisabledArticleIngestionQueue.Instance;
        StreamArticleTx = new NntpStreamArticleTxScheduler(streamOutstandingArticleDepth);
        registry ??= DefaultNntpCommandCatalog.Create(
            certificateProvider,
            AuthenticationProvider,
            loggerFactory);
        _dispatcher = new NntpCommandDispatcher(registry, loggerFactory);
    }

    /// <summary>Gets the underlying transport connection.</summary>
    public INntpConnection Connection { get; }

    /// <summary>Gets the immutable client identity established at connection start.</summary>
    public ConnectionClientIdentity ClientIdentity { get; }

    /// <summary>
    /// Gets the article ingestion queue used by transfer commands (<c>TAKETHIS</c>, later <c>POST</c>).
    /// </summary>
    public IArticleIngestionQueue ArticleIngestion { get; }

    /// <summary>
    /// Gets the bounded STREAM article TX scheduler (depth gate above
    /// <see cref="NntpResponseWriter.WriteArticleAsync(System.ReadOnlyMemory{byte}, NntpArticleTxFraming, System.Threading.CancellationToken)"/>).
    /// </summary>
    /// <remarks>
    /// Available for outbound STREAM article producers that already hold destuffed bytes.
    /// No production command currently supplies those bytes (storage/catalogue is out of scope).
    /// </remarks>
    public NntpStreamArticleTxScheduler StreamArticleTx { get; }

    /// <summary>Gets the effective client IP address for this session.</summary>
    public IPAddress ClientAddress => ClientIdentity.ClientAddress;

    /// <summary>Gets the effective client TCP source port for this session.</summary>
    public int ClientPort => ClientIdentity.ClientPort;

    /// <summary>Gets the actual TCP peer endpoint of the accepted socket.</summary>
    public IPEndPoint TcpPeer => ClientIdentity.TcpPeer;

    /// <summary>Gets the authentication provider used by AUTHINFO handlers.</summary>
    public INntpAuthenticationProvider AuthenticationProvider { get; }

    /// <summary>
    /// Gets a value indicating whether AUTHINFO USER/PASS is permitted without TLS
    /// (<c>Nntpd:AllowCleartextAuth</c>). TLS connections always permit AUTHINFO USER/PASS.
    /// </summary>
    public bool AllowCleartextAuth { get; }

    /// <summary>
    /// Gets a value indicating whether AUTHINFO USER/PASS may proceed on this connection
    /// given TLS state and <see cref="AllowCleartextAuth"/>.
    /// </summary>
    public bool IsAuthinfoPassPermitted => Connection.IsTls || AllowCleartextAuth;

    /// <summary>Gets the current authentication identity snapshot.</summary>
    public NntpAuthenticationState Authentication => _authentication;

    /// <summary>Gets the current authorization snapshot.</summary>
    public NntpAuthorization Authorization => _authorization;

    /// <summary>Gets the current NNTP operating mode.</summary>
    public NntpSessionMode Mode => _mode;

    /// <summary>
    /// Gets the RX strategy implied by <see cref="Mode"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="NntpReceiveStrategy.ReaderCommand"/> only after <c>MODE READER</c>.
    /// <c>MODE STREAM</c> does not change this (RFC 4644 §2.3).
    /// </remarks>
    internal NntpReceiveStrategy ReceiveStrategy =>
        _mode == NntpSessionMode.Reader
            ? NntpReceiveStrategy.ReaderCommand
            : NntpReceiveStrategy.StreamDataPlane;

    /// <summary>
    /// Gets the pending username from <c>AUTHINFO USER</c> awaiting <c>AUTHINFO PASS</c>,
    /// or <see langword="null"/> when no USER is cached.
    /// </summary>
    public string? PendingAuthUsername => _pendingAuthUsername;

    /// <summary>Replaces the authorization snapshot (tests / advanced handlers).</summary>
    public void SetAuthorization(NntpAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        _authorization = authorization;
        if (!authorization.IsAuthenticated)
        {
            _authentication = NntpAuthenticationState.Unauthenticated;
        }
    }

    /// <summary>Sets the session operating mode.</summary>
    public void SetMode(NntpSessionMode mode) => _mode = mode;

    /// <summary>Caches the username from <c>AUTHINFO USER</c> (does not authenticate).</summary>
    public void SetPendingAuthUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        _pendingAuthUsername = username;
    }

    /// <summary>Clears any pending <c>AUTHINFO USER</c> username.</summary>
    public void ClearPendingAuthUsername() => _pendingAuthUsername = null;

    /// <summary>
    /// Applies a successful authentication atomically: identity + authorization, pending USER cleared.
    /// Forces <see cref="NntpAuthorization.IsAuthenticated"/> to <see langword="true"/>.
    /// </summary>
    public void ApplySuccessfulAuthentication(string username, NntpAuthorization authorization)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(authorization);
        _pendingAuthUsername = null;
        _authentication = NntpAuthenticationState.ForUser(username);
        _authorization = authorization.With(isAuthenticated: true);
    }

    /// <summary>
    /// Records a failed authentication attempt: session remains unauthenticated.
    /// Connection-time peer privileges (if any) are restored; pending USER is retained so the
    /// client may retry <c>AUTHINFO PASS</c> or issue a new <c>AUTHINFO USER</c>.
    /// </summary>
    public void ApplyFailedAuthentication()
    {
        _authentication = NntpAuthenticationState.Unauthenticated;
        _authorization = _connectionAuthorization;
    }

    /// <summary>Requests the command loop to exit after the current response (e.g. QUIT).</summary>
    public void RequestClose() => Interlocked.Exchange(ref _closeRequested, 1);

    /// <summary>
    /// Sends the initial greeting and runs the command loop until QUIT, EOF, or cancellation.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            Connection.ConnectionClosed);
        var token = linked.Token;

        try
        {
            var response = _response ??= new NntpResponseWriter(Connection.Output);
            await SendGreetingAsync(response, token).ConfigureAwait(false);

            var readerRx = new NntpReaderCommandRx(this, _dispatcher, _logger);
            var streamRx = new NntpStreamDataPlaneRx(this, _dispatcher, _logger);

            while (!token.IsCancellationRequested && Volatile.Read(ref _closeRequested) == 0)
            {
                var progressed = ReceiveStrategy == NntpReceiveStrategy.ReaderCommand
                    ? await readerRx.ProcessOneAsync(response, token).ConfigureAwait(false)
                    : await streamRx.ProcessOneAsync(response, token).ConfigureAwait(false);
                if (!progressed)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Expected on shutdown or connection close.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NNTP session ended with an error for {Client}", ClientAddress);
        }
        finally
        {
            if (_response is not null)
            {
                try
                {
                    await _response.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort.
                }
            }

            try
            {
                await StreamArticleTx.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort.
            }

            try
            {
                await Connection.CompleteAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    private ValueTask SendGreetingAsync(NntpResponseWriter response, CancellationToken cancellationToken)
    {
        if (_authorization.PostingPermitted)
        {
            return response.WriteLineAsync(
                NntpReplyCodes.PostingAllowed,
                "VectorNNTP.NNTPD ready, posting permitted",
                cancellationToken);
        }

        return response.WriteLineAsync(
            NntpReplyCodes.PostingProhibited,
            "VectorNNTP.NNTPD ready, posting prohibited",
            cancellationToken);
    }

    /// <summary>
    /// Logs, parses, and dispatches one already-delimited command line (shared by both RX strategies).
    /// </summary>
    internal async ValueTask DispatchRawLineAsync(
        NntpCommandDispatcher dispatcher,
        NntpResponseWriter response,
        ILogger logger,
        string line,
        NntpMultilineReadResult? preReadArticle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(line);

        if (!IsBenchItCommand(line) && !NntpCommandLogFormat.SuppressHotPathCommandLog(line))
        {
            logger.LogInformation(
                "[{Client}] RX: {Command}",
                NntpCommandLogFormat.Client(this),
                NntpCommandLogFormat.RedactRxLine(line));
        }

        if (!NntpCommandParser.TryParse(line, out var parsed))
        {
            var syntaxStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            await response
                .WriteLineAsync(NntpReplyCodes.SyntaxError, "Syntax error", cancellationToken)
                .ConfigureAwait(false);
            NntpCommandExecution.WriteCompletion(
                NntpCommandLoggers.For(typeof(NntpCommandExecution)),
                this,
                NntpCommandLogFormat.DisplayNameFromRawLine(line),
                System.Diagnostics.Stopwatch.GetElapsedTime(syntaxStarted),
                "syntax error");
            return;
        }

        await dispatcher
            .DispatchAsync(this, parsed, response, cancellationToken, preReadArticle)
            .ConfigureAwait(false);
    }

    private static bool IsBenchItCommand(string line)
    {
        // Verb-only internal benchmark command; ignore trailing spaces / unexpected args for log suppression.
        if (line.Length < 7)
        {
            return false;
        }

        if (!line.StartsWith("BENCHIT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return line.Length == 7 || line[7] is ' ' or '\t';
    }
}
