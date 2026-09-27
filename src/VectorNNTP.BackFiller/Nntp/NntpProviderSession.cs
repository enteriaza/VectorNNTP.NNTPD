using System.Buffers;
using System.Text;

namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// One upstream NNTP session. ARTICLE and DATE share one exclusive busy lock.
/// </summary>
public sealed class NntpProviderSession : IAsyncDisposable
{
    private static readonly byte[] QuitCommand = "QUIT\r\n"u8.ToArray();

    private readonly BackFillerProviderDefinition _provider;
    private readonly NntpSessionOptions _options;
    private readonly ILogger _logger;
    private readonly string _wireIdentity;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private Stream? _stream;
    private NntpStreamReader? _reader;
    private int _disposed;
    private bool _unhealthy;

    /// <summary>Initializes a session that is not yet connected.</summary>
    /// <param name="provider">Upstream provider identity and capacity.</param>
    /// <param name="options">Session I/O timeouts and article limits.</param>
    /// <param name="logger">Session logger. Wire traces are Debug only.</param>
    /// <param name="connectionNumber">
    /// One-based slot in <see cref="BackFillerProviderDefinition.MaxSessions"/>.
    /// Stable for this session's lifetime.
    /// </param>
    public NntpProviderSession(
        BackFillerProviderDefinition provider,
        NntpSessionOptions options,
        ILogger logger,
        int connectionNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(connectionNumber, 1);
        _provider = provider;
        _options = options;
        _logger = logger;
        ConnectionNumber = connectionNumber;
        _wireIdentity = FormatWireIdentity(provider, connectionNumber);
        State = NntpSessionState.Created;
    }

    /// <summary>Gets the one-based pool slot captured at construction.</summary>
    public int ConnectionNumber { get; }

    /// <summary>
    /// Gets the stable Debug wire-log prefix
    /// <c>{Backbone}/{Account}[{ConnectionNumber:000}/{MaxSessions}]</c>.
    /// </summary>
    public string WireLogIdentity => _wireIdentity;

    /// <summary>Gets the current local state.</summary>
    public NntpSessionState State { get; private set; }

    /// <summary>Gets a value indicating whether the session may return to the idle pool.</summary>
    public bool IsReusable => !_unhealthy && State == NntpSessionState.Ready && _stream is not null;

    /// <summary>Gets the MySQL <c>keepalive</c> interval this session was constructed with.</summary>
    public byte KeepAliveSeconds => _provider.KeepAliveSeconds;

    /// <summary>
    /// Connects, validates the greeting, issues CAPABILITIES, upgrades via STARTTLS
    /// when advertised, and authenticates when configured.
    /// </summary>
    /// <returns><see langword="null"/> when the session is <see cref="NntpSessionState.Ready"/>.</returns>
    public async Task<ArticleRetrievalResult?> ConnectAsync(
        INntpTransportFactory transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        State = NntpSessionState.Connecting;
        NntpLogMessages.WireConnecting(
            _logger,
            _wireIdentity,
            _provider.Host,
            _provider.Port,
            _provider.UseTls ? "true" : "false");
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(_options.ConnectTimeout);
            _stream = await transport.ConnectAsync(_provider, _options, connectCts.Token).ConfigureAwait(false);
            _reader = new NntpStreamReader(_stream, _options.ReceiveBufferBytes);
            State = NntpSessionState.Connected;

            var greeting = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (greeting is null)
            {
                return FailClosed(ArticleRetrievalKind.ProviderFailure, null, "NNTP greeting was empty.", reusable: false);
            }

            if (!NntpProtocolIo.TryParseStatus(greeting, out var code, out var text))
            {
                return FailClosed(ArticleRetrievalKind.ProviderFailure, null, "NNTP greeting was malformed.", reusable: false);
            }

            if (!NntpStatusCode.IsServiceReadyGreeting(code))
            {
                return FailClosed(ArticleRetrievalKind.ProviderFailure, code, text, reusable: false);
            }

            var startTls = await NegotiateCapabilitiesAndStartTlsAsync(cancellationToken).ConfigureAwait(false);
            if (startTls is not null)
            {
                return startTls;
            }

            var auth = await AuthenticateIfConfiguredAsync(cancellationToken).ConfigureAwait(false);
            if (auth is not null)
            {
                return auth;
            }

            State = NntpSessionState.Ready;
            NntpLogMessages.SessionReady(_logger, _provider.Backbone, _provider.Host, _provider.Port, _provider.UseTls);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return FailClosed(ArticleRetrievalKind.Cancelled, null, "NNTP connect was cancelled.", reusable: false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return FailClosed(ArticleRetrievalKind.ProviderFailure, null, "NNTP connect timed out.", reusable: false);
        }
        catch (Exception ex)
        {
            return FailClosed(ArticleRetrievalKind.ProviderFailure, null, ex.GetType().Name, reusable: false);
        }
    }

    /// <summary>Issues ARTICLE with the exact Message-ID bytes.</summary>
    public async Task<ArticleRetrievalResult> DownloadArticleAsync(string messageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (_reader is null || _stream is null || State is NntpSessionState.Closed or NntpSessionState.Retiring)
        {
            return ArticleRetrievalResult.Failed(
                ArticleRetrievalKind.ProviderFailure,
                null,
                "NNTP session is not ready.",
                sessionReusable: false);
        }

        if (!NntpProtocolIo.TryEncodeAscii(messageId, out var messageIdBytes))
        {
            return ArticleRetrievalResult.Failed(
                ArticleRetrievalKind.ProviderFailure,
                null,
                "Message-ID is not ASCII and cannot be sent on the NNTP wire.",
                sessionReusable: true);
        }

        await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previous = State;
        State = NntpSessionState.Busy;
        try
        {
            var commandLength = NntpProtocolIo.ArticlePrefix.Length + messageIdBytes.Length + NntpProtocolIo.Crlf.Length;
            var command = ArrayPool<byte>.Shared.Rent(commandLength);
            try
            {
                NntpProtocolIo.ArticlePrefix.CopyTo(command.AsSpan());
                messageIdBytes.CopyTo(command.AsSpan(NntpProtocolIo.ArticlePrefix.Length));
                NntpProtocolIo.Crlf.CopyTo(command.AsSpan(NntpProtocolIo.ArticlePrefix.Length + messageIdBytes.Length));
                await WriteCommandAsync(
                        "ARTICLE " + messageId,
                        command.AsMemory(0, commandLength),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(command);
            }

            var status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status is null)
            {
                return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, "NNTP ARTICLE status was empty.");
            }

            if (!NntpProtocolIo.TryParseStatus(status, out var code, out var text))
            {
                return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, "NNTP ARTICLE status was malformed.");
            }

            if (code == NntpStatusCode.ArticleFollows)
            {
                byte[] payload;
                try
                {
                    payload = await _reader
                        .ReadArticlePayloadAsync(_options.MaxArticleBytes, _options.ReceiveTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    NntpLogMessages.WireArticlePayloadComplete(_logger, _wireIdentity, payload.Length);
                }
                catch (EndOfStreamException)
                {
                    return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, code, "NNTP article ended before terminator.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return MarkUnhealthy(ArticleRetrievalKind.Cancelled, code, "ARTICLE receive was cancelled.");
                }
                catch (OperationCanceledException)
                {
                    return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, code, "ARTICLE receive timed out.");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("MaxArticleBytes", StringComparison.Ordinal))
                {
                    return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, code, "NNTP article exceeded MaxArticleBytes.");
                }

                if (payload.Length == 0 || !NntpProtocolIo.HasHeaderBodySeparator(payload))
                {
                    State = NntpSessionState.Ready;
                    return ArticleRetrievalResult.Failed(
                        ArticleRetrievalKind.InvalidArticle,
                        code,
                        "ARTICLE payload is missing a header/body separator.",
                        sessionReusable: true);
                }

                State = NntpSessionState.Ready;
                return ArticleRetrievalResult.Retrieved(code, text, new RetrievedArticle(payload));
            }

            if (code == NntpStatusCode.NoArticleWithMessageId)
            {
                State = NntpSessionState.Ready;
                return ArticleRetrievalResult.Failed(
                    ArticleRetrievalKind.ArticleNotFound,
                    code,
                    text,
                    sessionReusable: true);
            }

            if (NntpStatusCode.IsAuthenticationFailure(code))
            {
                return MarkUnhealthy(ArticleRetrievalKind.AuthenticationFailure, code, text);
            }

            if (NntpStatusCode.IsCommandRejected(code)
                || code is NntpStatusCode.NoNewsgroupSelected
                    or NntpStatusCode.CurrentArticleInvalid
                    or NntpStatusCode.NoArticleWithNumber)
            {
                return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, code, text);
            }

            return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, code, text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MarkUnhealthy(ArticleRetrievalKind.Cancelled, null, "ARTICLE was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, "ARTICLE timed out.");
        }
        catch (Exception ex)
        {
            return MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, ex.GetType().Name);
        }
        finally
        {
            if (State == NntpSessionState.Busy)
            {
                State = previous;
            }

            _ = _busy.Release();
        }
    }

    /// <summary>
    /// Issues RFC 3977 DATE as an idle-session keepalive. Serialized with ARTICLE
    /// through <see cref="_busy"/>. When <paramref name="waitForIdle"/> is
    /// <see langword="false"/> and the session is already busy, DATE is skipped
    /// so article acquisition is not blocked.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the session remains reusable (111, or DATE skipped).
    /// <see langword="false"/> when the session was marked unhealthy.
    /// </returns>
    internal async Task<bool> SendDateKeepAliveAsync(
        CancellationToken cancellationToken,
        bool waitForIdle = false)
    {
        if (_reader is null || _stream is null || _unhealthy
            || State is NntpSessionState.Closed or NntpSessionState.Retiring
            || Volatile.Read(ref _disposed) == 1)
        {
            return false;
        }

        if (waitForIdle)
        {
            await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (!await _busy.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var previous = State;
        State = NntpSessionState.Busy;
        var wrote = false;
        try
        {
            await WriteCommandAsync("DATE", NntpProtocolIo.DateCommand, cancellationToken)
                .ConfigureAwait(false);
            wrote = true;
            var status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status is null)
            {
                _ = MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, "NNTP DATE status was empty.");
                return false;
            }

            if (!NntpProtocolIo.TryParseStatus(status, out var code, out var text))
            {
                _ = MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, "NNTP DATE status was malformed.");
                return false;
            }

            if (code != NntpStatusCode.DateFollows)
            {
                _ = MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, code, text);
                return false;
            }

            State = NntpSessionState.Ready;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (wrote)
            {
                _ = MarkUnhealthy(ArticleRetrievalKind.Cancelled, null, "DATE keepalive was cancelled.");
                return false;
            }

            State = previous;
            return true;
        }
        catch (OperationCanceledException)
        {
            _ = MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, "DATE keepalive timed out.");
            return false;
        }
        catch (Exception ex)
        {
            _ = MarkUnhealthy(ArticleRetrievalKind.ProviderFailure, null, ex.GetType().Name);
            return false;
        }
        finally
        {
            if (State == NntpSessionState.Busy)
            {
                State = previous;
            }

            _ = _busy.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        State = NntpSessionState.Retiring;
        NntpLogMessages.WireRetiring(_logger, _wireIdentity);
        if (_stream is not null)
        {
            try
            {
                NntpLogMessages.WireTx(_logger, _wireIdentity, "QUIT");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _stream.WriteAsync(QuitCommand, cts.Token).ConfigureAwait(false);
                await _stream.FlushAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _busy.Dispose();
        State = NntpSessionState.Closed;
    }

    private async Task<ArticleRetrievalResult?> NegotiateCapabilitiesAndStartTlsAsync(
        CancellationToken cancellationToken)
    {
        await WriteCommandAsync("CAPABILITIES", NntpProtocolIo.CapabilitiesCommand, cancellationToken)
            .ConfigureAwait(false);
        var status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status is null || !NntpProtocolIo.TryParseStatus(status, out var code, out var text))
        {
            return FailClosed(
                ArticleRetrievalKind.ProviderFailure,
                null,
                "NNTP CAPABILITIES status was empty or malformed.",
                reusable: false);
        }

        if (code != NntpStatusCode.CapabilityListFollows)
        {
            return FailClosed(ArticleRetrievalKind.ProviderFailure, code, text, reusable: false);
        }

        var startTlsAdvertised = false;
        for (var i = 0; i < NntpProtocolIo.MaxCapabilityLines; i++)
        {
            var line = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return FailClosed(
                    ArticleRetrievalKind.ProviderFailure,
                    code,
                    "NNTP CAPABILITIES list ended before the terminator.",
                    reusable: false);
            }

            if (NntpProtocolIo.IsMultilineTerminator(line))
            {
                return startTlsAdvertised && !_provider.UseTls
                    ? await IssueStartTlsAndUpgradeAsync(cancellationToken).ConfigureAwait(false)
                    : null;
            }

            var capability = NntpProtocolIo.DestuffDotLine(line);
            if (NntpProtocolIo.CapabilityLabelEquals(capability, NntpProtocolIo.StartTlsCapability))
            {
                startTlsAdvertised = true;
            }
        }

        return FailClosed(
            ArticleRetrievalKind.ProviderFailure,
            code,
            "NNTP CAPABILITIES list exceeded the maximum number of lines.",
            reusable: false);
    }

    private async Task<ArticleRetrievalResult?> IssueStartTlsAndUpgradeAsync(CancellationToken cancellationToken)
    {
        await WriteCommandAsync("STARTTLS", NntpProtocolIo.StartTlsCommand, cancellationToken)
            .ConfigureAwait(false);
        var status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status is null || !NntpProtocolIo.TryParseStatus(status, out var code, out var text))
        {
            return FailClosed(
                ArticleRetrievalKind.ProviderFailure,
                null,
                "NNTP STARTTLS status was empty or malformed.",
                reusable: false);
        }

        if (code != NntpStatusCode.ContinueWithTlsNegotiation)
        {
            return FailClosed(ArticleRetrievalKind.ProviderFailure, code, text, reusable: false);
        }

        if (_reader is not null && _reader.BufferedByteCount != 0)
        {
            return FailClosed(
                ArticleRetrievalKind.ProviderFailure,
                code,
                "NNTP STARTTLS left unread bytes before TLS negotiation.",
                reusable: false);
        }

        return await UpgradeExistingTransportToTlsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArticleRetrievalResult?> UpgradeExistingTransportToTlsAsync(
        CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return FailClosed(
                ArticleRetrievalKind.ProviderFailure,
                null,
                "NNTP stream is not open for STARTTLS.",
                reusable: false);
        }

        NntpLogMessages.WireTlsHandshakeStarting(_logger, _wireIdentity);
        try
        {
            var ssl = await NntpTlsClient.AuthenticateAsClientAsync(
                    _stream,
                    _provider.Host,
                    _options.ConnectTimeout,
                    _options.ServerCertificateValidationCallback,
                    cancellationToken)
                .ConfigureAwait(false);
            _stream = ssl;
            _reader = new NntpStreamReader(_stream, _options.ReceiveBufferBytes);
            NntpLogMessages.WireTlsHandshakeCompleted(_logger, _wireIdentity);
            return null;
        }
        catch (OperationCanceledException)
        {
            _stream = null;
            _reader = null;
            throw;
        }
        catch (Exception ex)
        {
            _stream = null;
            _reader = null;
            return FailClosed(
                ArticleRetrievalKind.ProviderFailure,
                null,
                $"TLS handshake failed ({ex.GetType().Name}).",
                reusable: false);
        }
    }

    private async Task<ArticleRetrievalResult?> AuthenticateIfConfiguredAsync(CancellationToken cancellationToken)
    {
        if (!_provider.RequiresAuthentication)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_provider.Username) || string.IsNullOrWhiteSpace(_provider.Password))
        {
            return FailClosed(
                ArticleRetrievalKind.AuthenticationFailure,
                null,
                "Both Username and Password must be configured together.",
                reusable: false);
        }

        if (!NntpProtocolIo.TryEncodeAscii(_provider.Username, out var userBytes)
            || !NntpProtocolIo.TryEncodeAscii(_provider.Password, out var passBytes))
        {
            return FailClosed(
                ArticleRetrievalKind.AuthenticationFailure,
                null,
                "Provider credentials contain non-ASCII bytes.",
                reusable: false);
        }

        State = NntpSessionState.Authenticating;
        await WritePrefixedAsync(
                NntpProtocolIo.AuthInfoUserPrefix,
                userBytes,
                "AUTHINFO USER ***",
                cancellationToken)
            .ConfigureAwait(false);
        var userLine = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        if (userLine is null || !NntpProtocolIo.TryParseStatus(userLine, out var userCode, out var userText))
        {
            return FailClosed(ArticleRetrievalKind.ProviderFailure, null, "Malformed AUTHINFO USER status line.", reusable: false);
        }

        if (userCode == NntpStatusCode.AuthenticationAccepted)
        {
            return null;
        }

        if (userCode != NntpStatusCode.PasswordRequired)
        {
            return ClassifyAuthFailure(userCode, userText);
        }

        await WritePrefixedAsync(
                NntpProtocolIo.AuthInfoPassPrefix,
                passBytes,
                "AUTHINFO PASS ***",
                cancellationToken)
            .ConfigureAwait(false);
        var passLine = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        if (passLine is null || !NntpProtocolIo.TryParseStatus(passLine, out var passCode, out var passText))
        {
            return FailClosed(ArticleRetrievalKind.ProviderFailure, null, "Malformed AUTHINFO PASS status line.", reusable: false);
        }

        return passCode == NntpStatusCode.AuthenticationAccepted ? null : ClassifyAuthFailure(passCode, passText);
    }

    private ArticleRetrievalResult ClassifyAuthFailure(int code, string text)
    {
        if (NntpStatusCode.IsAuthenticationFailure(code) || NntpStatusCode.IsCommandRejected(code))
        {
            return FailClosed(ArticleRetrievalKind.AuthenticationFailure, code, text, reusable: false);
        }

        return FailClosed(ArticleRetrievalKind.ProviderFailure, code, text, reusable: false);
    }

    private async Task<byte[]?> ReadStatusAsync(CancellationToken cancellationToken)
    {
        if (_reader is null)
        {
            return null;
        }

        try
        {
            var line = await _reader
                .ReadLineAsync(_options.MaxStatusLineBytes, _options.CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (line is not null)
            {
                NntpLogMessages.WireRx(_logger, _wireIdentity, Encoding.ASCII.GetString(line));
            }

            return line;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("NNTP status read timed out.");
        }
    }

    private async Task WritePrefixedAsync(
        byte[] prefix,
        byte[] argument,
        string wireCommand,
        CancellationToken cancellationToken)
    {
        var length = prefix.Length + argument.Length + NntpProtocolIo.Crlf.Length;
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            prefix.CopyTo(rented.AsSpan());
            argument.CopyTo(rented.AsSpan(prefix.Length));
            NntpProtocolIo.Crlf.CopyTo(rented.AsSpan(prefix.Length + argument.Length));
            await WriteCommandAsync(wireCommand, rented.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private Task WriteCommandAsync(
        string wireCommand,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        NntpLogMessages.WireTx(_logger, _wireIdentity, wireCommand);
        return WriteAsync(bytes, _options.CommandTimeout, cancellationToken);
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> bytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("NNTP stream is not open.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        await _stream.WriteAsync(bytes, timeoutCts.Token).ConfigureAwait(false);
        await _stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private ArticleRetrievalResult MarkUnhealthy(ArticleRetrievalKind kind, int? code, string reason)
    {
        _unhealthy = true;
        State = NntpSessionState.Retiring;
        return ArticleRetrievalResult.Failed(kind, code, reason, sessionReusable: false);
    }

    private ArticleRetrievalResult FailClosed(ArticleRetrievalKind kind, int? code, string reason, bool reusable)
    {
        _unhealthy = !reusable;
        State = reusable ? NntpSessionState.Ready : NntpSessionState.Retiring;
        return ArticleRetrievalResult.Failed(kind, code, reason, reusable);
    }

    internal static string FormatWireIdentity(BackFillerProviderDefinition provider, int connectionNumber)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var account = string.IsNullOrWhiteSpace(provider.Username) ? "-" : provider.Username.Trim();
        return $"{provider.Backbone}/{account}[{connectionNumber:000}/{provider.MaxSessions}]";
    }
}
