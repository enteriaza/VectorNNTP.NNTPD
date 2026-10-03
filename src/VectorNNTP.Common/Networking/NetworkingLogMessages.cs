using System.Net;

namespace VectorNNTP.Common.Networking
{
    /// <summary>Source-generated bind-address, TLS certificate, listener, and connection-acceptance log messages.</summary>
    internal static partial class NetworkingLogMessages
    {
        /// <summary>Logs event 1400 (warning): a bind-address entry was not an IP literal and was skipped.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 1400,
            Level = LogLevel.Warning,
            Message = "Ignoring non-IP BindAddress entry during resolution (validation should have failed earlier)")]
        internal static partial void IgnoringNonIpBindAddress(ILogger logger);

        /// <summary>Logs event 1401 (information): an explicit bind address failed DNS eligibility and was omitted.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Address">DNS text of the omitted address.</param>
        [LoggerMessage(
            EventId = 1401,
            Level = LogLevel.Information,
            Message = "BindAddress entry {Address} is not eligible for DNS publication and will be omitted from the reconciled set")]
        internal static partial void BindAddressNotEligibleForDns(ILogger logger, string Address);

        /// <summary>Logs event 1402 (information): bind-address resolution finished.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="TotalCount">Eligible addresses after deduplication.</param>
        /// <param name="IPv4Count">Eligible IPv4 addresses.</param>
        /// <param name="IPv6Count">Eligible IPv6 addresses.</param>
        [LoggerMessage(
            EventId = 1402,
            Level = LogLevel.Information,
            Message = "Resolved {TotalCount} eligible bind address(es) for DNS ({IPv4Count} IPv4, {IPv6Count} IPv6)")]
        internal static partial void BindAddressesResolved(ILogger logger, int TotalCount, int IPv4Count, int IPv6Count);

        /// <summary>Logs event 1403 (information): a TLS certificate context was published.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Generation">Process-wide holder generation assigned at PFX load.</param>
        [LoggerMessage(
            EventId = 1403,
            Level = LogLevel.Information,
            Message = "TLS certificate context published (generation={Generation})")]
        internal static partial void TlsCertificateContextPublished(ILogger logger, int Generation);

        /// <summary>Logs event 1404 (information): a plain listener accepted a connection that carried a PROXY preamble.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="TcpPeer">TCP remote endpoint text.</param>
        /// <param name="Client">Client address taken from the PROXY preamble.</param>
        [LoggerMessage(
            EventId = 1404,
            Level = LogLevel.Information,
            Message = "Proxy connection accepted from {TcpPeer}; proxy {Client}")]
        internal static partial void ProxyConnectionAccepted(ILogger logger, string TcpPeer, string Client);

        /// <summary>Logs event 1405 (information): a plain listener accepted a connection with no PROXY client.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="TcpPeer">TCP remote endpoint text.</param>
        [LoggerMessage(
            EventId = 1405,
            Level = LogLevel.Information,
            Message = "Plain connection accepted from {TcpPeer}")]
        internal static partial void PlainConnectionAccepted(ILogger logger, string TcpPeer);

        /// <summary>Logs event 1406 (information): a TLS listener accepted a proxied connection after the handshake.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="TcpPeer">TCP remote endpoint text.</param>
        /// <param name="Client">Client address taken from the PROXY preamble.</param>
        /// <param name="TlsVersion">Negotiated TLS protocol version text.</param>
        /// <param name="Cipher">Negotiated cipher suite text.</param>
        [LoggerMessage(
            EventId = 1406,
            Level = LogLevel.Information,
            Message = "TLS/Proxy connection accepted from {TcpPeer}; proxy {Client} (TlsVersion={TlsVersion}, Cipher={Cipher})")]
        internal static partial void TlsProxyConnectionAccepted(
            ILogger logger,
            string TcpPeer,
            string Client,
            string TlsVersion,
            string Cipher);

        /// <summary>Logs event 1407 (information): a TLS listener accepted a direct connection after the handshake.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="TcpPeer">TCP remote endpoint text.</param>
        /// <param name="TlsVersion">Negotiated TLS protocol version text.</param>
        /// <param name="Cipher">Negotiated cipher suite text.</param>
        [LoggerMessage(
            EventId = 1407,
            Level = LogLevel.Information,
            Message = "TLS connection accepted from {TcpPeer} (TlsVersion={TlsVersion}, Cipher={Cipher})")]
        internal static partial void TlsConnectionAccepted(
            ILogger logger,
            string TcpPeer,
            string TlsVersion,
            string Cipher);

        /// <summary>Logs event 1408 (information): plain NNTP listeners have started.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="ListenerCount">Number of plain listen sockets started.</param>
        /// <param name="Port">TCP port those sockets share.</param>
        [LoggerMessage(
            EventId = 1408,
            Level = LogLevel.Information,
            Message = "Plain NNTP listeners started ({ListenerCount}) on port {Port}")]
        internal static partial void PlainListenersStarted(ILogger logger, int ListenerCount, int Port);

        /// <summary>Logs event 1409 (debug): completing an accepted plain connection failed while the listener was stopping.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Completion failure attached to the log event.</param>
        [LoggerMessage(
            EventId = 1409,
            Level = LogLevel.Debug,
            Message = "Error completing plain connection during stop")]
        internal static partial void PlainConnectionCompleteError(ILogger logger, Exception exception);

        /// <summary>Logs event 1410 (debug): a plain accept was dropped because the remote endpoint was unavailable.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 1410,
            Level = LogLevel.Debug,
            Message = "Plain accept discarded: remote endpoint unavailable")]
        internal static partial void PlainAcceptDiscarded(ILogger logger);

        /// <summary>Logs event 1411 (information): a trusted proxy peer sent a PROXY preamble that was rejected.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Preamble failure attached to the log event.</param>
        /// <param name="TcpPeer">TCP remote endpoint of the trusted proxy peer.</param>
        [LoggerMessage(
            EventId = 1411,
            Level = LogLevel.Information,
            Message = "Rejected plain connection from trusted proxy peer {TcpPeer}: invalid PROXY preamble")]
        internal static partial void PlainProxyPreambleRejected(ILogger logger, Exception exception, IPEndPoint TcpPeer);

        /// <summary>Logs event 1412 (information): TLS listen is idle because the TLS bind port is 0.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 1412,
            Level = LogLevel.Information,
            Message = "TLS disabled (BindPortTls=0); TLS NNTP listener idle")]
        internal static partial void TlsListenerIdle(ILogger logger);

        /// <summary>Logs event 1413 (information): TLS NNTP listeners have started.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="ListenerCount">Number of TLS listen sockets started.</param>
        /// <param name="Port">TCP port those sockets share.</param>
        [LoggerMessage(
            EventId = 1413,
            Level = LogLevel.Information,
            Message = "TLS NNTP listeners started ({ListenerCount}) on port {Port}")]
        internal static partial void TlsListenersStarted(ILogger logger, int ListenerCount, int Port);

        /// <summary>Logs event 1414 (debug): completing an accepted TLS connection failed while the listener was stopping.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Completion failure attached to the log event.</param>
        [LoggerMessage(
            EventId = 1414,
            Level = LogLevel.Debug,
            Message = "Error completing TLS connection during stop")]
        internal static partial void TlsConnectionCompleteError(ILogger logger, Exception exception);

        /// <summary>Logs event 1415 (debug): a TLS accept was dropped because the remote endpoint was unavailable.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 1415,
            Level = LogLevel.Debug,
            Message = "TLS accept discarded: remote endpoint unavailable")]
        internal static partial void TlsAcceptDiscarded(ILogger logger);

        /// <summary>Logs event 1416 (information): a trusted proxy peer sent a PROXY preamble that was rejected on the TLS listener.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Preamble failure attached to the log event.</param>
        /// <param name="TcpPeer">TCP remote endpoint of the trusted proxy peer.</param>
        [LoggerMessage(
            EventId = 1416,
            Level = LogLevel.Information,
            Message = "Rejected TLS connection from trusted proxy peer {TcpPeer}: invalid PROXY preamble")]
        internal static partial void TlsProxyPreambleRejected(ILogger logger, Exception exception, IPEndPoint TcpPeer);

        /// <summary>Logs event 1417 (debug): TLS handshake or connection setup failed. The listener keeps accepting.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Handshake or setup failure attached to the log event.</param>
        [LoggerMessage(
            EventId = 1417,
            Level = LogLevel.Debug,
            Message = "TLS handshake or connection setup failed; listener continues")]
        internal static partial void TlsHandshakeOrSetupFailed(ILogger logger, Exception exception);

        /// <summary>Logs event 1418 (information): one NNTP listen socket has started.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="EndPoint">Local endpoint the socket bound.</param>
        /// <param name="DualMode">Whether the IPv6 socket also accepts IPv4-mapped connections.</param>
        [LoggerMessage(
            EventId = 1418,
            Level = LogLevel.Information,
            Message = "NNTP listener started on {EndPoint} (dualMode={DualMode})")]
        internal static partial void ListenerStarted(ILogger logger, IPEndPoint EndPoint, bool DualMode);

        /// <summary>Logs event 1419 (debug): the accept loop ended with an error while the listener was stopping.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Accept-loop failure attached to the log event.</param>
        [LoggerMessage(
            EventId = 1419,
            Level = LogLevel.Debug,
            Message = "Accept loop ended with an error during stop")]
        internal static partial void AcceptLoopStopError(ILogger logger, Exception exception);

        /// <summary>Logs event 1420 (information): one NNTP listen socket has stopped.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Address">Local address the socket was bound to.</param>
        /// <param name="Port">Local TCP port the socket was bound to.</param>
        [LoggerMessage(
            EventId = 1420,
            Level = LogLevel.Information,
            Message = "NNTP listener stopped ({Address}/{Port})")]
        internal static partial void ListenerStopped(ILogger logger, IPAddress Address, int Port);

        /// <summary>Logs event 1421 (debug): accept was interrupted because the listener was shutting down.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Interruption attached to the log event.</param>
        [LoggerMessage(
            EventId = 1421,
            Level = LogLevel.Debug,
            Message = "Accept interrupted during listener shutdown")]
        internal static partial void AcceptInterruptedDuringShutdown(ILogger logger, Exception exception);

        /// <summary>Logs event 1422 (error): accept failed and the listener continues.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Accept failure attached to the log event.</param>
        /// <param name="EndPoint">Local endpoint whose accept failed.</param>
        [LoggerMessage(
            EventId = 1422,
            Level = LogLevel.Error,
            Message = "Accept failed on {EndPoint}; listener continues")]
        internal static partial void AcceptFailed(ILogger logger, Exception exception, IPEndPoint EndPoint);

        /// <summary>Logs event 1423 (debug): the accepted-connection handler threw. The listener continues.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Handler failure attached to the log event.</param>
        [LoggerMessage(
            EventId = 1423,
            Level = LogLevel.Debug,
            Message = "Accepted connection handler failed; listener continues")]
        internal static partial void AcceptedHandlerFailed(ILogger logger, Exception exception);

        /// <summary>Logs event 1424 (error): binding a listener failed and listener startup cannot continue.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Bind failure attached to the log event.</param>
        /// <param name="ListenerType">Listener kind text supplied by the caller, such as plain or TLS.</param>
        /// <param name="Endpoint">Endpoint text the bind targeted.</param>
        /// <param name="Port">TCP port the bind targeted.</param>
        [LoggerMessage(
            EventId = 1424,
            Level = LogLevel.Error,
            Message = "Failed to bind {ListenerType} NNTP listener to {Endpoint} (port {Port}); listener startup cannot continue")]
        internal static partial void ListenerBindFailed(
            ILogger logger,
            Exception exception,
            string ListenerType,
            string Endpoint,
            int Port);

        /// <summary>Logs event 1425 (information): a TCP connection disconnected. This method does not reconnect it.</summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="Remote">Remote endpoint text.</param>
        /// <param name="Local">Local endpoint text.</param>
        /// <param name="Reason">Disconnect reason text supplied by the caller.</param>
        [LoggerMessage(
            EventId = 1425,
            Level = LogLevel.Information,
            Message = "TCP connection disconnected: remote={Remote} local={Local} reason={Reason}")]
        internal static partial void TcpConnectionDisconnected(
            ILogger logger,
            string Remote,
            string Local,
            string Reason);
    }
}
