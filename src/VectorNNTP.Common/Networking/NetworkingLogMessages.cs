using System.Net;

namespace VectorNNTP.Common.Networking
{
    /// <summary>Source-generated bind-address, TLS certificate, listener, and connection-acceptance log messages.</summary>
    internal static partial class NetworkingLogMessages
    {
        [LoggerMessage(
            EventId = 1400,
            Level = LogLevel.Warning,
            Message = "Ignoring non-IP BindAddress entry during resolution (validation should have failed earlier)")]
        internal static partial void IgnoringNonIpBindAddress(ILogger logger);

        [LoggerMessage(
            EventId = 1401,
            Level = LogLevel.Information,
            Message = "BindAddress entry {Address} is not eligible for DNS publication and will be omitted from the reconciled set")]
        internal static partial void BindAddressNotEligibleForDns(ILogger logger, string Address);

        [LoggerMessage(
            EventId = 1402,
            Level = LogLevel.Information,
            Message = "Resolved {TotalCount} eligible bind address(es) for DNS ({IPv4Count} IPv4, {IPv6Count} IPv6)")]
        internal static partial void BindAddressesResolved(ILogger logger, int TotalCount, int IPv4Count, int IPv6Count);

        [LoggerMessage(
            EventId = 1403,
            Level = LogLevel.Information,
            Message = "TLS certificate context published (generation={Generation})")]
        internal static partial void TlsCertificateContextPublished(ILogger logger, int Generation);

        [LoggerMessage(
            EventId = 1404,
            Level = LogLevel.Information,
            Message = "Proxy connection accepted from {TcpPeer}; proxy {Client}")]
        internal static partial void ProxyConnectionAccepted(ILogger logger, string TcpPeer, string Client);

        [LoggerMessage(
            EventId = 1405,
            Level = LogLevel.Information,
            Message = "Plain connection accepted from {TcpPeer}")]
        internal static partial void PlainConnectionAccepted(ILogger logger, string TcpPeer);

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

        [LoggerMessage(
            EventId = 1407,
            Level = LogLevel.Information,
            Message = "TLS connection accepted from {TcpPeer} (TlsVersion={TlsVersion}, Cipher={Cipher})")]
        internal static partial void TlsConnectionAccepted(
            ILogger logger,
            string TcpPeer,
            string TlsVersion,
            string Cipher);

        [LoggerMessage(
            EventId = 1408,
            Level = LogLevel.Information,
            Message = "Plain NNTP listeners started ({ListenerCount}) on port {Port}")]
        internal static partial void PlainListenersStarted(ILogger logger, int ListenerCount, int Port);

        [LoggerMessage(
            EventId = 1409,
            Level = LogLevel.Debug,
            Message = "Error completing plain connection during stop")]
        internal static partial void PlainConnectionCompleteError(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 1410,
            Level = LogLevel.Debug,
            Message = "Plain accept discarded: remote endpoint unavailable")]
        internal static partial void PlainAcceptDiscarded(ILogger logger);

        [LoggerMessage(
            EventId = 1411,
            Level = LogLevel.Information,
            Message = "Rejected plain connection from trusted proxy peer {TcpPeer}: invalid PROXY preamble")]
        internal static partial void PlainProxyPreambleRejected(ILogger logger, Exception exception, IPEndPoint TcpPeer);

        [LoggerMessage(
            EventId = 1412,
            Level = LogLevel.Information,
            Message = "TLS disabled (BindPortTls=0); TLS NNTP listener idle")]
        internal static partial void TlsListenerIdle(ILogger logger);

        [LoggerMessage(
            EventId = 1413,
            Level = LogLevel.Information,
            Message = "TLS NNTP listeners started ({ListenerCount}) on port {Port}")]
        internal static partial void TlsListenersStarted(ILogger logger, int ListenerCount, int Port);

        [LoggerMessage(
            EventId = 1414,
            Level = LogLevel.Debug,
            Message = "Error completing TLS connection during stop")]
        internal static partial void TlsConnectionCompleteError(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 1415,
            Level = LogLevel.Debug,
            Message = "TLS accept discarded: remote endpoint unavailable")]
        internal static partial void TlsAcceptDiscarded(ILogger logger);

        [LoggerMessage(
            EventId = 1416,
            Level = LogLevel.Information,
            Message = "Rejected TLS connection from trusted proxy peer {TcpPeer}: invalid PROXY preamble")]
        internal static partial void TlsProxyPreambleRejected(ILogger logger, Exception exception, IPEndPoint TcpPeer);

        [LoggerMessage(
            EventId = 1417,
            Level = LogLevel.Debug,
            Message = "TLS handshake or connection setup failed; listener continues")]
        internal static partial void TlsHandshakeOrSetupFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 1418,
            Level = LogLevel.Information,
            Message = "NNTP listener started on {EndPoint} (dualMode={DualMode})")]
        internal static partial void ListenerStarted(ILogger logger, IPEndPoint EndPoint, bool DualMode);

        [LoggerMessage(
            EventId = 1419,
            Level = LogLevel.Debug,
            Message = "Accept loop ended with an error during stop")]
        internal static partial void AcceptLoopStopError(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 1420,
            Level = LogLevel.Information,
            Message = "NNTP listener stopped ({Address}/{Port})")]
        internal static partial void ListenerStopped(ILogger logger, IPAddress Address, int Port);

        [LoggerMessage(
            EventId = 1421,
            Level = LogLevel.Debug,
            Message = "Accept interrupted during listener shutdown")]
        internal static partial void AcceptInterruptedDuringShutdown(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 1422,
            Level = LogLevel.Error,
            Message = "Accept failed on {EndPoint}; listener continues")]
        internal static partial void AcceptFailed(ILogger logger, Exception exception, IPEndPoint EndPoint);

        [LoggerMessage(
            EventId = 1423,
            Level = LogLevel.Debug,
            Message = "Accepted connection handler failed; listener continues")]
        internal static partial void AcceptedHandlerFailed(ILogger logger, Exception exception);

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
