using System.Buffers;

namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// One upstream NNTP session. Not safe for concurrent ARTICLE use.
/// </summary>
public sealed class NntpProviderSession : IAsyncDisposable
{
    private static readonly byte[] QuitCommand = "QUIT\r\n"u8.ToArray();

    private readonly BackFillerProviderDefinition _provider;
    private readonly NntpSessionOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private Stream? _stream;
    private NntpStreamReader? _reader;
    private int _disposed;
    private bool _unhealthy;

    /// <summary>Initializes a session that is not yet connected.</summary>
    public NntpProviderSession(
        BackFillerProviderDefinition provider,
        NntpSessionOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _provider = provider;
        _options = options;
        _logger = logger;
        State = NntpSessionState.Created;
    }

    /// <summary>Gets the current local state.</summary>
    public NntpSessionState State { get; private set; }

    /// <summary>Gets a value indicating whether the session may return to the idle pool.</summary>
    public bool IsReusable => !_unhealthy && State == NntpSessionState.Ready && _stream is not null;

    /// <summary>
    /// Connects, validates the greeting, and authenticates when configured.
    /// </summary>
    /// <returns><see langword="null"/> when the session is <see cref="NntpSessionState.Ready"/>.</returns>
    public async Task<ArticleRetrievalResult?> ConnectAsync(
        INntpTransportFactory transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        State = NntpSessionState.Connecting;
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
                await WriteAsync(command.AsMemory(0, commandLength), _options.CommandTimeout, cancellationToken)
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        State = NntpSessionState.Retiring;
        if (_stream is not null)
        {
            try
            {
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
        await WritePrefixedAsync(NntpProtocolIo.AuthInfoUserPrefix, userBytes, cancellationToken).ConfigureAwait(false);
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

        await WritePrefixedAsync(NntpProtocolIo.AuthInfoPassPrefix, passBytes, cancellationToken).ConfigureAwait(false);
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
            return await _reader
                .ReadLineAsync(_options.MaxStatusLineBytes, _options.CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("NNTP status read timed out.");
        }
    }

    private async Task WritePrefixedAsync(byte[] prefix, byte[] argument, CancellationToken cancellationToken)
    {
        var length = prefix.Length + argument.Length + NntpProtocolIo.Crlf.Length;
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            prefix.CopyTo(rented.AsSpan());
            argument.CopyTo(rented.AsSpan(prefix.Length));
            NntpProtocolIo.Crlf.CopyTo(rented.AsSpan(prefix.Length + argument.Length));
            await WriteAsync(rented.AsMemory(0, length), _options.CommandTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
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
}
