using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Text;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.BackFiller.Nntp
{
    /// <summary>
    /// One upstream NNTP session. ARTICLE and DATE share one exclusive busy lock.
    /// </summary>
    /// <remarks>
    /// Connect accepts a 200 or 201 greeting, always sends <c>CAPABILITIES</c>, sends <c>STARTTLS</c> only when that label is advertised and the provider is not already on implicit TLS, then sends <c>AUTHINFO</c> when credentials are required.
    /// The session owns the transport after connect, including when connect returns a failure. The caller disposes it.
    /// </remarks>
    internal sealed class NntpProviderSession : IAsyncDisposable
    {
        /// <summary>Pre-encoded <c>QUIT</c> command written by <see cref="DisposeAsync"/>.</summary>
        private static readonly byte[] QuitCommand = "QUIT\r\n"u8.ToArray();

        /// <summary>Provider identity, TLS mode, credentials, and keepalive interval captured at construction.</summary>
        private readonly BackFillerProviderDefinition _provider;

        /// <summary>Timeouts, buffer size, and article ceiling used for this connection.</summary>
        private readonly NntpSessionOptions _options;

        /// <summary>Session logger. Command and response traces are Debug.</summary>
        private readonly ILogger _logger;

        /// <summary>Stable Debug prefix built by <see cref="FormatWireIdentity"/>.</summary>
        private readonly string _wireIdentity;

        /// <summary>Serializes <see cref="DownloadArticleAsync"/> and <see cref="SendDateKeepAliveAsync"/>. Not held during connect or dispose.</summary>
        private readonly SemaphoreSlim _busy = new(1, 1);

        /// <summary>Owned transport. Null until connect assigns it, and null again if a TLS upgrade fails after closing it.</summary>
        private Stream? _stream;

        /// <summary>Reader over <see cref="_stream"/>. Replaced after a STARTTLS upgrade so leftover cleartext is not parsed as TLS.</summary>
        private NntpStreamReader? _reader;

        /// <summary>Zero until <see cref="DisposeAsync"/> runs. A second dispose returns immediately.</summary>
        private int _disposed;

        /// <summary>Set when a command failure must keep the session out of the idle pool. Sticky for the session lifetime.</summary>
        private bool _unhealthy;

        /// <summary>Initializes a session that is not yet connected.</summary>
        /// <param name="provider">Upstream provider identity and capacity.</param>
        /// <param name="options">Session I/O timeouts and article limits.</param>
        /// <param name="logger">Session logger. Wire traces are Debug only.</param>
        /// <param name="connectionNumber">
        /// One-based slot in <see cref="BackFillerProviderDefinition.MaxSessions"/>.
        /// Stable for this session's lifetime.
        /// </param>
        internal NntpProviderSession(
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
        internal int ConnectionNumber { get; }

        /// <summary>
        /// Gets the stable Debug wire-log prefix
        /// <c>{Backbone}/{Account}[{ConnectionNumber:000}/{MaxSessions}]</c>.
        /// </summary>
        internal string WireLogIdentity => _wireIdentity;

        /// <summary>
        /// Gets the session lifecycle updated by connect, <c>ARTICLE</c>, <c>DATE</c>, and <see cref="DisposeAsync"/>.
        /// </summary>
        internal NntpSessionState State { get; private set; }

        /// <summary>Gets a value indicating whether the session may return to the idle pool.</summary>
        /// <remarks>
        /// True only when the session has not been marked unhealthy, <see cref="State"/> is <see cref="NntpSessionState.Ready"/>, and <see cref="_stream"/> is assigned.
        /// The read does not take <see cref="_busy"/>.
        /// </remarks>
        internal bool IsReusable => !_unhealthy && State == NntpSessionState.Ready && _stream is not null;

        /// <summary>Gets the MySQL <c>keepalive</c> interval this session was constructed with.</summary>
        internal byte KeepAliveSeconds => _provider.KeepAliveSeconds;

        /// <summary>
        /// Connects, validates the greeting, issues CAPABILITIES, upgrades via STARTTLS
        /// when that capability is advertised and the provider is not already using implicit TLS, and authenticates when configured.
        /// </summary>
        /// <param name="transport">Opens the socket. Implicit TLS is applied by the transport when <see cref="BackFillerProviderDefinition.UseTls"/> is set.</param>
        /// <param name="cancellationToken">
        /// Cancels connect and later command I/O. Caller cancellation returns <see cref="ArticleRetrievalKind.Cancelled"/> and does not throw.
        /// </param>
        /// <returns><see langword="null"/> when the session is <see cref="NntpSessionState.Ready"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="transport"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The session was already disposed.</exception>
        /// <remarks>
        /// A 200 or 201 greeting is required. <c>CAPABILITIES</c> is always sent. <c>STARTTLS</c> is sent only when advertised and <see cref="BackFillerProviderDefinition.UseTls"/> is false.
        /// Protocol, timeout, and I/O failures return a non-reusable <see cref="ArticleRetrievalResult"/> with <see cref="State"/> <see cref="NntpSessionState.Retiring"/>.
        /// The transport connect budget is <see cref="NntpSessionOptions.ConnectTimeout"/>. The greeting and later status lines use <see cref="NntpSessionOptions.CommandTimeout"/>.
        /// The caller still owns disposal of this session when a failure result is returned.
        /// </remarks>
        internal async Task<ArticleRetrievalResult?> ConnectAsync(
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
        /// <param name="messageId">Argument appended to <c>ARTICLE </c>. Sent as ASCII with no angle-bracket check. CR and LF are not rejected.</param>
        /// <param name="cancellationToken">
        /// Cancellation while waiting for <see cref="_busy"/> throws <see cref="OperationCanceledException"/> and leaves the session unchanged.
        /// After the lock is held, cancellation returns <see cref="ArticleRetrievalKind.Cancelled"/> and marks the session unhealthy.
        /// </param>
        /// <param name="consumePayload">
        /// When null, a successful article is copied into an owned <see cref="RetrievedArticle"/>.
        /// When not null, it is invoked synchronously while this session still holds <see cref="_busy"/> and before
        /// <see cref="State"/> returns to <see cref="NntpSessionState.Ready"/>, with the reader scratch prefix.
        /// That memory is valid only until the delegate returns. The delegate must not store it and must not await.
        /// It is called only after the terminator is recognized and the payload has a header/body separator.
        /// An exception from the delegate is rethrown after <see cref="_busy"/> is released and does not mark the session unhealthy.
        /// </param>
        /// <returns>
        /// Status <see cref="NntpStatusCode.ArticleFollows"/> returns the destuffed payload when <see cref="NntpProtocolIo.HasHeaderBodySeparator"/> is true.
        /// With <paramref name="consumePayload"/> null, that payload is an owned <see cref="RetrievedArticle"/>.
        /// With <paramref name="consumePayload"/> not null, the result's article is null because the delegate already consumed the scratch.
        /// Status <see cref="NntpStatusCode.NoArticleWithMessageId"/> is <see cref="ArticleRetrievalKind.ArticleNotFound"/> and the session stays reusable.
        /// A 220 payload with no header/body separator is <see cref="ArticleRetrievalKind.InvalidArticle"/> and stays reusable.
        /// A payload that reaches <see cref="NntpSessionOptions.MaxArticleBytes"/> is <see cref="ArticleRetrievalKind.InvalidArticle"/> and the session is marked unhealthy because the multiline response is no longer synchronized.
        /// <see cref="NntpStatusCode.IsAuthenticationFailure"/>, <see cref="NntpStatusCode.IsCommandRejected"/>, statuses 412, 420, and 423, and every other status mark the session unhealthy.
        /// </returns>
        /// <exception cref="ArgumentException"><paramref name="messageId"/> is null or whitespace.</exception>
        /// <remarks>
        /// A non-ASCII message-id fails before the lock is taken and leaves the session reusable.
        /// A closed or retiring session, or one whose reader or stream is missing, returns a non-reusable provider failure without taking the lock and without setting <see cref="_unhealthy"/>.
        /// A status-line timeout becomes <see cref="TimeoutException"/> inside <see cref="ReadStatusAsync"/> and is then classified as provider failure whose reason is that exception's type name.
        /// A payload timeout is classified as provider failure with the reason <c>ARTICLE timed out.</c>
        /// </remarks>
        internal async Task<ArticleRetrievalResult> DownloadArticleAsync(
            string messageId,
            CancellationToken cancellationToken,
            Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult>? consumePayload = null)
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
            ExceptionDispatchInfo? consumerError = null;
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
                    if (consumePayload is null)
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
                            // Destuffed size exceeded the hard ceiling: permanently unusable for Article Work.
                            // Retire the session because the multiline response is no longer synchronized.
                            return MarkUnhealthy(ArticleRetrievalKind.InvalidArticle, code, "NNTP article exceeded MaxArticleBytes.");
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

                    var accepted = false;
                    try
                    {
                        _ = await _reader.ReadArticlePayloadAsync(
                                _options.MaxArticleBytes,
                                _options.ReceiveTimeout,
                                cancellationToken,
                                memory =>
                                {
                                    NntpLogMessages.WireArticlePayloadComplete(_logger, _wireIdentity, memory.Length);
                                    if (memory.Length == 0 || !NntpProtocolIo.HasHeaderBodySeparator(memory.Span))
                                    {
                                        return default;
                                    }

                                    accepted = true;
                                    try
                                    {
                                        return consumePayload(memory);
                                    }
                                    catch (Exception ex)
                                    {
                                        // Leave the network-failure catches. The original exception is rethrown after _busy is released.
                                        consumerError = ExceptionDispatchInfo.Capture(ex);
                                        throw new PayloadConsumerException();
                                    }
                                })
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
                        return MarkUnhealthy(ArticleRetrievalKind.InvalidArticle, code, "NNTP article exceeded MaxArticleBytes.");
                    }

                    if (!accepted)
                    {
                        State = NntpSessionState.Ready;
                        return ArticleRetrievalResult.Failed(
                            ArticleRetrievalKind.InvalidArticle,
                            code,
                            "ARTICLE payload is missing a header/body separator.",
                            sessionReusable: true);
                    }

                    State = NntpSessionState.Ready;
                    return ArticleRetrievalResult.Retrieved(code, text);
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
            catch (PayloadConsumerException)
            {
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

            if (consumerError is not null)
            {
                consumerError.Throw();
            }

            throw new InvalidOperationException("ARTICLE download ended without a result.");
        }

        /// <summary>
        /// Issues RFC 3977 DATE as an idle-session keepalive. Serialized with ARTICLE
        /// through <see cref="_busy"/>. When <paramref name="waitForIdle"/> is
        /// <see langword="false"/> and the session is already busy, DATE is skipped
        /// so article acquisition is not blocked.
        /// </summary>
        /// <param name="cancellationToken">
        /// When <paramref name="waitForIdle"/> is true, cancellation while waiting for <see cref="_busy"/> throws <see cref="OperationCanceledException"/>.
        /// Cancellation after <c>DATE</c> is written marks the session unhealthy and returns <see langword="false"/>.
        /// Cancellation before that write restores the previous <see cref="State"/> and returns <see langword="true"/>.
        /// </param>
        /// <param name="waitForIdle">
        /// <see langword="true"/> waits for <see cref="_busy"/>.
        /// <see langword="false"/> returns <see langword="true"/> immediately when the lock is already held, without writing <c>DATE</c>.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the server returns <see cref="NntpStatusCode.DateFollows"/>, when DATE is skipped because the session is busy, or when cancellation happens before DATE is written.
        /// <see langword="false"/> when the session is already unusable or this call marks it unhealthy.
        /// </returns>
        /// <remarks>
        /// Returns <see langword="false"/> immediately when the reader or stream is missing, the session is unhealthy, closed, retiring, or disposed.
        /// Only <see cref="NntpStatusCode.DateFollows"/> is success. Any other parsed status marks the session unhealthy.
        /// A command timeout is provider failure and marks the session unhealthy.
        /// </remarks>
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

        /// <summary>
        /// Retires the session, best-effort writes <c>QUIT</c>, and disposes the transport.
        /// A second call returns immediately.
        /// </summary>
        /// <remarks>
        /// <c>QUIT</c> uses a two-second timeout and is not followed by a response read. Write and flush failures are ignored.
        /// <see cref="_busy"/> is disposed. <see cref="State"/> ends as <see cref="NntpSessionState.Closed"/>.
        /// </remarks>
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

        /// <summary>
        /// Sends <c>CAPABILITIES</c> and, when <c>STARTTLS</c> is advertised and implicit TLS is off, upgrades the existing transport.
        /// </summary>
        /// <param name="cancellationToken">Cancels the exchange. <see cref="ConnectAsync"/> classifies a propagated cancellation.</param>
        /// <returns>
        /// <see langword="null"/> when the list completed and no upgrade was required, or when the upgrade succeeded.
        /// Otherwise a non-reusable failure.
        /// </returns>
        /// <remarks>
        /// Requires status <see cref="NntpStatusCode.CapabilityListFollows"/> and a dot-terminated body of at most <see cref="NntpProtocolIo.MaxCapabilityLines"/> lines.
        /// Only the <c>STARTTLS</c> label is recorded. An empty, malformed, non-101, unterminated, or over-long list fails closed.
        /// </remarks>
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

        /// <summary>Sends <c>STARTTLS</c> and, on status 382 with no unread bytes, replaces the transport with TLS.</summary>
        /// <param name="cancellationToken">Cancels the command and the handshake.</param>
        /// <returns>
        /// <see langword="null"/> when the handshake completed. Otherwise a non-reusable failure.
        /// This method does not read a greeting after the handshake.
        /// </returns>
        /// <remarks>
        /// <see cref="NntpStreamReader.BufferedByteCount"/> other than zero fails the session so negotiation does not start on a desynchronized stream.
        /// </remarks>
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

        /// <summary>Wraps <see cref="_stream"/> with TLS and installs a new reader over the encrypted stream.</summary>
        /// <param name="cancellationToken">Cancels the handshake together with <see cref="NntpSessionOptions.ConnectTimeout"/>.</param>
        /// <returns><see langword="null"/> when the handshake completed. A handshake exception other than cancellation returns a non-reusable provider failure.</returns>
        /// <remarks>
        /// There is no post-handshake greeting read. On failure the fields are cleared.
        /// <see cref="NntpTlsClient.AuthenticateAsClientAsync"/> closes <see cref="_stream"/> when the handshake throws.
        /// Cancellation is rethrown after the fields are cleared so <see cref="ConnectAsync"/> can classify it.
        /// </remarks>
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

        /// <summary>
        /// Sends <c>AUTHINFO USER</c> and, when the server replies 381, <c>AUTHINFO PASS</c>.
        /// </summary>
        /// <param name="cancellationToken">Cancels the exchange. <see cref="ConnectAsync"/> classifies a propagated cancellation.</param>
        /// <returns>
        /// <see langword="null"/> when authentication is not required or the server accepts it with <see cref="NntpStatusCode.AuthenticationAccepted"/>.
        /// Otherwise a non-reusable failure.
        /// </returns>
        /// <remarks>
        /// <see cref="BackFillerProviderDefinition.RequiresAuthentication"/> is true when either credential is non-whitespace, but both must be present and ASCII or this returns <see cref="ArticleRetrievalKind.AuthenticationFailure"/> without writing a command.
        /// Wire logs use <c>AUTHINFO USER ***</c> and <c>AUTHINFO PASS ***</c>. A 281 reply to USER does not send PASS.
        /// </remarks>
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

        /// <summary>Maps an AUTHINFO rejection to authentication failure or a generic provider failure.</summary>
        /// <param name="code">Status code from USER or PASS.</param>
        /// <param name="text">Status text, stored on the result as the reason.</param>
        /// <returns>
        /// <see cref="ArticleRetrievalKind.AuthenticationFailure"/> for <see cref="NntpStatusCode.IsAuthenticationFailure"/> and <see cref="NntpStatusCode.IsCommandRejected"/>.
        /// Every other code is <see cref="ArticleRetrievalKind.ProviderFailure"/>. The session is not reusable.
        /// </returns>
        private ArticleRetrievalResult ClassifyAuthFailure(int code, string text)
        {
            if (NntpStatusCode.IsAuthenticationFailure(code) || NntpStatusCode.IsCommandRejected(code))
            {
                return FailClosed(ArticleRetrievalKind.AuthenticationFailure, code, text, reusable: false);
            }

            return FailClosed(ArticleRetrievalKind.ProviderFailure, code, text, reusable: false);
        }

        /// <summary>
        /// Reads one status line with <see cref="NntpSessionOptions.MaxStatusLineBytes"/> and <see cref="NntpSessionOptions.CommandTimeout"/>, then logs the ASCII text at Debug.
        /// </summary>
        /// <param name="cancellationToken">Caller cancellation. Distinguished from the command timeout.</param>
        /// <returns>The line without its delimiter, or <see langword="null"/> when <see cref="_reader"/> is missing or the peer closes before any byte.</returns>
        /// <exception cref="TimeoutException">The command timeout elapses while <paramref name="cancellationToken"/> is not cancelled.</exception>
        /// <remarks>
        /// Caller cancellation propagates as <see cref="OperationCanceledException"/>.
        /// <see cref="EndOfStreamException"/> and an over-long line propagate to the caller.
        /// </remarks>
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
                if (line is null || !_logger.IsEnabled(LogLevel.Debug))
                {
                    return line;
                }

                var response = Encoding.ASCII.GetString(line);
                NntpLogMessages.WireRx(_logger, _wireIdentity, response);
                return line;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("NNTP status read timed out.");
            }
        }

        /// <summary>Writes <paramref name="prefix"/>, <paramref name="argument"/>, and CRLF, logging <paramref name="wireCommand"/> instead of the argument.</summary>
        /// <param name="prefix">Command prefix, such as <see cref="NntpProtocolIo.AuthInfoUserPrefix"/>.</param>
        /// <param name="argument">ASCII field. Not written to the log.</param>
        /// <param name="wireCommand">Debug TX text. Callers pass a redacted command for credentials.</param>
        /// <param name="cancellationToken">Forwarded to <see cref="WriteCommandAsync"/>.</param>
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

        /// <summary>Logs <paramref name="wireCommand"/> at Debug, then writes <paramref name="bytes"/> under <see cref="NntpSessionOptions.CommandTimeout"/>.</summary>
        /// <param name="wireCommand">Text passed to <see cref="NntpLogMessages.WireTx"/>.</param>
        /// <param name="bytes">Exact command bytes, including CRLF.</param>
        /// <param name="cancellationToken">Linked with the command timeout.</param>
        /// <returns>The write started by <see cref="WriteAsync"/>.</returns>
        private Task WriteCommandAsync(
            string wireCommand,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            NntpLogMessages.WireTx(_logger, _wireIdentity, wireCommand);
            return WriteAsync(bytes, _options.CommandTimeout, cancellationToken);
        }

        /// <summary>Writes and flushes <paramref name="bytes"/>, cancelling when <paramref name="timeout"/> elapses or <paramref name="cancellationToken"/> is cancelled.</summary>
        /// <param name="bytes">Bytes to write.</param>
        /// <param name="timeout">Linked budget. Not converted into <see cref="TimeoutException"/> here.</param>
        /// <param name="cancellationToken">Caller cancellation, linked with <paramref name="timeout"/>.</param>
        /// <exception cref="InvalidOperationException"><see cref="_stream"/> is null.</exception>
        /// <remarks>Both the timeout and caller cancellation surface as <see cref="OperationCanceledException"/>.</remarks>
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

        /// <summary>Marks the session unhealthy and retiring, and returns a non-reusable failure.</summary>
        /// <param name="kind">Classification stored on the result.</param>
        /// <param name="code">Status code when one was parsed; otherwise null.</param>
        /// <param name="reason">Diagnostic text stored on the result.</param>
        /// <returns>A failed result with <see cref="ArticleRetrievalResult.SessionReusable"/> false.</returns>
        private ArticleRetrievalResult MarkUnhealthy(ArticleRetrievalKind kind, int? code, string reason)
        {
            _unhealthy = true;
            State = NntpSessionState.Retiring;
            return ArticleRetrievalResult.Failed(kind, code, reason, sessionReusable: false);
        }

        /// <summary>Records whether <paramref name="reusable"/> may return to the pool and returns the matching failure.</summary>
        /// <param name="kind">Classification stored on the result.</param>
        /// <param name="code">Status code when one was parsed; otherwise null.</param>
        /// <param name="reason">Diagnostic text stored on the result.</param>
        /// <param name="reusable">
        /// <see langword="false"/> sets <see cref="_unhealthy"/> and <see cref="NntpSessionState.Retiring"/>.
        /// <see langword="true"/> leaves the session <see cref="NntpSessionState.Ready"/>.
        /// </param>
        /// <returns>A failed result whose reusable flag is <paramref name="reusable"/>.</returns>
        private ArticleRetrievalResult FailClosed(ArticleRetrievalKind kind, int? code, string reason, bool reusable)
        {
            _unhealthy = !reusable;
            State = reusable ? NntpSessionState.Ready : NntpSessionState.Retiring;
            return ArticleRetrievalResult.Failed(kind, code, reason, reusable);
        }

        /// <summary>
        /// Builds <c>{Backbone}/{account}[{connectionNumber:000}/{MaxSessions}]</c>.
        /// The account is the trimmed username, or <c>-</c> when the username is missing.
        /// </summary>
        /// <param name="provider">Provider whose backbone, username, and max sessions are formatted.</param>
        /// <param name="connectionNumber">One-based slot written as three digits.</param>
        /// <returns>The wire-log prefix stored for the session lifetime.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
        private static string FormatWireIdentity(BackFillerProviderDefinition provider, int connectionNumber)
        {
            ArgumentNullException.ThrowIfNull(provider);
            var account = string.IsNullOrWhiteSpace(provider.Username) ? "-" : provider.Username.Trim();
            return $"{provider.Backbone}/{account}[{connectionNumber:000}/{provider.MaxSessions}]";
        }

        /// <summary>
        /// Signals that the article callback failed after the payload was complete.
        /// Caught so the session is not marked unhealthy. The original exception is rethrown after <c>_busy</c> is released.
        /// </summary>
        private sealed class PayloadConsumerException : Exception
        {
        }
    }
}
