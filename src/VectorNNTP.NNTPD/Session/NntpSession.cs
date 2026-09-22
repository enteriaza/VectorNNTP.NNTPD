using System.Net;
using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session.Authentication;
using VectorNNTP.NNTPD.Session.Commands;

namespace VectorNNTP.NNTPD.Session;

/// <summary>
/// NNTP session boundary: greeting, command loop, mode, authentication, and authorization state over an
/// <see cref="INntpConnection"/>.
/// </summary>
/// <remarks>
/// Transport owns sockets, TLS, DEFLATE, PROXY identity, and connection lifecycle.
/// This type owns NNTP protocol sequencing and does not touch sockets or stream wrappers directly.
/// Authentication and authorization are distinct; AUTHINFO success applies only provider-returned privileges.
/// </remarks>
public sealed class NntpSession
{
    private readonly ILogger<NntpSession> _logger;
    private readonly NntpCommandDispatcher _dispatcher;
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
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);
        Connection = connection;
        ClientIdentity = connection.ClientIdentity;
        _logger = logger;
        _authorization = NntpAuthorization.Unauthenticated;
        _authentication = NntpAuthenticationState.Unauthenticated;
        _mode = NntpSessionMode.Unspecified;
        AllowCleartextAuth = allowCleartextAuth;
        AuthenticationProvider = authenticationProvider ?? DenyAllNntpAuthenticationProvider.Instance;
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
    /// Records a failed authentication attempt: session remains unauthenticated with default-deny
    /// authorization. Pending USER is retained so the client may retry <c>AUTHINFO PASS</c>
    /// or issue a new <c>AUTHINFO USER</c>.
    /// </summary>
    public void ApplyFailedAuthentication()
    {
        _authentication = NntpAuthenticationState.Unauthenticated;
        _authorization = NntpAuthorization.Unauthenticated;
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

            while (!token.IsCancellationRequested && Volatile.Read(ref _closeRequested) == 0)
            {
                var line = await NntpCommandLineReader.ReadLineAsync(Connection.Input, token)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                var client = NntpCommandLogFormat.Client(this);
                _logger.LogInformation(
                    "[{Client}] RX: {Command}",
                    client,
                    NntpCommandLogFormat.RedactRxLine(line));

                if (!NntpCommandParser.TryParse(line, out var parsed))
                {
                    var syntaxStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    await response
                        .WriteLineAsync(NntpReplyCodes.SyntaxError, "Syntax error", token)
                        .ConfigureAwait(false);
                    NntpCommandExecution.WriteCompletion(
                        NntpCommandLoggers.For(typeof(NntpCommandExecution)),
                        this,
                        NntpCommandLogFormat.DisplayNameFromRawLine(line),
                        System.Diagnostics.Stopwatch.GetElapsedTime(syntaxStarted),
                        "syntax error");
                    continue;
                }

                await _dispatcher.DispatchAsync(this, parsed, response, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Expected on shutdown or connection close.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NNTP session ended with an error for {Client}.", ClientAddress);
        }
        finally
        {
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
}
